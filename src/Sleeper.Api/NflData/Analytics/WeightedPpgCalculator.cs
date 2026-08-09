namespace Sleeper.Api.NflData.Analytics;

/// <summary>
/// Calculates weighted points-per-game with recency bias.
/// More recent seasons are weighted heavier because player performance changes.
/// </summary>
public static class WeightedPpgCalculator
{
    /// <summary>
    /// Calculate weighted PPG from season summaries (most recent first).
    /// Default weights: Y-1=3, Y-2=2, Y-3=1. Seasons with fewer than minGames are excluded.
    /// </summary>
    public static decimal Calculate(List<SeasonSummary> seasons, int minGames = 4, decimal[]? weights = null)
    {
        weights ??= [3m, 2m, 1m];

        var eligible = seasons
            .Where(s => s.GamesPlayed >= minGames)
            .OrderByDescending(s => s.Season)
            .Take(weights.Length)
            .ToList();

        if (eligible.Count == 0)
            return 0m;

        decimal weightedSum = 0m;
        decimal weightTotal = 0m;

        for (int i = 0; i < eligible.Count; i++)
        {
            var w = weights[i];
            weightedSum += eligible[i].Ppg * w;
            weightTotal += w;
        }

        return weightTotal > 0 ? Math.Round(weightedSum / weightTotal, 2) : 0m;
    }
}
