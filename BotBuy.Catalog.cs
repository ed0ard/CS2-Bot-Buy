using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotBuyPatch;

internal static class WeaponCatalog
{
    // M4A4 listed as $2900 until weapons.vdata / live buy menu is checked.
    private static readonly Dictionary<string, int> Prices = new()
    {
        ["item_kevlar"] = 650,
        ["item_assaultsuit"] = 1000,
        ["item_defuser"] = 400,
        ["weapon_taser"] = 200,

        ["weapon_glock"] = 0,
        ["weapon_hkp2000"] = 0,
        ["weapon_usp_silencer"] = 0,
        ["weapon_elite"] = 300,
        ["weapon_p250"] = 300,
        ["weapon_tec9"] = 500,
        ["weapon_fiveseven"] = 500,
        ["weapon_deagle"] = 700,
        ["weapon_cz75a"] = 500,
        ["weapon_revolver"] = 600,

        ["weapon_mac10"] = 1050,
        ["weapon_mp9"] = 1250,
        ["weapon_mp7"] = 1500,
        ["weapon_mp5sd"] = 1500,
        ["weapon_ump45"] = 1200,
        ["weapon_bizon"] = 1400,
        ["weapon_p90"] = 2350,

        ["weapon_nova"] = 1050,
        ["weapon_xm1014"] = 2000,
        ["weapon_sawedoff"] = 1100,
        ["weapon_mag7"] = 1300,

        ["weapon_galilar"] = 1800,
        ["weapon_ak47"] = 2700,
        ["weapon_sg556"] = 3000,
        ["weapon_famas"] = 1950,
        ["weapon_m4a1"] = 2900,
        ["weapon_m4a1_silencer"] = 2900,
        ["weapon_aug"] = 3300,

        ["weapon_ssg08"] = 1700,
        ["weapon_awp"] = 4750,
        ["weapon_scar20"] = 5000,
        ["weapon_g3sg1"] = 5000,

        ["weapon_negev"] = 1700,
        ["weapon_m249"] = 5200,
    };

    internal static readonly HashSet<string> Primaries = new()
    {
        "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_aug", "weapon_sg556",
        "weapon_galilar", "weapon_famas",
        "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1",
        "weapon_mac10", "weapon_mp9", "weapon_mp7", "weapon_mp5sd", "weapon_ump45", "weapon_bizon", "weapon_p90",
        "weapon_nova", "weapon_xm1014", "weapon_sawedoff", "weapon_mag7",
        "weapon_negev", "weapon_m249",
    };

    internal static readonly HashSet<string> Pistols = new()
    {
        "weapon_glock", "weapon_hkp2000", "weapon_usp_silencer", "weapon_elite", "weapon_p250",
        "weapon_tec9", "weapon_fiveseven", "weapon_deagle", "weapon_cz75a", "weapon_revolver",
    };

    internal static readonly HashSet<string> Rifles = new()
    {
        "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_aug", "weapon_sg556",
        "weapon_galilar", "weapon_famas",
    };

    internal static readonly HashSet<string> Smgs = new()
    {
        "weapon_mac10", "weapon_mp9", "weapon_mp7", "weapon_mp5sd", "weapon_ump45", "weapon_bizon", "weapon_p90",
    };

    internal static readonly HashSet<string> Shotguns = new()
    {
        "weapon_nova", "weapon_xm1014", "weapon_sawedoff", "weapon_mag7",
    };

    internal static readonly HashSet<string> Snipers = new()
    {
        "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1",
    };

    internal static bool IsPrimary(string? name) => name != null && Primaries.Contains(name);
    internal static bool IsPistol(string? name) => name != null && Pistols.Contains(name);
    internal static bool IsRifle(string? name) => name != null && Rifles.Contains(name);
    internal static bool IsSmg(string? name) => name != null && Smgs.Contains(name);
    internal static bool IsShotgun(string? name) => name != null && Shotguns.Contains(name);
    internal static bool IsSniper(string? name) => name != null && Snipers.Contains(name);
    internal static bool IsAwp(string? name) => name == "weapon_awp";

    internal static bool IsTeamLegal(string item, CsTeam team)
    {
        return item switch
        {
            "weapon_ak47" or "weapon_galilar" or "weapon_sg556" or "weapon_mac10"
                or "weapon_tec9" or "weapon_glock" or "weapon_sawedoff" or "weapon_g3sg1"
                => team == CsTeam.Terrorist,
            "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug" or "weapon_famas"
                or "weapon_mp9" or "weapon_usp_silencer" or "weapon_hkp2000" or "weapon_fiveseven"
                or "weapon_mag7" or "weapon_scar20" or "item_defuser"
                => team == CsTeam.CounterTerrorist,
            _ => true,
        };
    }

    internal static int PriceOf(string item, CCSPlayerPawn? pawn = null)
    {
        if (item == "item_assaultsuit" && pawn != null)
        {
            bool helmet = HasHelmet(pawn);
            if (pawn.ArmorValue > 0 && !helmet)
                return 350;
            if (pawn.ArmorValue > 99 && helmet)
                return 0;
            return 1000;
        }

        return Prices.GetValueOrDefault(item, -1);
    }

    internal static bool HasHelmet(CCSPlayerPawn pawn)
    {
        if (pawn.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
            return false;
        return new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasHelmet;
    }

    internal static bool HasDefuser(CCSPlayerPawn pawn)
    {
        if (pawn.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
            return false;
        return new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasDefuser;
    }

    internal static string? FindPrimary(CCSPlayerPawn pawn) => FindWeapon(pawn, IsPrimary);
    internal static string? FindPistol(CCSPlayerPawn pawn) => FindWeapon(pawn, IsPistol);

    internal static IEnumerable<string> Weapons(CCSPlayerPawn pawn)
    {
        if (pawn.WeaponServices == null)
            yield break;
        foreach (var handle in pawn.WeaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null)
                continue;
            string name = weapon.DesignerName;
            if (string.IsNullOrEmpty(name))
                continue;
            if (name is "item_kevlar" or "item_assaultsuit" or "item_defuser")
                continue;
            yield return name;
        }
    }

    private static string? FindWeapon(CCSPlayerPawn pawn, Func<string?, bool> pred)
    {
        foreach (var name in Weapons(pawn))
        {
            if (pred(name))
                return name;
        }
        return null;
    }
}

public sealed partial class BotBuyPatch
{
    private bool HasPrimaryWeapon(CCSPlayerController player)
    {
        if (!player.IsValid || player.PlayerPawn.Value == null)
            return false;
        return WeaponCatalog.FindPrimary(player.PlayerPawn.Value) != null;
    }

    private bool Buy(CCSPlayerController player, string itemName)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        if (!WeaponCatalog.IsTeamLegal(itemName, player.Team))
            return false;

        if (HasItem(player, itemName, pawn))
            return true;

        int price = WeaponCatalog.PriceOf(itemName, pawn);
        if (price < 0)
            return false;
        if (player.InGameMoneyServices.Account < price)
            return false;

        player.GiveNamedItem(itemName);
        if (price > 0)
        {
            player.InGameMoneyServices.Account -= price;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        }
        return true;
    }

    private bool Refund(CCSPlayerController player, string itemName)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        if (!CanRefund(player, itemName))
            return false;
        if (!WeaponCatalog.IsTeamLegal(itemName, player.Team) && itemName.StartsWith("weapon_"))
            return false;
        if (!HasItem(player, itemName, pawn))
            return false;

        int price = itemName == "item_assaultsuit"
            ? 1000
            : WeaponCatalog.PriceOf(itemName);
        if (price < 0)
            return false;

        if (itemName.StartsWith("weapon_"))
        {
            player.RemoveItemByDesignerName(itemName);
        }
        else if (itemName is "item_assaultsuit" or "item_kevlar")
        {
            pawn.ArmorValue = 0;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        }

        if (price > 0)
        {
            player.InGameMoneyServices.Account += price;
            int maxMoney = ConVar.Find("mp_maxmoney")?.GetPrimitiveValue<int>() ?? 16000;
            if (player.InGameMoneyServices.Account > maxMoney)
                player.InGameMoneyServices.Account = maxMoney;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        }
        return true;
    }

    private bool CanRefund(CCSPlayerController player, string itemName)
    {
        if (IsFirstRoundOfHalf())
            return true;
        if (!player.IsValid || !player.IsBot)
            return false;
        var (prevWeapons, _, prevArmor) = PreviousInventory(player);
        if (itemName is "item_assaultsuit" or "item_kevlar")
            return prevArmor <= 0;
        return !prevWeapons.Contains(itemName);
    }

    private bool Swap(CCSPlayerController player, string oldItem, string newItem)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;
        if (!Refund(player, oldItem))
            return false;
        if (!Buy(player, newItem))
        {
            Buy(player, oldItem);
            return false;
        }
        return true;
    }

    private static bool HasItem(CCSPlayerController player, string itemName, CCSPlayerPawn pawn)
    {
        if (itemName.StartsWith("weapon_"))
        {
            return pawn.WeaponServices != null && pawn.WeaponServices.MyWeapons
                .Any(w => w.Value != null && w.Value.DesignerName == itemName);
        }
        if (itemName == "item_defuser")
            return WeaponCatalog.HasDefuser(pawn);
        if (itemName == "item_assaultsuit")
            return pawn.ArmorValue > 0 && WeaponCatalog.HasHelmet(pawn);
        if (itemName == "item_kevlar")
            return pawn.ArmorValue > 0;
        return false;
    }
}
