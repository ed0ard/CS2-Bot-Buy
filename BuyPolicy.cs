using static BotBuyPatch.Weapon;

namespace BotBuyPatch;

// The existing 1.0.14 choices, evaluated before spending instead of after a refund.
internal static class BuyPolicy
{
    internal static ushort Pistol(bool ct, int money, float roll) => (ct, money) switch
    {
        (true, 800) => roll switch { < .50f => Kevlar, < .65f => Elite, < .75f => P250, < .83f => Deagle, < .91f => Cz75, < .98f => FiveSeven, _ => Revolver },
        (false, 800) => roll switch { < .50f => Kevlar, < .65f => Elite, < .77f => P250, < .85f => Deagle, < .87f => Revolver, _ => Tec9 },
        (true, 1000) => roll switch { < .20f => Elite, < .50f => Deagle, < .65f => Cz75, < .95f => FiveSeven, _ => Revolver },
        (false, 1000) => roll switch { < .20f => Elite, < .30f => P250, < .55f => Deagle, < .60f => Revolver, _ => Tec9 },
        (true, 10000) => roll switch { < .35f => M4A4, < .70f => M4A1S, < .90f => Awp, _ => Scar20 },
        (false, 10000) => roll switch { < .70f => Ak47, < .90f => Awp, _ => G3sg1 },
        _ => 0
    };

    internal static ushort Force(bool ct, float roll)
        => roll < .10f ? (ct ? (ushort)FiveSeven : (ushort)Tec9)
            : roll < .20f ? (ct ? (ushort)Mp9 : (ushort)Mac10) : (ushort)0;

    internal static ushort Replace(ushort requested, bool ct, float roll, bool lowGravity) => requested switch
    {
        Aug => roll switch { < .06f => Aug, < .53f => M4A4, _ => M4A1S },
        P90 => roll switch { < .30f => Bizon, < .40f => Mp7, < .50f => Mp5, < .60f => Ump45, _ => P90 },
        Xm1014 => roll < .50f ? (ushort)Negev : ct && roll < .60f ? (ushort)Mag7 : !ct && roll < .65f ? (ushort)SawedOff : (ushort)Xm1014,
        Scout when !lowGravity => roll < .05f ? (ushort)Deagle : roll < .45f ? (ct ? (ushort)Mp9 : (ushort)Mac10) : (ushort)Scout,
        _ => requested
    };

    internal static ushort Advantage(bool ct, float roll)
        => roll < .10f ? (ct ? (ushort)Scar20 : (ushort)G3sg1) : roll < .14f ? (ushort)M249 : (ushort)0;

    internal static bool Primary(ushort definition) => definition is
        Ak47 or Aug or Awp or Famas or G3sg1 or Galil or M249 or M4A4 or Mac10 or P90 or Mp5 or Ump45 or Xm1014 or Bizon or Mag7 or Negev or SawedOff or Mp7 or Mp9 or Scar20 or Sg556 or Scout or M4A1S;
    internal static bool Pistol(ushort definition) => definition is Deagle or Elite or FiveSeven or Glock or Tec9 or P2000 or P250 or Usp or Cz75 or Revolver;
    internal static bool CanSponsor(int donor, int recipient, int price)
        => price > 0 && recipient >= 0 && donor - (long)price >= recipient;

    internal static IEnumerable<ushort> Shop(bool ct)
    {
        for (ushort definition = Deagle; definition <= Revolver; definition++)
        {
            if (!Primary(definition) && !Pistol(definition)) continue;
            if (ct && definition is Glock or Ak47 or G3sg1 or Galil or Mac10 or SawedOff or Tec9 or Sg556) continue;
            if (!ct && definition is FiveSeven or Aug or Famas or M4A4 or Mag7 or P2000 or Mp9 or Scar20 or M4A1S or Usp) continue;
            yield return definition;
        }
    }
}

internal static class Weapon
{
    internal const ushort Deagle = 1, Elite = 2, FiveSeven = 3, Glock = 4, Ak47 = 7;
    internal const ushort Aug = 8, Awp = 9, Famas = 10, G3sg1 = 11, Galil = 13;
    internal const ushort M249 = 14, M4A4 = 16, Mac10 = 17, P90 = 19, Mp5 = 23;
    internal const ushort Ump45 = 24, Xm1014 = 25, Bizon = 26, Mag7 = 27, Negev = 28;
    internal const ushort SawedOff = 29, Tec9 = 30, P2000 = 32, Mp7 = 33, Mp9 = 34;
    internal const ushort P250 = 36, Scar20 = 38, Sg556 = 39, Scout = 40, Kevlar = 50;
    internal const ushort M4A1S = 60, Usp = 61, Cz75 = 63, Revolver = 64;
}
