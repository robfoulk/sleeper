namespace Sleeper.Api.NflData.Analytics;

/// <summary>
/// Calculates year-over-year trend to detect ascending/declining trajectories.
/// Breakout players have positive trends; aging players have negative trends.
/// </summary>
public static class TrendCalculator
{
    /// <summary>
    /// Calculate average PPG change per year using linear regression on season PPGs.
    /// Positive = ascending, Negative = declining, Near-zero = stable.
    /// </summary>
    public static decimal CalculateTrendPerYear(List<SeasonSummary> seasons, int minGames = 4)
    {
        var eligible = seasons
            .Where(s => s.GamesPlayed >= minGames)
            .OrderBy(s => s.Season)
            .ToList();

        if (eligible.Count < 2) return 0m;

        // Simple linear regression: slope of PPG over seasons
        var n = eligible.Count;
        var xMean = eligible.Average(s => (decimal)s.Season);
        var yMean = eligible.Average(s => s.Ppg);

        decimal numerator = 0m;
        decimal denominator = 0m;

        foreach (var s in eligible)
        {
            var xDiff = s.Season - xMean;
            var yDiff = s.Ppg - yMean;
            numerator += xDiff * yDiff;
            denominator += xDiff * xDiff;
        }

        if (denominator == 0m) return 0m;

        var rawSlope = numerator / denominator;

        // Dampen trend when we have few data points — 2 seasons is noisy
        var confidence = eligible.Count switch
        {
            2 => 0.5m,   // 2 seasons: halve the trend (very noisy)
            3 => 0.8m,   // 3 seasons: slight dampening
            _ => 1.0m    // 4+ seasons: full confidence
        };

        return Math.Round(rawSlope * confidence, 2);
    }

    /// <summary>
    /// Get a human-readable trend direction. Takes season count for context.
    /// </summary>
    public static string GetDirection(decimal trendPerYear, int seasonCount = 3)
    {
        if (seasonCount < 2)
            return "1yr Data";

        return trendPerYear switch
        {
            > 2.0m => "Strong Rise",
            > 0.5m => "Rising",
            >= -0.5m => "Stable",
            >= -2.0m => "Declining",
            _ => "Sharp Decline"
        };
    }

    /// <summary>
    /// Get a trend bonus/penalty to apply to projections.
    /// Ascending players get a small boost; declining players get a small penalty.
    /// Capped to prevent overreaction.
    /// </summary>
    public static decimal GetTrendAdjustment(decimal trendPerYear)
    {
        // Cap at ±15% adjustment
        var adjustment = trendPerYear * 0.05m;
        return Math.Max(-0.15m, Math.Min(0.15m, adjustment));
    }
}
