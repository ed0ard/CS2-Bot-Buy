using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotBuyPatch;

internal enum ArmorIntent
{
    Keep,
    Kevlar,
    Full,
}

internal sealed class IntendedBuy
{
    public string? Primary { get; init; }
    public string? Pistol { get; init; }
    public ArmorIntent Armor { get; init; }
    public bool Defuser { get; init; }
}

public sealed partial class BotBuyPatch
{
    private IntendedBuy PickLoadout(PlayerSnap snap, TeamBuyPlan plan)
    {
        return plan.Round switch
        {
            BuyRound.Pistol => PickPistol(snap),
            BuyRound.Eco => PickEco(snap),
            BuyRound.LightForce => PickLightForce(snap),
            BuyRound.Force => PickForce(snap),
            BuyRound.AntiEco => PickAntiEco(snap),
            BuyRound.FullBuy => PickFullBuy(snap, plan),
            _ => PickEco(snap),
        };
    }

    private static IntendedBuy PickPistol(PlayerSnap snap)
    {
        // Approximate pistol mix, not split-by-round published rates.
        float r = Random.Shared.NextSingle();
        string pistol;
        ArmorIntent armor = r < 0.55f ? ArmorIntent.Kevlar : ArmorIntent.Keep;

        if (snap.Team == CsTeam.CounterTerrorist)
        {
            pistol = Weighted(snap.Team,
                ("weapon_usp_silencer", 0.80f),
                ("weapon_hkp2000", 0.12f),
                ("weapon_p250", 0.04f),
                ("weapon_elite", 0.02f),
                ("weapon_fiveseven", 0.02f)) ?? "weapon_usp_silencer";
        }
        else
        {
            pistol = Weighted(snap.Team,
                ("weapon_glock", 0.80f),
                ("weapon_tec9", 0.08f),
                ("weapon_p250", 0.07f),
                ("weapon_elite", 0.05f)) ?? "weapon_glock";
        }

        return new IntendedBuy
        {
            Primary = null,
            Pistol = pistol,
            Armor = armor,
            Defuser = snap.Team == CsTeam.CounterTerrorist && snap.StartMoney >= 400 && armor == ArmorIntent.Keep,
        };
    }

    private static IntendedBuy PickEco(PlayerSnap snap)
    {
        float r = Random.Shared.NextSingle();
        if (snap.StartMoney >= 1500 && r < 0.35f)
            return PickLightForce(snap);

        // Save the rifle for next full-buy, but never go naked.
        // Floor: team default pistol. If money allows, buy kevlar and/or a pistol upgrade.
        string pistol = DefaultPistol(snap.Team);
        ArmorIntent armor = ArmorIntent.Keep;
        int money = snap.StartMoney;
        bool canKevlar = money >= 650;
        bool canUpgrade = money >= 300;
        bool canBoth = money >= 950;

        if (canBoth)
        {
            float d = Random.Shared.NextSingle();
            if (d < 0.50f)
            {
                armor = ArmorIntent.Kevlar;
            }
            else if (d < 0.75f)
            {
                pistol = EcoPistolUpgrade(snap);
            }
            else
            {
                pistol = EcoPistolUpgrade(snap);
                armor = ArmorIntent.Kevlar;
            }
        }
        else if (canKevlar)
        {
            if (canUpgrade && Random.Shared.NextSingle() < 0.30f)
                pistol = EcoPistolUpgrade(snap);
            else
                armor = ArmorIntent.Kevlar;
        }
        else if (canUpgrade)
        {
            pistol = EcoPistolUpgrade(snap);
        }

        return new IntendedBuy
        {
            Primary = null,
            Pistol = pistol,
            Armor = armor,
            Defuser = false,
        };
    }

    private static string DefaultPistol(CsTeam team)
    {
        return team == CsTeam.CounterTerrorist ? "weapon_usp_silencer" : "weapon_glock";
    }

    private static string EcoPistolUpgrade(PlayerSnap snap)
    {
        if (snap.StartMoney < 500)
            return "weapon_p250";

        if (Random.Shared.NextSingle() < 0.70f)
            return "weapon_p250";

        if (snap.Team == CsTeam.CounterTerrorist)
        {
            return Weighted(snap.Team,
                ("weapon_cz75a", 0.50f),
                ("weapon_fiveseven", 0.30f),
                ("weapon_p250", 0.20f)) ?? "weapon_p250";
        }

        return Weighted(snap.Team,
            ("weapon_cz75a", 0.50f),
            ("weapon_tec9", 0.30f),
            ("weapon_p250", 0.20f)) ?? "weapon_p250";
    }

    private static IntendedBuy PickLightForce(PlayerSnap snap)
    {
        string pistol;
        if (snap.Team == CsTeam.CounterTerrorist)
        {
            pistol = Weighted(snap.Team,
                ("weapon_cz75a", 0.40f),
                ("weapon_deagle", 0.18f),
                ("weapon_p250", 0.15f),
                ("weapon_fiveseven", 0.15f),
                ("weapon_elite", 0.12f)) ?? "weapon_cz75a";
        }
        else
        {
            pistol = Weighted(snap.Team,
                ("weapon_cz75a", 0.40f),
                ("weapon_deagle", 0.18f),
                ("weapon_p250", 0.15f),
                ("weapon_tec9", 0.15f),
                ("weapon_elite", 0.12f)) ?? "weapon_cz75a";
        }

        ArmorIntent armor = snap.StartMoney >= 1500 ? ArmorIntent.Full : ArmorIntent.Kevlar;
        return new IntendedBuy
        {
            Primary = null,
            Pistol = pistol,
            Armor = armor,
            Defuser = false,
        };
    }

    private static IntendedBuy PickForce(PlayerSnap snap)
    {
        string? primary = snap.Personality switch
        {
            BotPersonality.Sniper => Weighted(snap.Team,
                ("weapon_ssg08", 0.50f),
                (snap.Team == CsTeam.Terrorist ? "weapon_galilar" : "weapon_famas", 0.25f),
                (snap.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.25f)),
            BotPersonality.Rusher => Weighted(snap.Team,
                (snap.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.35f),
                ("weapon_mp5sd", 0.20f),
                ("weapon_ump45", 0.15f),
                ("weapon_p90", 0.10f),
                (snap.Team == CsTeam.Terrorist ? "weapon_galilar" : "weapon_famas", 0.20f)),
            BotPersonality.Camper => Weighted(snap.Team,
                (snap.Team == CsTeam.Terrorist ? "weapon_sawedoff" : "weapon_mag7", 0.25f),
                ("weapon_nova", 0.15f),
                ("weapon_ump45", 0.25f),
                (snap.Team == CsTeam.Terrorist ? "weapon_galilar" : "weapon_famas", 0.35f)),
            _ => Weighted(snap.Team,
                (snap.Team == CsTeam.Terrorist ? "weapon_galilar" : "weapon_famas", 0.35f),
                (snap.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.22f),
                ("weapon_ssg08", 0.12f),
                ("weapon_mp5sd", 0.12f),
                ("weapon_ump45", 0.10f),
                ("weapon_p90", 0.05f),
                (snap.Team == CsTeam.Terrorist ? "weapon_nova" : "weapon_mag7", 0.04f)),
        };

        return new IntendedBuy
        {
            Primary = primary,
            Armor = snap.StartMoney >= 2500 ? ArmorIntent.Full : ArmorIntent.Kevlar,
            Defuser = false,
        };
    }

    private static IntendedBuy PickAntiEco(PlayerSnap snap)
    {
        if (snap.LeftoverRifle)
        {
            return new IntendedBuy
            {
                Primary = snap.CurrentPrimary ?? KeepRifleName(snap),
                Armor = ArmorIntent.Full,
                Defuser = snap.Team == CsTeam.CounterTerrorist,
            };
        }

        string? primary = snap.Personality switch
        {
            BotPersonality.Camper => Weighted(snap.Team,
                (snap.Team == CsTeam.Terrorist ? "weapon_sawedoff" : "weapon_mag7", 0.30f),
                ("weapon_nova", 0.15f),
                ("weapon_ump45", 0.25f),
                (snap.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.20f),
                ("weapon_xm1014", 0.10f)),
            BotPersonality.Rusher => Weighted(snap.Team,
                (snap.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.40f),
                ("weapon_p90", 0.20f),
                ("weapon_mp5sd", 0.15f),
                ("weapon_bizon", 0.15f),
                ("weapon_ump45", 0.10f)),
            BotPersonality.Sniper when snap.StartMoney >= 5750 && Random.Shared.NextSingle() < 0.10f
                => "weapon_awp",
            _ => Weighted(snap.Team,
                (snap.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.50f),
                ("weapon_mp5sd", 0.14f),
                ("weapon_ump45", 0.12f),
                ("weapon_mp7", 0.08f),
                ("weapon_bizon", 0.06f),
                ("weapon_p90", 0.06f),
                (snap.Team == CsTeam.Terrorist ? "weapon_nova" : "weapon_mag7", 0.04f)),
        };

        return new IntendedBuy
        {
            Primary = primary,
            Armor = ArmorIntent.Full,
            Defuser = snap.Team == CsTeam.CounterTerrorist,
        };
    }

    private static IntendedBuy PickFullBuy(PlayerSnap snap, TeamBuyPlan plan)
    {
        string? primary;
        if (WeaponCatalog.IsAwp(snap.CurrentPrimary) && snap.LeftoverRifle)
        {
            plan.AwpSlots = 0;
            return new IntendedBuy
            {
                Primary = "weapon_awp",
                Armor = ArmorIntent.Full,
                Defuser = snap.Team == CsTeam.CounterTerrorist,
            };
        }

        bool wantAwp = snap.Personality == BotPersonality.Sniper
            || (plan.AwpSlots > 0 && Random.Shared.NextSingle() < 0.10f);

        if (wantAwp && plan.AwpSlots > 0 && snap.StartMoney >= 5750)
        {
            plan.AwpSlots--;
            primary = "weapon_awp";
        }
        else if (snap.Personality == BotPersonality.Rusher && Random.Shared.NextSingle() < 0.08f)
        {
            primary = "weapon_p90";
        }
        else if (snap.Team == CsTeam.Terrorist)
        {
            primary = Weighted(snap.Team,
                ("weapon_ak47", 0.94f),
                ("weapon_sg556", 0.03f),
                ("weapon_galilar", 0.03f));
        }
        else
        {
            primary = Weighted(snap.Team,
                ("weapon_m4a1_silencer", 0.75f),
                ("weapon_m4a1", 0.22f),
                ("weapon_aug", 0.03f));
        }

        return new IntendedBuy
        {
            Primary = primary,
            Armor = ArmorIntent.Full,
            Defuser = snap.Team == CsTeam.CounterTerrorist,
        };
    }

    private static string? KeepRifleName(PlayerSnap snap)
    {
        return snap.Team == CsTeam.Terrorist ? "weapon_ak47" : "weapon_m4a1_silencer";
    }

    private void ApplyLoadout(CCSPlayerController player, IntendedBuy intended)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return;

        ApplyPrimary(player, pawn, intended.Primary);
        ApplyPistol(player, pawn, intended.Pistol);
        ApplyArmor(player, pawn, intended.Armor);
        if (intended.Defuser)
            Buy(player, "item_defuser");
        EnsurePistol(player, pawn);
    }

    private void ApplyPistol(CCSPlayerController player, CCSPlayerPawn pawn, string? pistol)
    {
        if (pistol == null)
            return;
        string? current = WeaponCatalog.FindPistol(pawn);
        if (current == pistol)
            return;
        if (current == null)
        {
            Buy(player, pistol);
            return;
        }
        if (!CanRefund(player, current))
            return;
        Swap(player, current, pistol);
    }

    private void EnsurePistol(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (WeaponCatalog.FindPistol(pawn) != null)
            return;
        string fallback = DefaultPistol(player.Team);
        if (!Buy(player, fallback))
            player.GiveNamedItem(fallback);
    }

    private void ApplyPrimary(CCSPlayerController player, CCSPlayerPawn pawn, string? primary)
    {
        var currentWeapons = WeaponCatalog.Weapons(pawn).Where(WeaponCatalog.IsPrimary).ToList();
        if (primary == null)
        {
            foreach (var gun in currentWeapons)
                Refund(player, gun);
            return;
        }

        if (currentWeapons.Contains(primary))
        {
            foreach (var gun in currentWeapons.Where(g => g != primary))
                Refund(player, gun);
            return;
        }

        foreach (var gun in currentWeapons)
        {
            if (Swap(player, gun, primary))
                return;
            if (!CanRefund(player, gun))
                return;
            Refund(player, gun);
        }

        Buy(player, primary);
    }

    private void ApplyArmor(CCSPlayerController player, CCSPlayerPawn pawn, ArmorIntent intent)
    {
        var (_, _, prevArmor) = PreviousInventory(player);
        bool leftover = prevArmor > 0;
        bool hasArmor = pawn.ArmorValue > 0;
        bool helmet = WeaponCatalog.HasHelmet(pawn);

        switch (intent)
        {
            case ArmorIntent.Keep:
                if (hasArmor && !leftover)
                {
                    if (helmet)
                        Refund(player, "item_assaultsuit");
                    else
                        Refund(player, "item_kevlar");
                }
                break;
            case ArmorIntent.Kevlar:
                if (!hasArmor)
                    Buy(player, "item_kevlar");
                break;
            case ArmorIntent.Full:
                if (!helmet)
                    Buy(player, "item_assaultsuit");
                break;
        }
    }

    private static string? Weighted(CsTeam team, params (string? item, float weight)[] options)
    {
        var legal = options
            .Where(o => o.weight > 0 && (o.item == null || WeaponCatalog.IsTeamLegal(o.item, team)))
            .ToArray();
        if (legal.Length == 0)
            return null;

        float total = legal.Sum(o => o.weight);
        float roll = Random.Shared.NextSingle() * total;
        foreach (var option in legal)
        {
            roll -= option.weight;
            if (roll <= 0)
                return option.item;
        }
        return legal[^1].item;
    }
}
