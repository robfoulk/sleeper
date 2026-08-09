namespace Sleeper.Api.NflData.Analytics;

/// <summary>
/// Position-specific aging curves based on historical NFL data.
/// Returns a multiplier (0.0-1.1) representing expected production relative to peak.
/// </summary>
public static class AgingCurve
{
    /// <summary>
    /// Get age adjustment factor for a position. Values > 1.0 mean ascending, &lt; 1.0 declining.
    /// </summary>
    public static decimal GetFactor(string? position, int? age)
    {
        if (position is null || age is null)
            return 1.0m;

        return position.ToUpperInvariant() switch
        {
            "QB" => QbCurve(age.Value),
            "RB" => RbCurve(age.Value),
            "WR" => WrCurve(age.Value),
            "TE" => TeCurve(age.Value),
            "K" => KCurve(age.Value),
            _ => 1.0m
        };
    }

    // QB: Long plateau 26-33, slow decline 34-37, steeper after
    private static decimal QbCurve(int age) => age switch
    {
        <= 23 => 0.80m,
        24 => 0.88m,
        25 => 0.94m,
        >= 26 and <= 33 => 1.00m,
        34 => 0.97m,
        35 => 0.94m,
        36 => 0.90m,
        37 => 0.85m,
        38 => 0.78m,
        39 => 0.70m,
        >= 40 => 0.60m,
    };

    // RB: Peaks 23-26, sharpest decline in fantasy sports
    private static decimal RbCurve(int age) => age switch
    {
        <= 21 => 0.85m,
        22 => 0.93m,
        23 => 1.00m,
        24 => 1.03m,
        25 => 1.02m,
        26 => 0.97m,
        27 => 0.90m,
        28 => 0.80m,
        29 => 0.70m,
        30 => 0.58m,
        31 => 0.48m,
        >= 32 => 0.35m,
    };

    // WR: Peaks 25-29, route runners age better
    private static decimal WrCurve(int age) => age switch
    {
        <= 21 => 0.75m,
        22 => 0.85m,
        23 => 0.92m,
        24 => 0.97m,
        25 => 1.00m,
        26 => 1.02m,
        27 => 1.03m,
        28 => 1.01m,
        29 => 0.97m,
        30 => 0.91m,
        31 => 0.84m,
        32 => 0.75m,
        33 => 0.65m,
        >= 34 => 0.50m,
    };

    // TE: Late bloomers, peak 26-30
    private static decimal TeCurve(int age) => age switch
    {
        <= 22 => 0.70m,
        23 => 0.78m,
        24 => 0.86m,
        25 => 0.93m,
        26 => 0.98m,
        27 => 1.00m,
        28 => 1.02m,
        29 => 1.01m,
        30 => 0.97m,
        31 => 0.91m,
        32 => 0.83m,
        33 => 0.73m,
        >= 34 => 0.60m,
    };

    // K: Nearly flat, very long career
    private static decimal KCurve(int age) => age switch
    {
        <= 24 => 0.95m,
        >= 25 and <= 35 => 1.00m,
        36 => 0.97m,
        37 => 0.94m,
        38 => 0.90m,
        >= 39 => 0.82m,
    };
}
