using BotBuyPatch;
using CounterStrikeSharp.API.Modules.Utils;
using Xunit;

namespace BotBuy.Tests;

public class StrategyTests
{
    private static BotInput Bot(
        int money,
        CsTeam team = CsTeam.Terrorist,
        BotPersonality personality = BotPersonality.Rifle,
        int tier = 1,
        bool leftoverRifle = false,
        string? gun = null)
    {
        return new BotInput
        {
            Team = team,
            StartMoney = money,
            LeftoverRifle = leftoverRifle,
            CurrentPrimary = gun,
            Personality = personality,
            SkillTier = tier,
        };
    }

    private static TeamContext Ctx(
        TeamTemperament temperament = TeamTemperament.Balanced,
        bool firstOfHalf = false,
        bool pistol = false,
        CsTeam lastWinner = CsTeam.None,
        int lossIndex = 0)
    {
        return new TeamContext
        {
            FirstRoundOfHalf = firstOfHalf,
            PistolRound = pistol,
            LastWinner = lastWinner,
            LossIndex = lossIndex,
            Temperament = temperament,
        };
    }

    private static List<BotInput> TeamOf(CsTeam team, params int[] monies)
    {
        return monies.Select(m => Bot(m, team)).ToList();
    }

    private static TeamBuyPlan Plan(BuyRound round)
    {
        return new TeamBuyPlan { Round = round, AwpSlots = 1, Temperament = TeamTemperament.Balanced };
    }

    private static int SpendOf(BotInput bot, IntendedBuy buy)
    {
        int cost = 0;
        if (buy.Primary != null)
            cost += WeaponCatalog.PriceOf(buy.Primary);
        if (buy.Pistol != null && buy.Pistol != BotStrategy.DefaultPistol(bot.Team))
            cost += WeaponCatalog.PriceOf(buy.Pistol);
        cost += buy.Armor switch
        {
            ArmorIntent.Kevlar => 650,
            ArmorIntent.Full => 1000,
            _ => 0,
        };
        return cost;
    }

    [Fact]
    public void Classify_PistolRound_LowMedian()
    {
        var us = TeamOf(CsTeam.Terrorist, 800, 800, 800, 800, 800);
        var plan = BotStrategy.ClassifyTeam(us, TeamOf(CsTeam.CounterTerrorist, 800), Ctx(firstOfHalf: true), new Random(1));
        Assert.Equal(BuyRound.Pistol, plan.Round);
    }

    [Fact]
    public void Classify_RichTeam_FullBuy()
    {
        var us = TeamOf(CsTeam.Terrorist, 6000, 5500, 5000, 4800, 4500);
        var plan = BotStrategy.ClassifyTeam(us, TeamOf(CsTeam.CounterTerrorist, 5000), Ctx(), new Random(2));
        Assert.Equal(BuyRound.FullBuy, plan.Round);
        Assert.Equal(1, plan.AwpSlots);
    }

    [Fact]
    public void Classify_CanFullNextIfSave_SavesOrForces()
    {
        // T floor 3700; median 2200 + next loss 1900 = 4100 → save path.
        var us = TeamOf(CsTeam.Terrorist, 2200, 2100, 2200, 2300, 2200);
        var rng = new Random(3);
        for (int i = 0; i < 50; i++)
        {
            var plan = BotStrategy.ClassifyTeam(us, TeamOf(CsTeam.CounterTerrorist, 4000), Ctx(lossIndex: 0), rng);
            Assert.Contains(plan.Round, new[] { BuyRound.Eco, BuyRound.Force });
        }
    }

    [Fact]
    public void Classify_AggressiveForcesMoreThanSaver()
    {
        var us = TeamOf(CsTeam.Terrorist, 2200, 2100, 2200, 2300, 2200);
        var enemy = TeamOf(CsTeam.CounterTerrorist, 4000);

        int saverForce = 0, aggroForce = 0;
        var rng = new Random(42);
        for (int i = 0; i < 400; i++)
        {
            if (BotStrategy.ClassifyTeam(us, enemy, Ctx(TeamTemperament.Saver, lossIndex: 0), rng).Round == BuyRound.Force)
                saverForce++;
            if (BotStrategy.ClassifyTeam(us, enemy, Ctx(TeamTemperament.Aggressive, lossIndex: 0), rng).Round == BuyRound.Force)
                aggroForce++;
        }
        Assert.True(aggroForce > saverForce * 2, $"aggressive {aggroForce} vs saver {saverForce}");
    }

    [Fact]
    public void Classify_WonVsBrokeEnemy_AntiEco()
    {
        var us = TeamOf(CsTeam.Terrorist, 3000, 2900, 3100, 2800, 3000);
        var enemy = TeamOf(CsTeam.CounterTerrorist, 2000, 1900, 2100, 2000, 2000);
        var plan = BotStrategy.ClassifyTeam(us, enemy, Ctx(lastWinner: CsTeam.Terrorist), new Random(4));
        Assert.Equal(BuyRound.AntiEco, plan.Round);
    }

    [Fact]
    public void EcoSpend_NeverExceedsCap()
    {
        // Fuzz: for any (money, lossIndex), eco spend must fit the hard cap.
        var rng = new Random(7);
        for (int i = 0; i < 3000; i++)
        {
            int money = rng.Next(0, 6000);
            int lossIndex = rng.Next(0, 5);
            bool leftover = rng.Next(3) == 0;
            var bot = Bot(money, leftoverRifle: leftover);
            var buy = BotStrategy.PickLoadout(bot, Plan(BuyRound.Eco), Plan(BuyRound.FullBuy), lossIndex, rng);
            int cap = money - BotStrategy.ReserveFor(CsTeam.Terrorist, bot, lossIndex);
            Assert.True(SpendOf(bot, buy) <= Math.Max(cap, 0),
                $"money={money} loss={lossIndex} cap={cap} spend={SpendOf(bot, buy)} primary={buy.Primary} pistol={buy.Pistol} armor={buy.Armor}");
        }
    }

    [Fact]
    public void Eco_BelowCapBuysNothing()
    {
        // money 2000, lossIndex 0 → reserve 1800 → cap 200: nothing but the default pistol.
        var bot = Bot(2000);
        for (int seed = 0; seed < 100; seed++)
        {
            var buy = BotStrategy.PickLoadout(bot, Plan(BuyRound.Eco), Plan(BuyRound.FullBuy), 0, new Random(seed));
            Assert.Null(buy.Primary);
            Assert.Equal(BotStrategy.DefaultPistol(CsTeam.Terrorist), buy.Pistol);
            Assert.Equal(ArmorIntent.Keep, buy.Armor);
        }
    }

    [Fact]
    public void Force_PoorBotFallsBackToPistol()
    {
        var bot = Bot(800);
        for (int seed = 0; seed < 100; seed++)
        {
            var buy = BotStrategy.PickLoadout(bot, Plan(BuyRound.Force), Plan(BuyRound.Eco), 4, new Random(seed));
            Assert.Null(buy.Primary);
            Assert.NotEqual(BotStrategy.DefaultPistol(CsTeam.Terrorist), buy.Pistol);
        }
    }

    [Fact]
    public void Force_BrokeBotGetsDefaultPistol()
    {
        var bot = Bot(100);
        var buy = BotStrategy.PickLoadout(bot, Plan(BuyRound.Force), Plan(BuyRound.Eco), 4, new Random(1));
        Assert.Null(buy.Primary);
        Assert.Equal(BotStrategy.DefaultPistol(CsTeam.Terrorist), buy.Pistol);
    }

    [Fact]
    public void Force_OnlyAffordablePrimaries()
    {
        var rng = new Random(9);
        for (int i = 0; i < 2000; i++)
        {
            int money = rng.Next(0, 4000);
            var bot = Bot(money, personality: (BotPersonality)rng.Next(4));
            var buy = BotStrategy.PickLoadout(bot, Plan(BuyRound.Force), Plan(BuyRound.Eco), 4, rng);
            if (buy.Primary != null)
                Assert.True(WeaponCatalog.PriceOf(buy.Primary) <= money,
                    $"money={money} got {buy.Primary}");
        }
    }

    [Fact]
    public void FullBuy_LowTierNeverGetsPrecisionGuns()
    {
        var rng = new Random(11);
        var banned = new HashSet<string> { "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1", "weapon_aug", "weapon_sg556", "weapon_deagle" };
        for (int i = 0; i < 1000; i++)
        {
            var bot = Bot(16000, tier: 0, personality: (BotPersonality)rng.Next(4));
            var buy = BotStrategy.PickLoadout(bot, Plan(BuyRound.FullBuy), Plan(BuyRound.FullBuy), 4, rng);
            Assert.NotNull(buy.Primary);
            Assert.DoesNotContain(buy.Primary!, banned);
        }
    }

    [Fact]
    public void FullBuy_AwpAtMostOnePerTeam()
    {
        var plan = Plan(BuyRound.FullBuy);
        var bots = new[]
        {
            Bot(16000, personality: BotPersonality.Sniper),
            Bot(16000, personality: BotPersonality.Sniper),
            Bot(16000, personality: BotPersonality.Sniper),
        };
        var rng = new Random(13);
        int awps = bots.Count(b => BotStrategy.PickLoadout(b, plan, Plan(BuyRound.Eco), 4, rng).Primary == "weapon_awp");
        Assert.True(awps <= 1, $"awps={awps}");
    }

    [Fact]
    public void FullBuy_LowTierNeverGetsAwp()
    {
        var rng = new Random(17);
        for (int i = 0; i < 500; i++)
        {
            var bot = Bot(16000, tier: 0, personality: BotPersonality.Sniper);
            var buy = BotStrategy.PickLoadout(bot, Plan(BuyRound.FullBuy), Plan(BuyRound.Eco), 4, rng);
            Assert.NotEqual("weapon_awp", buy.Primary);
        }
    }

    [Fact]
    public void FullBuy_CtSkipsHelmetVsTFullBuyWhenTight()
    {
        var ctTight = Bot(4500, CsTeam.CounterTerrorist);
        var ctRich = Bot(16000, CsTeam.CounterTerrorist);
        var enemyFull = Plan(BuyRound.FullBuy);
        var enemyEco = Plan(BuyRound.Eco);

        Assert.Equal(ArmorIntent.Kevlar,
            BotStrategy.PickLoadout(ctTight, Plan(BuyRound.FullBuy), enemyFull, 4, new Random(1)).Armor);
        Assert.Equal(ArmorIntent.Full,
            BotStrategy.PickLoadout(ctRich, Plan(BuyRound.FullBuy), enemyFull, 4, new Random(1)).Armor);
        Assert.Equal(ArmorIntent.Full,
            BotStrategy.PickLoadout(ctTight, Plan(BuyRound.FullBuy), enemyEco, 4, new Random(1)).Armor);
    }

    [Fact]
    public void FullBuy_OnlyAffordablePrimaries()
    {
        var rng = new Random(19);
        for (int i = 0; i < 2000; i++)
        {
            int money = rng.Next(1500, 16000);
            var bot = Bot(money,
                team: rng.Next(2) == 0 ? CsTeam.Terrorist : CsTeam.CounterTerrorist,
                personality: (BotPersonality)rng.Next(4),
                tier: rng.Next(3));
            var buy = BotStrategy.PickLoadout(bot, Plan(BuyRound.FullBuy), Plan(BuyRound.FullBuy), 4, rng);
            if (buy.Primary != null && buy.Primary != "weapon_awp")
            {
                int price = WeaponCatalog.PriceOf(buy.Primary);
                Assert.True(money >= price + (price >= 2900 ? 1000 : 650) || price <= 1950,
                    $"money={money} got {buy.Primary}");
            }
        }
    }

    [Fact]
    public void Temperament_RollsRoughQuarters()
    {
        var rng = new Random(23);
        var counts = new int[3];
        for (int i = 0; i < 4000; i++)
            counts[(int)BotStrategy.RollTemperament(rng)]++;
        Assert.InRange(counts[0], 800, 1200);   // Saver ~25%
        Assert.InRange(counts[1], 1800, 2200);  // Balanced ~50%
        Assert.InRange(counts[2], 800, 1200);   // Aggressive ~25%
    }

    [Fact]
    public void SkillTier_MapsAndFallsBackToMid()
    {
        Assert.Equal(1, BotStrategy.MapSkillTier(-1));
        Assert.Equal(0, BotStrategy.MapSkillTier(0));
        Assert.Equal(0, BotStrategy.MapSkillTier(1));
        Assert.Equal(1, BotStrategy.MapSkillTier(2));
        Assert.Equal(2, BotStrategy.MapSkillTier(3));
        Assert.Equal(2, BotStrategy.MapSkillTier(9));
    }

    [Fact]
    public void DropPool_RespectsMoneyAndTeam()
    {
        var rng = new Random(29);
        for (int i = 0; i < 500; i++)
        {
            var gun = BotStrategy.PickDroppedPrimary(CsTeam.Terrorist, 2800, false, rng);
            Assert.NotNull(gun);
            Assert.True(WeaponCatalog.PriceOf(gun!) <= 2800, $"got {gun}");
            Assert.True(WeaponCatalog.IsTeamLegal(gun!, CsTeam.Terrorist));
        }
    }

    [Fact]
    public void DropPool_CtBelow2900StillDrops()
    {
        // A CT with 2750 can't afford the M4 family; famas should appear.
        var gun = BotStrategy.PickDroppedPrimary(CsTeam.CounterTerrorist, 2750, false, new Random(31));
        Assert.Equal("weapon_famas", gun);
    }

    [Fact]
    public void Reserve_LeftoverRifleLowersFloor()
    {
        var poor = Bot(100);
        var rich = Bot(100, leftoverRifle: true);
        Assert.True(BotStrategy.ReserveFor(CsTeam.Terrorist, rich, 0) < BotStrategy.ReserveFor(CsTeam.Terrorist, poor, 0));
    }

    [Fact]
    public void ScriptedMatch_NoDegeneratePlans()
    {
        // A whole-half script: pistol → CT wins → streaks → mid-game money swings.
        var rng = new Random(37);
        int tLoss = 0, ctLoss = 0;
        CsTeam lastWinner = CsTeam.None;
        int[][] tMoney =
        [
            [800, 800, 800, 800, 800],
            [2500, 2400, 2300, 2600, 2400],
            [1000, 900, 1100, 1000, 900],
            [4200, 4100, 4300, 4000, 4200],
            [5200, 5300, 5100, 5400, 5000],
            [6000, 5800, 6200, 5900, 6100],
        ];
        int[][] ctMoney =
        [
            [800, 800, 800, 800, 800],
            [3400, 3300, 3500, 3400, 3400],
            [1500, 1600, 1400, 1500, 1600],
            [4600, 4500, 4700, 4600, 4500],
            [2400, 2500, 2300, 2400, 2500],
            [5600, 5700, 5500, 5600, 5700],
        ];
        var winners = new[] { CsTeam.CounterTerrorist, CsTeam.CounterTerrorist, CsTeam.Terrorist, CsTeam.CounterTerrorist, CsTeam.Terrorist };

        var tTemper = BotStrategy.RollTemperament(rng);
        var ctTemper = BotStrategy.RollTemperament(rng);
        for (int round = 0; round < tMoney.Length; round++)
        {
            bool firstOfHalf = round == 0;
            bool pistol = firstOfHalf && tMoney[round].Max() <= 1200;
            var tPlan = BotStrategy.ClassifyTeam(
                TeamOf(CsTeam.Terrorist, tMoney[round]), TeamOf(CsTeam.CounterTerrorist, ctMoney[round]),
                Ctx(tTemper, firstOfHalf, pistol, lastWinner, tLoss), rng);
            var ctPlan = BotStrategy.ClassifyTeam(
                TeamOf(CsTeam.CounterTerrorist, ctMoney[round]), TeamOf(CsTeam.Terrorist, tMoney[round]),
                Ctx(ctTemper, firstOfHalf, pistol, lastWinner, ctLoss), rng);

            // Picks must never throw and must respect each plan.
            foreach (var b in TeamOf(CsTeam.Terrorist, tMoney[round]))
                BotStrategy.PickLoadout(b, tPlan, ctPlan, tLoss, rng);
            foreach (var b in TeamOf(CsTeam.CounterTerrorist, ctMoney[round]))
                BotStrategy.PickLoadout(b, ctPlan, tPlan, ctLoss, rng);

            if (round > 0)
            {
                var loser = winners[round - 1] == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
                if (loser == CsTeam.Terrorist) tLoss = Math.Min(4, tLoss + 1); else tLoss = Math.Max(0, tLoss - 1);
                if (loser == CsTeam.CounterTerrorist) ctLoss = Math.Min(4, ctLoss + 1); else ctLoss = Math.Max(0, ctLoss - 1);
                lastWinner = winners[round - 1];
            }
        }
    }
}
