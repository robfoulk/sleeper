namespace Sleeper.Api.Models;

public record KeeperValue(
    Player Player,
    bool IsStarter,
    bool IsReserve,
    int? DraftRound,
    int? DraftPickNumber,
    bool WasKeptLastYear,
    int? KeeperCostRound,
    bool CanBeKept
)
{
    /// <summary>
    /// Default keeper value formula: Round Drafted - 3.
    /// Rounds 1-3 cannot be kept. Undrafted players cost round 10.
    /// </summary>
    public static (int? costRound, bool canBeKept) Calculate(int? draftRound, int undraftedCost = 10)
    {
        if (draftRound is null)
            return (undraftedCost, true);

        if (draftRound <= 3)
            return (null, false);

        return (draftRound.Value - 3, true);
    }
}
