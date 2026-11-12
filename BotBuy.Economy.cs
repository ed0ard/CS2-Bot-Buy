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
    public int SkillTier { get; init; } = 1;

    public BotInput ToBotInput()
    {
        return new BotInput
        {
            Team = Team,
            StartMoney = StartMoney,
            LeftoverRifle = LeftoverRifle,
            CurrentPrimary = CurrentPrimary,
            Personality = Personality,
            SkillTier = SkillTier,
        };
    }
}

internal sealed class TeamBuyPlan
{
    public BuyRound Round { get; init; }
    public int Median { get; init; }
    public int AwpSlots { get; set; }
    public TeamTemperament Temperament { get; init; }
}

public sealed partial class BotBuyPatch
{
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
}
