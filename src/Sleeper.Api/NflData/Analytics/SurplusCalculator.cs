namespace Sleeper.Api.NflData.Analytics;

/// <summary>
/// Calculates keeper surplus: the difference between a player's projected value
/// and the value you'd expect from drafting at their keeper cost round.
/// Positive surplus = good keeper. Negative surplus = don't keep.
/// </summary>
public static class SurplusCalculator
{
    /// <summary>
    /// Expected PPG by draft round for a standard league.
    /// Round 1 picks are expected to score the most; late rounds very little.
    /// Derived from historical draft pick PPG averages.
    /// </summary>
    public static readonly Dictionary<int, decimal> DefaultDraftRoundValue = new()
    {
        [1] = 16.0m,
        [2] = 14.0m,
        [3] = 12.5m,
        [4] = 11.0m,
        [5] = 10.0m,
        [6] = 9.0m,
        [7] = 8.0m,
        [8] = 7.0m,
        [9] = 6.0m,
        [10] = 5.5m,
        [11] = 5.0m,
        [12] = 4.5m,
        [13] = 4.0m,
        [14] = 3.5m,
        [15] = 3.0m,
    };

    /// <summary>
    /// Calculate keeper surplus: projected PPG minus expected PPG from the draft round you'd spend.
    /// </summary>
    public static decimal Calculate(decimal projectedPpg, int? keeperCostRound, Dictionary<int, decimal>? draftRoundValues = null)
    {
        if (keeperCostRound is null) return 0m;

        draftRoundValues ??= DefaultDraftRoundValue;

        // For rounds beyond the table, interpolate down to ~1.0 PPG
        decimal draftValue;
        if (draftRoundValues.TryGetValue(keeperCostRound.Value, out var v))
            draftValue = v;
        else if (keeperCostRound.Value > 15)
            draftValue = Math.Max(1.0m, 3.0m - (keeperCostRound.Value - 15) * 0.2m);
        else
            draftValue = 0m;

        return Math.Round(projectedPpg - draftValue, 2);
    }

    /// <summary>
    /// Calculate surplus in season-total points (PPG surplus × games in season).
    /// </summary>
    public static decimal CalculateSeasonSurplus(decimal projectedPpg, int? keeperCostRound, int gamesInSeason = 17, Dictionary<int, decimal>? draftRoundValues = null)
    {
        return Math.Round(Calculate(projectedPpg, keeperCostRound, draftRoundValues) * gamesInSeason, 1);
    }
}
