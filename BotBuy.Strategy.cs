using CounterStrikeSharp.API.Modules.Utils;

namespace BotBuyPatch;

// Pure decision layer: scalar inputs only, no entity references.
// Deterministic given (inputs, rng) — unit-testable without a server.

public enum TeamTemperament
{
    Saver,
    Balanced,
    Aggressive,
}

internal sealed class BotInput
{
    public CsTeam Team { get; init; }
    public int StartMoney { get; init; }
    public bool LeftoverRifle { get; init; }
    public string? CurrentPrimary { get; init; }
    public BotPersonality Personality { get; init; }
    public int SkillTier { get; init; } = 1; // 0 low / 1 mid / 2 high
}

internal sealed class TeamContext
{
    public bool FirstRoundOfHalf { get; init; }
    public bool PistolRound { get; init; }
    public CsTeam LastWinner { get; init; }
    public int LossIndex { get; init; }
    public TeamTemperament Temperament { get; init; }
}

internal static class BotStrategy
{
    internal static readonly int[] LossBonus = [1400, 1900, 2400, 2900, 3400];

    internal static int RifleCost(CsTeam team) => team == CsTeam.Terrorist ? 2700 : 2900;
    internal static int FullBuyFloor(CsTeam team) => RifleCost(team) + 1000;
    internal static int NextLossPayout(int lossIndex) => LossBonus[Math.Min(4, lossIndex + 1)];

    // m_iPawnBotDifficulty: 0-1 low / 2 mid / 3+ high; unknown (<0) falls back to mid.
    internal static int MapSkillTier(int rawDifficulty) => rawDifficulty switch
    {
        < 0 => 1,
        <= 1 => 0,
        2 => 1,
        _ => 2,
    };

    internal static TeamTemperament RollTemperament(Random rng)
    {
        float r = rng.NextSingle();
        return r < 0.25f ? TeamTemperament.Saver
            : r < 0.75f ? TeamTemperament.Balanced
            : TeamTemperament.Aggressive;
    }

    // Hard save floor: what this bot must keep so that (money + next loss payout) still
    // reaches the full-buy line. A leftover rifle lowers the requirement.
    internal static int ReserveFor(CsTeam team, BotInput bot, int lossIndex)
    {
        int reserve = Math.Max(0, FullBuyFloor(team) - NextLossPayout(lossIndex));
        if (bot.LeftoverRifle)
            reserve = Math.Max(0, reserve - RifleCost(team));
        return reserve;
    }

    internal static int Median(List<int> values)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        int mid = values.Count / 2;
        if (values.Count % 2 == 1)
            return values[mid];
        return (values[mid - 1] + values[mid]) / 2;
    }

    internal static TeamBuyPlan ClassifyTeam(IReadOnlyList<BotInput> us, IReadOnlyList<BotInput> enemy, TeamContext ctx, Random rng)
    {
        if (us.Count == 0)
            return new TeamBuyPlan { Round = BuyRound.Eco, Temperament = ctx.Temperament };

        var team = us[0].Team;
        int floor = FullBuyFloor(team);
        int jitter = rng.Next(-250, 251);
        int floorJ = floor + jitter;
        int median = Median(us.Select(b => b.StartMoney).ToList());
        int enemyMedian = enemy.Count == 0 ? 0 : Median(enemy.Select(b => b.StartMoney).ToList());

        bool pistol = ctx.PistolRound || (ctx.FirstRoundOfHalf && median <= 1200);
        if (pistol)
            return new TeamBuyPlan { Round = BuyRound.Pistol, Median = median, Temperament = ctx.Temperament };

        int canFull = us.Count(b => b.LeftoverRifle || b.StartMoney >= floorJ);
        int leftoverRifles = us.Count(b => b.LeftoverRifle);
        bool majorityFull = canFull * 5 >= us.Count * 3;
        bool weWon = ctx.LastWinner == team;
        bool enemyEco = enemyMedian < 2500 && enemy.Count(b => b.LeftoverRifle) <= 1;
        int antiEcoMin = ctx.Temperament == TeamTemperament.Aggressive ? 1500 : 2000;

        if (weWon && enemyEco && leftoverRifles * 2 < us.Count && median >= antiEcoMin)
            return new TeamBuyPlan { Round = BuyRound.AntiEco, Median = median, Temperament = ctx.Temperament };

        if (majorityFull || median >= floorJ)
            return new TeamBuyPlan { Round = BuyRound.FullBuy, Median = median, AwpSlots = 1, Temperament = ctx.Temperament };

        int nextIfLose = median + NextLossPayout(ctx.LossIndex);
        if (nextIfLose >= floorJ)
        {
            float forceChance = ctx.Temperament switch
            {
                TeamTemperament.Saver => 0.08f,
                TeamTemperament.Aggressive => 0.45f,
                _ => 0.22f,
            };
            if (median >= 2000 && rng.NextSingle() < forceChance)
                return new TeamBuyPlan { Round = BuyRound.Force, Median = median, Temperament = ctx.Temperament };
            return new TeamBuyPlan { Round = BuyRound.Eco, Median = median, Temperament = ctx.Temperament };
        }

        if (median >= 1800 + jitter)
            return new TeamBuyPlan { Round = BuyRound.Force, Median = median, Temperament = ctx.Temperament };
        if (median >= 1200 + jitter)
            return new TeamBuyPlan { Round = BuyRound.LightForce, Median = median, Temperament = ctx.Temperament };
        return new TeamBuyPlan { Round = BuyRound.Eco, Median = median, Temperament = ctx.Temperament };
    }

    internal static IntendedBuy PickLoadout(BotInput bot, TeamBuyPlan plan, TeamBuyPlan enemyPlan, int lossIndex, Random rng)
    {
        return plan.Round switch
        {
            BuyRound.Pistol => PickPistol(bot, rng),
            BuyRound.Eco => PickEco(bot, lossIndex, rng),
            BuyRound.LightForce => PickLightForce(bot, rng),
            BuyRound.Force => PickForce(bot, rng),
            BuyRound.AntiEco => PickAntiEco(bot, rng),
            BuyRound.FullBuy => PickFullBuy(bot, plan, enemyPlan, rng),
            _ => PickEco(bot, lossIndex, rng),
        };
    }

    private static IntendedBuy PickPistol(BotInput bot, Random rng)
    {
        // Approximate pistol mix, not split-by-round published rates.
        float r = rng.NextSingle();
        string pistol;
        ArmorIntent armor = r < 0.55f ? ArmorIntent.Kevlar : ArmorIntent.Keep;

        if (bot.Team == CsTeam.CounterTerrorist)
        {
            pistol = Weighted(bot.Team, rng,
                ("weapon_usp_silencer", 0.80f),
                ("weapon_hkp2000", 0.12f),
                ("weapon_p250", 0.04f),
                ("weapon_elite", 0.02f),
                ("weapon_fiveseven", 0.02f)) ?? "weapon_usp_silencer";
        }
        else
        {
            pistol = Weighted(bot.Team, rng,
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
            Defuser = bot.Team == CsTeam.CounterTerrorist && bot.StartMoney >= 400 && armor == ArmorIntent.Keep,
        };
    }

    private static IntendedBuy PickEco(BotInput bot, int lossIndex, Random rng)
    {
        // Hard floor: everything below the reserve line is untouchable.
        int cap = bot.StartMoney - ReserveFor(bot.Team, bot, lossIndex);

        // Rich savers may still go light-force, but only inside the cap.
        if (cap >= 1700 && rng.NextSingle() < 0.35f)
            return PickLightForce(bot, rng);

        string pistol = DefaultPistol(bot.Team);
        ArmorIntent armor = ArmorIntent.Keep;

        bool canKevlar = cap >= 650;
        bool canUpgrade = cap >= 300;
        bool canBoth = cap >= 950;

        if (canBoth)
        {
            float d = rng.NextSingle();
            if (d < 0.50f)
            {
                armor = ArmorIntent.Kevlar;
            }
            else if (d < 0.75f)
            {
                pistol = EcoPistolUpgrade(bot, cap, rng);
            }
            else
            {
                // kevlar + pistol must fit inside the cap together.
                pistol = EcoPistolUpgrade(bot, cap - 650, rng);
                armor = ArmorIntent.Kevlar;
            }
        }
        else if (canKevlar)
        {
            if (canUpgrade && rng.NextSingle() < 0.30f)
                pistol = EcoPistolUpgrade(bot, cap, rng);
            else
                armor = ArmorIntent.Kevlar;
        }
        else if (canUpgrade)
        {
            pistol = EcoPistolUpgrade(bot, cap, rng);
        }

        return new IntendedBuy
        {
            Primary = null,
            Pistol = pistol,
            Armor = armor,
            Defuser = false,
        };
    }

    internal static string DefaultPistol(CsTeam team)
    {
        return team == CsTeam.CounterTerrorist ? "weapon_usp_silencer" : "weapon_glock";
    }

    private static string EcoPistolUpgrade(BotInput bot, int cap, Random rng)
    {
        if (cap < 500)
            return cap >= 300 ? "weapon_p250" : DefaultPistol(bot.Team);

        if (rng.NextSingle() < 0.65f)
            return "weapon_p250";

        return Weighted(bot.Team, rng,
            ("weapon_cz75a", 0.45f),
            (bot.Team == CsTeam.CounterTerrorist ? "weapon_fiveseven" : "weapon_tec9", 0.30f),
            ("weapon_deagle", cap >= 700 ? 0.15f : 0f),
            ("weapon_p250", 0.25f)) ?? "weapon_p250";
    }

    private static IntendedBuy PickLightForce(BotInput bot, Random rng)
    {
        string pistol;
        if (bot.Team == CsTeam.CounterTerrorist)
        {
            pistol = Weighted(bot.Team, rng,
                ("weapon_cz75a", 0.40f),
                ("weapon_deagle", 0.18f),
                ("weapon_p250", 0.15f),
                ("weapon_fiveseven", 0.15f),
                ("weapon_elite", 0.12f)) ?? "weapon_cz75a";
        }
        else
        {
            pistol = Weighted(bot.Team, rng,
                ("weapon_cz75a", 0.40f),
                ("weapon_deagle", 0.18f),
                ("weapon_p250", 0.15f),
                ("weapon_tec9", 0.15f),
                ("weapon_elite", 0.12f)) ?? "weapon_cz75a";
        }

        ArmorIntent armor = bot.StartMoney >= 1500 ? ArmorIntent.Full : ArmorIntent.Kevlar;
        return new IntendedBuy
        {
            Primary = null,
            Pistol = pistol,
            Armor = armor,
            Defuser = false,
        };
    }

    private static (string? item, float weight)[] ForcePool(BotInput bot)
    {
        return bot.Personality switch
        {
            BotPersonality.Sniper =>
            [
                ("weapon_ssg08", 0.50f),
                (bot.Team == CsTeam.Terrorist ? "weapon_galilar" : "weapon_famas", 0.25f),
                (bot.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.25f),
            ],
            BotPersonality.Rusher =>
            [
                (bot.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.35f),
                ("weapon_mp5sd", 0.20f),
                ("weapon_ump45", 0.15f),
                ("weapon_p90", 0.10f),
                (bot.Team == CsTeam.Terrorist ? "weapon_galilar" : "weapon_famas", 0.20f),
            ],
            BotPersonality.Camper =>
            [
                (bot.Team == CsTeam.Terrorist ? "weapon_sawedoff" : "weapon_mag7", 0.25f),
                ("weapon_nova", 0.15f),
                ("weapon_ump45", 0.25f),
                (bot.Team == CsTeam.Terrorist ? "weapon_galilar" : "weapon_famas", 0.35f),
            ],
            _ =>
            [
                (bot.Team == CsTeam.Terrorist ? "weapon_galilar" : "weapon_famas", 0.35f),
                (bot.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.22f),
                ("weapon_ssg08", 0.12f),
                ("weapon_mp5sd", 0.12f),
                ("weapon_ump45", 0.10f),
                ("weapon_p90", 0.05f),
                (bot.Team == CsTeam.Terrorist ? "weapon_nova" : "weapon_mag7", 0.04f),
            ],
        };
    }

    private static IntendedBuy PickForce(BotInput bot, Random rng)
    {
        // Affordable-only pool: a poor bot on a force round degrades to what it can
        // actually pay for, falling back to a pistol upgrade if nothing fits.
        var pool = ForcePool(bot);
        var affordable = pool
            .Where(o => o.item == null || WeaponCatalog.PriceOf(o.item) <= bot.StartMoney)
            .ToArray();

        string? primary = affordable.Length > 0 ? Weighted(bot.Team, rng, affordable) : null;
        string? pistol = null;
        if (primary == null)
            pistol = EcoPistolUpgrade(bot, bot.StartMoney, rng);

        return new IntendedBuy
        {
            Primary = primary,
            Pistol = pistol,
            Armor = bot.StartMoney >= 2500 ? ArmorIntent.Full : ArmorIntent.Kevlar,
            Defuser = false,
        };
    }

    private static IntendedBuy PickAntiEco(BotInput bot, Random rng)
    {
        if (bot.LeftoverRifle)
        {
            return new IntendedBuy
            {
                Primary = bot.CurrentPrimary ?? KeepRifleName(bot),
                Armor = ArmorIntent.Full,
                Defuser = bot.Team == CsTeam.CounterTerrorist,
            };
        }

        (string? item, float weight)[] pool = bot.Personality switch
        {
            BotPersonality.Camper =>
            [
                (bot.Team == CsTeam.Terrorist ? "weapon_sawedoff" : "weapon_mag7", 0.30f),
                ("weapon_nova", 0.15f),
                ("weapon_ump45", 0.25f),
                (bot.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.20f),
                ("weapon_xm1014", 0.10f),
            ],
            BotPersonality.Rusher =>
            [
                (bot.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.40f),
                ("weapon_p90", 0.20f),
                ("weapon_mp5sd", 0.15f),
                ("weapon_bizon", 0.15f),
                ("weapon_ump45", 0.10f),
            ],
            _ =>
            [
                (bot.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.50f),
                ("weapon_mp5sd", 0.14f),
                ("weapon_ump45", 0.12f),
                ("weapon_mp7", 0.08f),
                ("weapon_bizon", 0.06f),
                ("weapon_p90", 0.06f),
                (bot.Team == CsTeam.Terrorist ? "weapon_nova" : "weapon_mag7", 0.04f),
            ],
        };

        string? primary;
        if (bot.Personality == BotPersonality.Sniper && bot.StartMoney >= 5750 && rng.NextSingle() < 0.10f)
            primary = "weapon_awp";
        else
        {
            var affordable = pool
                .Where(o => o.item == null || WeaponCatalog.PriceOf(o.item) <= bot.StartMoney)
                .ToArray();
            primary = affordable.Length > 0 ? Weighted(bot.Team, rng, affordable) : null;
        }

        return new IntendedBuy
        {
            Primary = primary,
            Armor = ArmorIntent.Full,
            Defuser = bot.Team == CsTeam.CounterTerrorist,
        };
    }

    private static ArmorIntent FullBuyArmor(BotInput bot, TeamBuyPlan enemyPlan)
    {
        // CT vs a T full-buy (AK/AWP headshots anyway) and tight on money: skip the helmet.
        if (bot.Team == CsTeam.CounterTerrorist
            && enemyPlan.Round == BuyRound.FullBuy
            && bot.StartMoney < FullBuyFloor(bot.Team) + 800)
            return ArmorIntent.Kevlar;
        return ArmorIntent.Full;
    }

    private static (string? item, float weight)[] AnchorPool(CsTeam team, TeamTemperament temperament)
    {
        return (team, temperament) switch
        {
            (CsTeam.Terrorist, TeamTemperament.Saver) =>
            [
                ("weapon_ak47", 0.90f),
                ("weapon_galilar", 0.10f),
            ],
            (CsTeam.Terrorist, TeamTemperament.Aggressive) =>
            [
                ("weapon_ak47", 0.72f),
                ("weapon_galilar", 0.16f),
                ("weapon_sg556", 0.12f),
            ],
            (CsTeam.Terrorist, _) =>
            [
                ("weapon_ak47", 0.82f),
                ("weapon_sg556", 0.09f),
                ("weapon_galilar", 0.09f),
            ],
            (CsTeam.CounterTerrorist, TeamTemperament.Saver) =>
            [
                ("weapon_m4a1_silencer", 0.62f),
                ("weapon_m4a1", 0.23f),
                ("weapon_famas", 0.15f),
            ],
            (CsTeam.CounterTerrorist, TeamTemperament.Aggressive) =>
            [
                ("weapon_m4a1_silencer", 0.52f),
                ("weapon_m4a1", 0.18f),
                ("weapon_aug", 0.12f),
                ("weapon_famas", 0.18f),
            ],
            _ =>
            [
                ("weapon_m4a1_silencer", 0.58f),
                ("weapon_m4a1", 0.22f),
                ("weapon_aug", 0.10f),
                ("weapon_famas", 0.10f),
            ],
        };
    }

    // Cheap spray pool for the lowest difficulty tier: no scoped/precision guns.
    private static (string? item, float weight)[] LowSkillPool(BotInput bot)
    {
        string smg = bot.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9";
        string cheapRifle = bot.Team == CsTeam.Terrorist ? "weapon_galilar" : "weapon_famas";
        string shotgun = bot.Team == CsTeam.Terrorist ? "weapon_nova" : "weapon_mag7";
        return
        [
            (smg, 0.30f),
            (cheapRifle, 0.30f),
            ("weapon_ump45", 0.12f),
            ("weapon_p90", 0.12f),
            (shotgun, 0.11f),
            ("weapon_negev", 0.05f),
        ];
    }

    private static string? FullBuyPrimary(BotInput bot, TeamBuyPlan plan, Random rng)
    {
        if (bot.SkillTier == 0)
            return AffordablePick(bot, LowSkillPool(bot), rng);

        if (bot.Personality == BotPersonality.Sniper
            || (plan.AwpSlots > 0 && rng.NextSingle() < 0.10f))
        {
            if (plan.AwpSlots > 0 && bot.StartMoney >= 5750)
            {
                plan.AwpSlots--;
                return "weapon_awp";
            }
            if (bot.Personality == BotPersonality.Sniper
                && bot.StartMoney >= 2350
                && rng.NextSingle() < 0.50f)
                return "weapon_ssg08";
        }

        var pool = AnchorPool(bot.Team, plan.Temperament).ToList();
        float tail = plan.Temperament switch
        {
            TeamTemperament.Saver => 0.06f,
            TeamTemperament.Aggressive => 0.28f,
            _ => 0.15f,
        };

        switch (bot.Personality)
        {
            case BotPersonality.Rusher:
                pool.Add(("weapon_p90", plan.Temperament == TeamTemperament.Saver ? 0f : tail));
                if (plan.Temperament == TeamTemperament.Aggressive)
                    pool.Add((bot.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9", 0.10f));
                break;
            case BotPersonality.Camper:
                if (plan.Temperament == TeamTemperament.Saver)
                {
                    pool.Add(("weapon_nova", tail));
                }
                else
                {
                    pool.Add(("weapon_xm1014", tail * 0.6f));
                    pool.Add((bot.Team == CsTeam.Terrorist ? "weapon_sawedoff" : "weapon_mag7", tail * 0.4f));
                }
                if (bot.StartMoney >= 6000)
                {
                    string auto = bot.Team == CsTeam.Terrorist ? "weapon_g3sg1" : "weapon_scar20";
                    pool.Add((auto, plan.Temperament == TeamTemperament.Aggressive ? 0.02f : 0.01f));
                }
                break;
            case BotPersonality.Sniper:
                if (bot.StartMoney >= 2350)
                    pool.Add(("weapon_ssg08", 0.06f));
                break;
        }

        return AffordablePick(bot, pool.ToArray(), rng);
    }

    // Guns must leave room for armor: >=2900 primaries need +1000, cheaper need +650.
    private static string? AffordablePick(BotInput bot, (string? item, float weight)[] pool, Random rng)
    {
        var affordable = pool
            .Where(o => o.item == null || bot.StartMoney >= WeaponCatalog.PriceOf(o.item) + (WeaponCatalog.PriceOf(o.item) >= 2900 ? 1000 : 650))
            .ToArray();
        if (affordable.Length == 0)
            return FallbackPrimary(bot);
        return Weighted(bot.Team, rng, affordable) ?? FallbackPrimary(bot);
    }

    private static string? FallbackPrimary(BotInput bot)
    {
        string cheapRifle = bot.Team == CsTeam.Terrorist ? "weapon_galilar" : "weapon_famas";
        if (bot.StartMoney >= WeaponCatalog.PriceOf(cheapRifle) + 650)
            return cheapRifle;
        string smg = bot.Team == CsTeam.Terrorist ? "weapon_mac10" : "weapon_mp9";
        if (bot.StartMoney >= WeaponCatalog.PriceOf(smg))
            return smg;
        return null;
    }

    private static IntendedBuy PickFullBuy(BotInput bot, TeamBuyPlan plan, TeamBuyPlan enemyPlan, Random rng)
    {
        if (WeaponCatalog.IsAwp(bot.CurrentPrimary) && bot.LeftoverRifle)
        {
            plan.AwpSlots = 0;
            return new IntendedBuy
            {
                Primary = "weapon_awp",
                Armor = ArmorIntent.Full,
                Defuser = bot.Team == CsTeam.CounterTerrorist,
            };
        }

        return new IntendedBuy
        {
            Primary = FullBuyPrimary(bot, plan, rng),
            Armor = FullBuyArmor(bot, enemyPlan),
            Defuser = bot.Team == CsTeam.CounterTerrorist,
        };
    }

    internal static string? PickDroppedPrimary(CsTeam team, int money, bool allowAwp, Random rng)
    {
        int awpPrice = WeaponCatalog.PriceOf("weapon_awp");
        if (allowAwp && awpPrice > 0 && money >= awpPrice)
            return "weapon_awp";

        (string? item, float weight)[] pool = team == CsTeam.CounterTerrorist
            ?
            [
                ("weapon_m4a1_silencer", 0.50f),
                ("weapon_m4a1", 0.25f),
                ("weapon_aug", 0.15f),
                ("weapon_famas", 0.10f),
            ]
            :
            [
                ("weapon_ak47", 0.60f),
                ("weapon_sg556", 0.15f),
                ("weapon_galilar", 0.25f),
            ];

        var affordable = pool
            .Where(o => o.item != null && WeaponCatalog.PriceOf(o.item) <= money)
            .ToArray();
        if (affordable.Length > 0)
            return Weighted(team, rng, affordable);

        return team == CsTeam.CounterTerrorist ? "weapon_famas" : "weapon_galilar";
    }

    private static string? KeepRifleName(BotInput bot)
    {
        return bot.Team == CsTeam.Terrorist ? "weapon_ak47" : "weapon_m4a1_silencer";
    }

    internal static string? Weighted(CsTeam team, Random rng, params (string? item, float weight)[] options)
    {
        var legal = options
            .Where(o => o.weight > 0 && (o.item == null || WeaponCatalog.IsTeamLegal(o.item, team)))
            .ToArray();
        if (legal.Length == 0)
            return null;

        float total = legal.Sum(o => o.weight);
        float roll = rng.NextSingle() * total;
        foreach (var option in legal)
        {
            roll -= option.weight;
            if (roll <= 0)
                return option.item;
        }
        return legal[^1].item;
    }

    internal static BotPersonality PersonalityFromGun(string? gun)
    {
        if (WeaponCatalog.IsSniper(gun))
            return BotPersonality.Sniper;
        if (WeaponCatalog.IsSmg(gun))
            return BotPersonality.Rusher;
        if (WeaponCatalog.IsShotgun(gun) || gun is "weapon_negev" or "weapon_m249")
            return BotPersonality.Camper;
        return BotPersonality.Rifle;
    }
}
