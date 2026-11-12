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
    private void ApplyLoadout(CCSPlayerController player, IntendedBuy intended, BuyRound round)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return;

        if (round is BuyRound.Eco or BuyRound.Pistol)
            RefundNewGrenades(player, pawn);

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

    private void RefundNewGrenades(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        foreach (var nade in WeaponCatalog.Weapons(pawn).Where(WeaponCatalog.IsGrenade).ToList())
            Refund(player, nade);
    }

    private void EnsurePistol(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        if (WeaponCatalog.FindPistol(pawn) != null)
            return;
        string fallback = BotStrategy.DefaultPistol(player.Team);
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
}
