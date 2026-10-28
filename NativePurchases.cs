using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace BotBuyPatch;

internal sealed class NativePurchases : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Price(nint services, ushort definition, nint item);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte CanBuy(nint pawn, byte message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ItemSystem();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint DefaultItem(nint system, ushort definition, int quality, int unknown);
    internal readonly record struct Offer(int Slot, int Price, ushort Definition);
    private readonly BotBuyPatch owner;
    private readonly MemoryFunctionWithReturn<nint, int, byte, byte, int> purchase;
    private readonly MemoryFunctionWithReturn<nint, int, int, nint> loadout;
    private readonly Price price;
    private readonly CanBuy canBuy;
    private readonly ItemSystem itemSystem;
    private readonly DefaultItem defaultItem;
    private readonly Dictionary<(nint Inventory, int Team, int Slot), nint> extraItems = new();
    private readonly HashSet<(nint Inventory, int Team)> inventories = new();
    private nint permitted, captureServices, captured;
    private ushort captureDefinition;
    private bool purchaseHooked, loadoutHooked, giveHooked;

    internal NativePurchases(BotBuyPatch owner)
    {
        this.owner = owner;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64
            || (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()))
            throw new PlatformNotSupportedException("Native BotBuy requires Windows or Linux x64 gamedata.");
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(owner.ModuleDirectory, "native-purchases.json")));
        bool windows = OperatingSystem.IsWindows();
        var profile = data.RootElement.GetProperty(windows ? "windows" : "linux");
        string modulePath = windows ? "/csgo/bin/win64/server.dll" : "/csgo/bin/linuxsteamrt64/libserver.so";
        using var process = Process.GetCurrentProcess();
        var module = process.Modules.Cast<ProcessModule>().Single(m => m.FileName.Replace('\\', '/').EndsWith(
            modulePath, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        using var file = File.OpenRead(module.FileName);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(profile.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"BotBuy gamedata does not match {module.ModuleName}; update native-purchases.json.");
        var functions = profile.GetProperty("functions");
        (nint Address, string Signature) Resolve(string name)
        {
            var entry = functions.GetProperty(name);
            int rva = entry.GetProperty("rva").GetInt32();
            byte[] expected = Convert.FromHexString(entry.GetProperty("bytes").GetString()!);
            if (rva <= 0 || expected.Length < 16 || rva > module.ModuleMemorySize - expected.Length)
                throw new InvalidDataException($"BotBuy entry outside {module.ModuleName}: {name}");
            string signature = string.Join(' ', expected.Select(b => b.ToString("X2")));
            nint address = module.BaseAddress + rva;
            // A cached CSS function may retain its detour during reload. Only
            // accept its audited jump span with unchanged undisplaced bytes.
            var cache = typeof(BaseMemoryFunction).GetField("_createdFunctions", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) as Dictionary<string, nint>;
            int skip = 0;
            if (cache?.TryGetValue(signature, out nint cached) == true && cached != 0
                && entry.TryGetProperty("hookBytes", out var hookBytes) && Marshal.ReadByte(address) == 0xE9)
            {
                skip = hookBytes.GetInt32();
                if (skip < 5 || skip >= expected.Length) throw new InvalidDataException($"Invalid hook span: {name}");
                for (int i = 5; i < skip; i++)
                    if (Marshal.ReadByte(address, i) != 0x90) throw new InvalidOperationException($"Unexpected detour padding: {name}");
            }
            byte[] actual = new byte[expected.Length - skip];
            Marshal.Copy(address + skip, actual, 0, actual.Length);
            if (!actual.AsSpan().SequenceEqual(expected.AsSpan(skip)))
                throw new InvalidOperationException($"BotBuy native entry changed: {name}");
            return (address, signature);
        }
        price = Marshal.GetDelegateForFunctionPointer<Price>(Resolve("price").Address);
        canBuy = Marshal.GetDelegateForFunctionPointer<CanBuy>(Resolve("canBuy").Address);
        itemSystem = Marshal.GetDelegateForFunctionPointer<ItemSystem>(Resolve("itemSystem").Address);
        defaultItem = Marshal.GetDelegateForFunctionPointer<DefaultItem>(Resolve("defaultItem").Address);
        purchase = new(Resolve("purchase").Signature);
        loadout = new(Resolve("loadout").Signature);
        try
        {
            purchase.Hook(OnPurchase, HookMode.Pre); purchaseHooked = true;
            loadout.Hook(OnLoadout, HookMode.Post); loadoutHooked = true;
            VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveItem, HookMode.Post); giveHooked = true;
        }
        catch { Dispose(); throw; }
    }

    internal void ClearInventory(CCSPlayerController player)
    {
        if (player.InventoryServices is not { } inventory) return;
        foreach (var key in extraItems.Keys.Where(k => k.Inventory == inventory.Handle).ToArray()) extraItems.Remove(key);
        inventories.RemoveWhere(k => k.Inventory == inventory.Handle);
    }

    internal void ClearInventories() { extraItems.Clear(); inventories.Clear(); }

    private void PrepareInventory(CCSPlayerController player)
    {
        var inventory = player.InventoryServices!;
        var key = (inventory.Handle, (int)player.Team);
        if (!player.IsBot || inventories.Contains(key)) return;
        var definitions = new HashSet<ushort>();
        var empty = new Queue<int>();
        for (int slot = 2; slot < 58; slot++)
        {
            nint view = loadout.Invoke(key.Handle, key.Item2, slot, bypasshook: true);
            if (view == 0 || !new CEconItemView(view).Initialized) empty.Enqueue(slot);
            else definitions.Add(new CEconItemView(view).ItemDefinitionIndex);
        }
        // Assign all missing weapons in definition order, so the same inventory
        // has the same slots after a reload, including native refund records.
        foreach (ushort definition in BuyPolicy.Shop(player.TeamNum == 3))
        {
            if (definitions.Contains(definition)) continue;
            nint view = defaultItem(itemSystem(), definition, 0, 0);
            if (view == 0 || !new CEconItemView(view).Initialized)
                throw new InvalidOperationException($"No native default item for {definition}");
            if (empty.Count == 0) throw new InvalidOperationException("No unused bot loadout slot");
            extraItems.Add((key.Handle, key.Item2, empty.Dequeue()), view);
        }
        inventories.Add(key);
    }

    internal bool Allowed(CCSPlayerController player) => player.PlayerPawn.Value is { IsValid: true } pawn && canBuy(pawn.Handle, 0) != 0;
    internal ushort Definition(CCSPlayerController player, int slot)
    {
        if ((uint)slot >= 58 || player.InventoryServices is not { } inventory) return 0;
        nint view = loadout.Invoke(inventory.Handle, (int)player.Team, slot, bypasshook: false);
        return view == 0 ? (ushort)0 : new CEconItemView(view).ItemDefinitionIndex;
    }

    internal Offer? Quote(CCSPlayerController player, ushort definition)
    {
        if (player.InventoryServices is not { } inventory || player.PlayerPawn.Value?.WeaponServices is not { } weapons) return null;
        PrepareInventory(player);
        for (int slot = 2; slot < 58; slot++)
        {
            nint view = loadout.Invoke(inventory.Handle, (int)player.Team, slot, bypasshook: false);
            if (view == 0 || !new CEconItemView(view).Initialized)
            {
                continue;
            }
            if (new CEconItemView(view).ItemDefinitionIndex == definition)
                return MakeOffer(slot, view);
        }
        return null;

        Offer? MakeOffer(int slot, nint view)
        {
            int cost = price(weapons.Handle, definition, view);
            return cost < 0 ? null : new Offer(slot, cost, definition);
        }
    }

    internal int Buy(CCSPlayerController player, Offer offer, bool drop, out CBasePlayerWeapon? weapon)
    {
        weapon = null;
        var pawn = player.PlayerPawn.Value;
        if (pawn?.BuyServices is not { } services || pawn.ItemServices is not { } items) return 3;
        if (permitted != 0) throw new InvalidOperationException("Reentrant BotBuy transaction");
        permitted = services.Handle;
        captureServices = drop ? items.Handle : 0;
        captureDefinition = offer.Definition;
        captured = 0;
        try
        {
            int result = purchase.Invoke(services.Handle, offer.Slot, drop ? (byte)0 : (byte)1, drop ? (byte)1 : (byte)0, bypasshook: false);
            if (drop && result == 0)
            {
                if (captured == 0) throw new InvalidOperationException("Native buy-and-drop succeeded without an item");
                weapon = new CBasePlayerWeapon(captured);
            }
            return result;
        }
        finally { permitted = captureServices = captured = 0; }
    }

    private HookResult OnPurchase(DynamicHook hook)
    {
        nint service = hook.GetParam<nint>(0);
        if (service == permitted) return HookResult.Continue;
        var pawn = new CCSPlayer_BuyServices(service).Pawn.Value?.As<CCSPlayerPawn>();
        var player = pawn?.Controller.Value?.As<CCSPlayerController>();
        if (player is not { IsValid: true } || player.PlayerPawn.Raw != pawn?.EntityHandle.Raw) return HookResult.Continue;
        int? result = owner.Purchase(player, hook.GetParam<int>(1));
        if (!result.HasValue) return HookResult.Continue;
        hook.SetReturn(result.Value);
        return HookResult.Handled;
    }

    private HookResult OnLoadout(DynamicHook hook)
    {
        nint original = hook.GetReturn<nint>();
        if ((original == 0 || !new CEconItemView(original).Initialized)
            && extraItems.TryGetValue((hook.GetParam<nint>(0), hook.GetParam<int>(1), hook.GetParam<int>(2)), out nint view))
            hook.SetReturn(view);
        return HookResult.Continue;
    }

    private HookResult OnGiveItem(DynamicHook hook)
    {
        if (captureServices != 0 && hook.GetParam<nint>(0) == captureServices)
        {
            nint view = hook.GetReturn<nint>();
            if (view != 0 && new CBasePlayerWeapon(view).AttributeManager.Item.ItemDefinitionIndex == captureDefinition) captured = view;
        }
        return HookResult.Continue;
    }

    public void Dispose()
    {
        try { if (purchaseHooked) { purchase.Unhook(OnPurchase, HookMode.Pre); purchaseHooked = false; } }
        finally
        {
            try { if (loadoutHooked) { loadout.Unhook(OnLoadout, HookMode.Post); loadoutHooked = false; } }
            finally
            {
                if (giveHooked) { VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveItem, HookMode.Post); giveHooked = false; }
                ClearInventories();
            }
        }
    }
}
