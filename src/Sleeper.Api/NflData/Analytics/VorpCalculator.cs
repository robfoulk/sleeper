namespace Sleeper.Api.NflData.Analytics;

/// <summary>
/// Calculates Value Over Replacement Player (VORP).
/// Measures how much better a player is than the best freely available player at their position.
/// Replacement level depends on league size and starter slots.
/// </summary>
public static class VorpCalculator
{
    /// <summary>
    /// Default replacement-level PPG by position for a standard 1-QB, 8-10 team league.
    /// These represent the PPG of the borderline-startable player at each position.
    /// </summary>
    public static readonly Dictionary<string, decimal> DefaultReplacementPpg = new()
    {
        ["QB"] = 14.0m,
        ["RB"] = 7.5m,
        ["WR"] = 8.0m,
        ["TE"] = 5.5m,
        ["K"] = 6.0m,
    };

    /// <summary>
    /// Estimate replacement-level PPG from league roster config.
    /// More starter slots = deeper position = lower replacement level.
    /// Uses effective starters (including flex) to determine scarcity.
    /// </summary>
    public static Dictionary<string, decimal> EstimateReplacementLevels(
        int teams,
        Dictionary<string, int> starterSlots,
        int flexSlots = 0,
        IReadOnlySet<string>? flexEligiblePositions = null)
    {
        flexEligiblePositions ??= new HashSet<string>(["RB", "WR", "TE"], StringComparer.OrdinalIgnoreCase);

        // Baseline PPG pools by position (approximate top-N PPG at each position)
        // These represent a smooth curve of expected PPG by positional rank
        return new Dictionary<string, decimal>
        {
            ["QB"] = EstimateReplacement(teams * starterSlots.GetValueOrDefault("QB", 1), PositionDepthCurves.Qb),
            ["RB"] = EstimateReplacement(teams * EffectiveSlots("RB", starterSlots, flexSlots, flexEligiblePositions), PositionDepthCurves.Rb),
            ["WR"] = EstimateReplacement(teams * EffectiveSlots("WR", starterSlots, flexSlots, flexEligiblePositions), PositionDepthCurves.Wr),
            ["TE"] = EstimateReplacement(teams * EffectiveSlots("TE", starterSlots, flexSlots, flexEligiblePositions), PositionDepthCurves.Te),
            ["K"] = EstimateReplacement(teams * starterSlots.GetValueOrDefault("K", 1), PositionDepthCurves.K),
        };
    }

    private static int EffectiveSlots(
        string position,
        Dictionary<string, int> starterSlots,
        int flexSlots,
        IReadOnlySet<string> flexEligiblePositions) =>
        starterSlots.GetValueOrDefault(position, position == "TE" ? 1 : 2)
        + (flexEligiblePositions.Contains(position) ? flexSlots : 0);

    private static decimal EstimateReplacement(int startersNeeded, decimal[] curve)
    {
        if (startersNeeded <= 0) return 0m;
        var idx = Math.Min(startersNeeded, curve.Length - 1);
        return curve[idx];
    }

    /// <summary>
    /// Approximate PPG by positional rank (index = rank, 0-based).
    /// Derived from historical fantasy scoring distributions.
    /// </summary>
    private static class PositionDepthCurves
    {
        // QB1 through QB32
        public static readonly decimal[] Qb =
        [
            24m, 23m, 22m, 21m, 20.5m, 20m, 19.5m, 19m, 18.5m, 18m,
            17.5m, 17m, 16.5m, 16m, 15.5m, 15m, 14.5m, 14m, 13.5m, 13m,
            12.5m, 12m, 11.5m, 11m, 10.5m, 10m, 9.5m, 9m, 8.5m, 8m, 7.5m, 7m
        ];

        // RB1 through RB48
        public static readonly decimal[] Rb =
        [
            18m, 16m, 15m, 14m, 13.5m, 13m, 12.5m, 12m, 11.5m, 11m,
            10.5m, 10m, 9.5m, 9m, 8.5m, 8m, 7.5m, 7m, 6.5m, 6m,
            5.5m, 5m, 4.5m, 4m, 3.8m, 3.5m, 3.2m, 3m, 2.8m, 2.5m,
            2.3m, 2m, 1.8m, 1.5m, 1.3m, 1m, 1m, 1m, 1m, 1m,
            1m, 1m, 1m, 1m, 1m, 1m, 1m, 1m
        ];

        // WR1 through WR48
        public static readonly decimal[] Wr =
        [
            17m, 15.5m, 14.5m, 14m, 13.5m, 13m, 12.5m, 12m, 11.5m, 11m,
            10.5m, 10m, 9.5m, 9m, 8.5m, 8m, 7.5m, 7m, 6.5m, 6m,
            5.5m, 5m, 4.5m, 4m, 3.8m, 3.5m, 3.2m, 3m, 2.8m, 2.5m,
            2.3m, 2m, 1.8m, 1.5m, 1.3m, 1m, 1m, 1m, 1m, 1m,
            1m, 1m, 1m, 1m, 1m, 1m, 1m, 1m
        ];

        // TE1 through TE24
        public static readonly decimal[] Te =
        [
            12m, 10m, 9m, 8.5m, 8m, 7.5m, 7m, 6.5m, 6m, 5.5m,
            5m, 4.5m, 4m, 3.5m, 3m, 2.5m, 2m, 1.5m, 1m, 1m,
            1m, 1m, 1m, 1m
        ];

        // K1 through K16
        public static readonly decimal[] K =
        [
            9m, 8.5m, 8m, 7.5m, 7.2m, 7m, 6.8m, 6.5m, 6.2m, 6m,
            5.8m, 5.5m, 5.2m, 5m, 4.8m, 4.5m
        ];
    }

    /// <summary>
    /// Calculate VORP = player's projected PPG minus replacement-level PPG for their position.
    /// </summary>
    public static decimal Calculate(decimal projectedPpg, string? position, Dictionary<string, decimal>? replacementLevels = null)
    {
        replacementLevels ??= DefaultReplacementPpg;

        if (position is null) return 0m;

        var pos = position.ToUpperInvariant();
        var replacement = replacementLevels.GetValueOrDefault(pos, 0m);

        return Math.Round(projectedPpg - replacement, 2);
    }

    /// <summary>
    /// Calculate replacement levels from actual league data.
    /// Takes all player PPGs grouped by position and uses the Nth-ranked as replacement
    /// where N = teams * starterSlots for that position.
    /// </summary>
    public static Dictionary<string, decimal> CalculateReplacementLevels(
        Dictionary<string, List<decimal>> ppgByPosition,
        int teams,
        Dictionary<string, int>? starterSlots = null)
    {
        starterSlots ??= new Dictionary<string, int>
        {
            ["QB"] = 1, ["RB"] = 2, ["WR"] = 2, ["TE"] = 1, ["K"] = 1,
        };

        var result = new Dictionary<string, decimal>();

        foreach (var (pos, ppgs) in ppgByPosition)
        {
            var slots = starterSlots.GetValueOrDefault(pos, 1);
            var replacementIndex = teams * slots;

            var sorted = ppgs.OrderByDescending(p => p).ToList();

            // Replacement = the player just outside starter range
            result[pos] = replacementIndex < sorted.Count
                ? Math.Round(sorted[replacementIndex], 2)
                : 0m;
        }

        return result;
    }
}
