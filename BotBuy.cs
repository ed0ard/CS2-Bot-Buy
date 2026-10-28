using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Cvars;

namespace BotBuyPatch;

public sealed class BotBuyPatch : BasePlugin
{
    public override string ModuleName        => "BotBuyPatch";
    public override string ModuleVersion     => "1.1.0";
    public override string ModuleAuthor      => "ed0ard";
    public override string ModuleDescription => "Enable bots to take more buy options";

    private sealed class RoundBuy
    {
        internal int Money, Armor, Donations;
        internal bool Initialized, PistolLocked, PrimaryLocked, Received;
        internal readonly Dictionary<ushort, ushort> Choices = new();
    }
    private readonly Dictionary<uint, RoundBuy> rounds = new();
    private readonly Dictionary<CsTeam, ushort> force = new();
    private NativePurchases native = null!;
    private int generation;
    private bool roundOpen, freezeEnded, queued;

    public override void Load(bool hotReload)
    {
        native = new NativePurchases(this);
        RegisterListener<Listeners.OnMapStart>(_ => { Reset(false); native.ClearInventories(); });
        if (hotReload)
        {
            roundOpen = true;
            // Do not repeat spawn purchases on reload.
            foreach (var player in Utilities.GetPlayers().Where(Managed))
                rounds[player.PlayerPawn.Raw] = new RoundBuy { Initialized = true, Money = Money(player), Armor = player.PlayerPawn.Value!.ArmorValue };
        }
    }
    public override void Unload(bool hotReload) { Reset(false); native?.Dispose(); }
    private void Reset(bool open)
    {
        generation++;
        roundOpen = open;
        freezeEnded = queued = false;
        rounds.Clear();
        force.Clear();
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        Reset(true);
        var bots = Utilities.GetPlayers().Where(Managed).ToList();
        foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
        {
            var members = bots.Where(p => p.Team == team).ToList();
            if (members.Count > 0 && members.All(p => Money(p) > 1000 && Money(p) < 2800))
                force[team] = BuyPolicy.Force(team == CsTeam.CounterTerrorist, Random.Shared.NextSingle());
        }
        foreach (var player in bots) State(player);
        Queue();
        return HookResult.Continue;
    }
    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info) { Reset(false); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info) { Queue(); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnEnterBuyzone(EventEnterBuyzone @event, GameEventInfo info) { if (@event.Canbuy) Queue(); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnItemPurchase(EventItemPurchase @event, GameEventInfo info) { if (freezeEnded) Queue(); return HookResult.Continue; }
    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player) native.ClearInventory(player);
        return HookResult.Continue;
    }
    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (!Enabled()) return HookResult.Continue;
        Server.ExecuteCommand(IsSecondToLastRoundOfHalf() ? "bot_eco_limit 0" : "bot_eco_limit 2800");
        freezeEnded = true;
        Queue();
        return HookResult.Continue;
    }

    private void Queue()
    {
        if (!roundOpen || queued) return;
        queued = true;
        int round = generation;
        Server.NextFrame(() =>
        {
            if (round != generation) return;
            queued = false;
            if (!roundOpen || !Enabled()) return;
            var players = Utilities.GetPlayers().Where(Usable).ToList();
            foreach (var player in players.Where(Managed)) Initialize(player);
            if (!freezeEnded) return;
            foreach (var player in players.Where(Managed))
            {
                var state = State(player);
                if (player.Team == CsTeam.CounterTerrorist && (state.Money >= 2800 || IsFirstRoundOfHalf() && Money(player) == 500)
                    && !Items(player).HasDefuser) Buy(player, 55);
            }
            Assist(players);
        });
    }
    private RoundBuy State(CCSPlayerController player)
    {
        if (!rounds.TryGetValue(player.PlayerPawn.Raw, out var state))
            rounds.Add(player.PlayerPawn.Raw, state = new RoundBuy { Money = Money(player), Armor = player.PlayerPawn.Value!.ArmorValue });
        return state;
    }

    private void Initialize(CCSPlayerController player)
    {
        var state = State(player);
        if (state.Initialized || !native.Allowed(player)) return;
        state.Initialized = true;
        bool ct = player.Team == CsTeam.CounterTerrorist;
        // This is the free spawn sidearm, not a purchase or a refund.
        var pistol = Weapons(player).FirstOrDefault(w => w.AttributeManager.Item.ItemDefinitionIndex == 32);
        if (ct && pistol != null && Random.Shared.NextSingle() < .8f)
        {
            player.RemoveItemByDesignerName("weapon_hkp2000");
            if (player.GiveNamedItem("weapon_usp_silencer") == 0)
                throw new InvalidOperationException("Could not assign the bot's starting USP-S");
        }
        if (IsFirstRoundOfHalf())
        {
            ushort item = BuyPolicy.Pistol(ct, state.Money, Random.Shared.NextSingle());
            if (item != 0)
            {
                state.PistolLocked = true;
                state.PrimaryLocked = state.Money is 800 or 1000;
                if (state.Money == 10000) Buy(player, 51);
                Buy(player, item);
            }
        }
        else if (force.TryGetValue(player.Team, out ushort forced) && forced != 0)
        {
            if (!BuyPolicy.Primary(forced) || Primary(player) == null) Buy(player, forced);
        }
    }

    internal int? Purchase(CCSPlayerController player, int slot)
    {
        if (!roundOpen || !Managed(player) || !Enabled()) return null;
        Initialize(player);
        var state = State(player);
        ushort requested = native.Definition(player, slot);
        if (requested == 0) return null;
        if (BuyPolicy.Primary(requested) && (state.PrimaryLocked || Primary(player) != null)) return 3;
        if (BuyPolicy.Pistol(requested) && state.PistolLocked) return 3;
        if (requested is 50 or 51 && !IsFirstRoundOfHalf() && state.Armor is > 40 and <= 99) return 3;
        if (requested == 55 && state.Money < 2800 && !(IsFirstRoundOfHalf() && Money(player) == 500)) return 3;
        if (!BuyPolicy.Primary(requested)) return null;
        if (!state.Choices.TryGetValue(requested, out ushort selected))
        {
            bool ct = player.Team == CsTeam.CounterTerrorist;
            selected = BuyPolicy.Replace(requested, ct, Random.Shared.NextSingle(), ConVar.Find("sv_gravity")?.GetPrimitiveValue<float>() == 230f);
            var quote = native.Quote(player, selected);
            if (!IsFirstRoundOfHalf() && quote.HasValue && Money(player) - quote.Value.Price >= 5200)
            {
                ushort upgrade = BuyPolicy.Advantage(ct, Random.Shared.NextSingle());
                if (upgrade != 0) selected = upgrade;
            }
            state.Choices.Add(requested, selected);
        }
        var offer = native.Quote(player, selected);
        // Preserve the original candidate if its replacement is unaffordable.
        if (!offer.HasValue || offer.Value.Price > Money(player)) return null;
        int result = native.Buy(player, offer.Value, false, out _);
        if (result == 0 && BuyPolicy.Pistol(selected)) state.PrimaryLocked = state.PistolLocked = true;
        return result;
    }
    private bool Buy(CCSPlayerController player, ushort definition)
        => native.Quote(player, definition) is { } offer && native.Buy(player, offer, false, out _) == 0;

    private void Assist(List<CCSPlayerController> players)
    {
        if (IsFirstRoundOfHalf()) return;
        foreach (var recipient in players.Where(p => Primary(p) == null).OrderBy(Money))
        {
            if (State(recipient).Received || !native.Allowed(recipient)) continue;
            foreach (var donor in players.Where(p => p != recipient && p.Team == recipient.Team && Managed(p) && Primary(p) != null).OrderByDescending(Money))
            {
                if (State(donor).Donations >= 3 || !native.Allowed(donor)) continue;
                ushort definition = donor.Team == CsTeam.Terrorist ? (ushort)7 : Random.Shared.Next(2) == 0 ? (ushort)60 : (ushort)16;
                var offer = native.Quote(donor, definition);
                var origin = recipient.PlayerPawn.Value?.AbsOrigin;
                if (origin == null || !offer.HasValue || !BuyPolicy.CanSponsor(Money(donor), Money(recipient), offer.Value.Price)) continue;
                if (native.Buy(donor, offer.Value, true, out var weapon) != 0) continue;
                State(donor).Donations++;
                State(recipient).Received = true;
                // Keep the donor's creation hooks, cosmetic attributes and owner.
                weapon!.OriginalOwnerXuidLow = (uint)donor.SteamID;
                weapon.OriginalOwnerXuidHigh = (uint)(donor.SteamID >> 32);
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidLow");
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidHigh");
                weapon.Teleport(new Vector(origin.X, origin.Y, origin.Z + 8), null, new Vector(0, 0, 0));
                foreach (var teammate in players.Where(p => !p.IsBot && p.Team == donor.Team))
                    teammate.PrintToChat($"{ChatColors.Green}{donor.PlayerName}{ChatColors.Yellow}: {recipient.PlayerName}, I dropped a weapon for ya");
                break;
            }
        }
        // Armor cannot be dropped. Fund the recipient's native purchase, then
        // charge its actual cost to the sponsor; do not synthesize free armor.
        foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
        {
            foreach (var target in players.Where(p => Managed(p) && p.Team == team && Primary(p) != null && p.PlayerPawn.Value!.ArmorValue == 0).OrderBy(_ => Random.Shared.Next()))
            {
                var donor = players.Where(p => Managed(p) && p.Team == team && State(p).Money >= 2800 && Money(p) >= (team == CsTeam.Terrorist ? 1000 : 650)).OrderByDescending(Money).FirstOrDefault();
                if (donor == null || !native.Allowed(donor) || !native.Allowed(target)) continue;
                var offer = native.Quote(target, Money(donor) >= 1000 ? (ushort)51 : (ushort)50);
                if (!offer.HasValue || offer.Value.Price > Money(donor)) continue;
                if (donor == target) { native.Buy(target, offer.Value, false, out _); continue; }
                int original = Money(target);
                target.InGameMoneyServices!.Account = original + offer.Value.Price;
                try
                {
                    if (native.Buy(target, offer.Value, false, out _) == 0)
                    {
                        int charged = original + offer.Value.Price - Money(target);
                        donor.InGameMoneyServices!.Account -= charged;
                        Utilities.SetStateChanged(donor, "CCSPlayerController", "m_pInGameMoneyServices");
                    }
                }
                finally
                {
                    target.InGameMoneyServices.Account = original;
                    Utilities.SetStateChanged(target, "CCSPlayerController", "m_pInGameMoneyServices");
                }
            }
        }
    }

    private static int Money(CCSPlayerController player) => player.InGameMoneyServices?.Account ?? 0;
    private static bool Usable(CCSPlayerController player) => player.IsValid && !player.IsHLTV && player.PawnIsAlive
        && player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist && player.PlayerPawn.Value is { IsValid: true } && player.InGameMoneyServices != null;
    private static bool Managed(CCSPlayerController player) => Usable(player) && player.IsBot && !player.ControllingBot && !player.HasBeenControlledByPlayerThisRound;
    private static IEnumerable<CBasePlayerWeapon> Weapons(CCSPlayerController player)
        => player.PlayerPawn.Value!.WeaponServices!.MyWeapons.Select(w => w.Value).OfType<CBasePlayerWeapon>().Where(w => w.IsValid);
    private static CBasePlayerWeapon? Primary(CCSPlayerController player)
        => Weapons(player).FirstOrDefault(w => BuyPolicy.Primary(w.AttributeManager.Item.ItemDefinitionIndex));
    private static CCSPlayer_ItemServices Items(CCSPlayerController player) => new(player.PlayerPawn.Value!.ItemServices!.Handle);
    private static bool Enabled() => Server.MapName is not ("aim_rush" or "rush_001")
        && string.IsNullOrEmpty(ConVar.Find("bot_loadout")?.StringValue)
        && Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules?.WarmupPeriod == false;
    private bool IsFirstRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            int otMaxRounds = ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6;

            if (maxRounds <= 0) maxRounds = 24;
            if (otMaxRounds <= 0) otMaxRounds = 6;

            int half = maxRounds / 2;
            int otHalf = otMaxRounds / 2;

            return played == 0
                || played == half
                || played == maxRounds
                || (played > maxRounds && (played - maxRounds) % otHalf == 0);
        }
        catch
        {
            return false;
        }
    }

    private bool IsSecondToLastRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            if (maxRounds <= 0) maxRounds = 24;
            int half = maxRounds / 2;

            return played == half - 2 || played == maxRounds - 2;
        }
        catch { return false; }
    }

}
