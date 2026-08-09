namespace Sleeper.Api.NflData.Analytics;

/// <summary>
/// Measures week-to-week scoring consistency using coefficient of variation,
/// boom/bust rates, and floor/ceiling analysis.
/// </summary>
public static class ConsistencyCalculator
{
    /// <summary>
    /// Calculate consistency score (0-100). Higher = more consistent.
    /// Based on inverse coefficient of variation, normalized to 0-100.
    /// </summary>
    public static decimal CalculateScore(List<decimal> weeklyPoints)
    {
        if (weeklyPoints.Count < 3) return 50m; // not enough data

        var mean = weeklyPoints.Average();
        if (mean <= 0) return 0m;

        var stdDev = CalculateStdDev(weeklyPoints);
        var cv = stdDev / mean; // coefficient of variation

        // CV of 0 = perfectly consistent (100), CV of 1.5+ = extremely volatile (0)
        // Scale: CV 0.3 = ~80, CV 0.5 = ~67, CV 0.8 = ~47, CV 1.0 = ~33, CV 1.5 = ~0
        var score = Math.Max(0m, Math.Min(100m, (1m - cv / 1.5m) * 100m));
        return Math.Round(score, 1);
    }

    /// <summary>
    /// Boom rate: % of weeks scoring above 1.5x the positional average.
    /// </summary>
    public static decimal CalculateBoomRate(List<decimal> weeklyPoints, decimal positionalAvgPpg)
    {
        if (weeklyPoints.Count == 0 || positionalAvgPpg <= 0) return 0m;

        var threshold = positionalAvgPpg * 1.5m;
        var booms = weeklyPoints.Count(p => p >= threshold);
        return Math.Round((decimal)booms / weeklyPoints.Count * 100m, 1);
    }

    /// <summary>
    /// Bust rate: % of weeks scoring below 0.5x the positional average.
    /// </summary>
    public static decimal CalculateBustRate(List<decimal> weeklyPoints, decimal positionalAvgPpg)
    {
        if (weeklyPoints.Count == 0 || positionalAvgPpg <= 0) return 0m;

        var threshold = positionalAvgPpg * 0.5m;
        var busts = weeklyPoints.Count(p => p < threshold);
        return Math.Round((decimal)busts / weeklyPoints.Count * 100m, 1);
    }

    /// <summary>
    /// Weekly floor: 25th percentile of weekly scores.
    /// </summary>
    public static decimal CalculateFloor(List<decimal> weeklyPoints)
    {
        if (weeklyPoints.Count == 0) return 0m;
        var sorted = weeklyPoints.OrderBy(p => p).ToList();
        var index = (int)(sorted.Count * 0.25);
        return sorted[Math.Min(index, sorted.Count - 1)];
    }

    /// <summary>
    /// Weekly ceiling: 75th percentile of weekly scores.
    /// </summary>
    public static decimal CalculateCeiling(List<decimal> weeklyPoints)
    {
        if (weeklyPoints.Count == 0) return 0m;
        var sorted = weeklyPoints.OrderBy(p => p).ToList();
        var index = (int)(sorted.Count * 0.75);
        return sorted[Math.Min(index, sorted.Count - 1)];
    }

    public static decimal CalculateStdDev(List<decimal> values)
    {
        if (values.Count < 2) return 0m;
        var mean = values.Average();
        var sumOfSquares = values.Sum(v => (v - mean) * (v - mean));
        return (decimal)Math.Sqrt((double)(sumOfSquares / (values.Count - 1)));
    }
}
