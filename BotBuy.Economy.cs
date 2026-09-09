using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotBuyPatch;

internal enum BuyRound
{
    Pistol,
    Eco,
    LightForce,
    Force,
    AntiEco,
    FullBuy,
}

internal enum BotPersonality
{
    Rifle,
    Sniper,
    Rusher,
    Camper,
}

internal sealed class PlayerSnap
{
    public CCSPlayerController Player { get; init; } = null!;
    public CsTeam Team { get; init; }
    public int StartMoney { get; init; }
    public bool LeftoverRifle { get; init; }
    public string? CurrentPrimary { get; init; }
    public BotPersonality Personality { get; init; }
}

internal sealed class TeamBuyPlan
{
    public BuyRound Round { get; init; }
    public int Median { get; init; }
    public int AwpSlots { get; set; }
}

public sealed partial class BotBuyPatch
{
    private static readonly int[] LossBonus = [1400, 1900, 2400, 2900, 3400];

    private TeamBuyPlan ClassifyTeam(List<PlayerSnap> us, List<PlayerSnap> enemy)
    {
        if (us.Count == 0)
            return new TeamBuyPlan { Round = BuyRound.Eco, Median = 0 };

        var team = us[0].Team;
        int rifleCost = team == CsTeam.Terrorist ? 2700 : 2900;
        int fullBuyFloor = rifleCost + 1000;
        int median = Median(us.Select(p => p.StartMoney).ToList());
        int enemyMedian = enemy.Count == 0 ? 0 : Median(enemy.Select(p => p.StartMoney).ToList());

        bool pistol = _pistolThisRound || (IsFirstRoundOfHalf() && median <= 1200);
        if (pistol)
            return new TeamBuyPlan { Round = BuyRound.Pistol, Median = median };

        int canFull = us.Count(p => p.LeftoverRifle || p.StartMoney >= fullBuyFloor);
        int leftoverRifles = us.Count(p => p.LeftoverRifle);
        bool majorityFull = canFull * 5 >= us.Count * 3;
        bool weWon = _lastWinner == team;
        bool enemyEco = enemyMedian < 2500 && enemy.Count(p => p.LeftoverRifle) <= 1;

        if (weWon && enemyEco && leftoverRifles * 2 < us.Count && median >= 2000)
            return new TeamBuyPlan { Round = BuyRound.AntiEco, Median = median };

        if (majorityFull || median >= fullBuyFloor)
            return new TeamBuyPlan { Round = BuyRound.FullBuy, Median = median, AwpSlots = 1 };

        int nextIfLose = median + NextLossPayout(team);
        bool canFullNextIfSave = nextIfLose >= fullBuyFloor;

        if (canFullNextIfSave)
        {
            // Unfrozen mix: a saving team sometimes still force-buys together.
            if (median >= 2000 && Random.Shared.NextSingle() < 0.22f)
                return new TeamBuyPlan { Round = BuyRound.Force, Median = median };
            return new TeamBuyPlan { Round = BuyRound.Eco, Median = median };
        }

        if (median >= 1800)
            return new TeamBuyPlan { Round = BuyRound.Force, Median = median };
        if (median >= 1200)
            return new TeamBuyPlan { Round = BuyRound.LightForce, Median = median };
        return new TeamBuyPlan { Round = BuyRound.Eco, Median = median };
    }

    private int NextLossPayout(CsTeam team)
    {
        int index = _lossIndex.GetValueOrDefault(team);
        return LossBonus[Math.Min(4, index + 1)];
    }

    private void UpdateLossBonus(CsTeam winner)
    {
        if (winner != CsTeam.Terrorist && winner != CsTeam.CounterTerrorist)
            return;

        var loser = winner == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        if (_pistolThisRound)
            _lossIndex[loser] = Math.Max(_lossIndex.GetValueOrDefault(loser), 1);
        else
            _lossIndex[loser] = Math.Min(4, _lossIndex.GetValueOrDefault(loser) + 1);

        _lossIndex[winner] = Math.Max(0, _lossIndex.GetValueOrDefault(winner) - 1);
        _lastWinner = winner;
    }

    private static int Median(List<int> values)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        int mid = values.Count / 2;
        if (values.Count % 2 == 1)
            return values[mid];
        return (values[mid - 1] + values[mid]) / 2;
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
