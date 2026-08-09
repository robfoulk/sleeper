using Sleeper.Api.NflData.Scoring;

namespace Sleeper.Api.NflData.Analytics;

/// <summary>
/// Composes all analytics calculators to produce a complete keeper analysis for a player.
/// Takes scored season data and produces a PlayerAnalysis with projections, VORP,
/// surplus, consistency, durability, trend, and composite keeper score.
/// </summary>
public class KeeperAnalyzer
{
    private readonly Dictionary<string, decimal> _replacementLevels;

    public KeeperAnalyzer(Dictionary<string, decimal>? replacementLevels = null)
    {
        _replacementLevels = replacementLevels ?? VorpCalculator.DefaultReplacementPpg;
    }

    /// <summary>
    /// Analyze a player for keeper value.
    /// </summary>
    /// <param name="sleeperId">Sleeper player ID</param>
    /// <param name="playerName">Display name</param>
    /// <param name="position">QB, RB, WR, TE, K</param>
    /// <param name="age">Current age (null if unknown)</param>
    /// <param name="keeperCostRound">Round to keep them (null if can't keep)</param>
    /// <param name="canBeKept">Whether the player is eligible to be kept</param>
    /// <param name="seasonScores">Scored seasons (most recent first preferred), each containing weekly breakdowns</param>
    /// <param name="weeklyPointsBySeason">Weekly point totals per season for consistency calc</param>
    /// <param name="gamesInSeason">Games in the upcoming season (17 for 2021+, 16 before)</param>
    public PlayerAnalysis Analyze(
        string sleeperId,
        string? playerName,
        string? position,
        int? age,
        int? keeperCostRound,
        bool canBeKept,
        List<SeasonSummary> seasonHistory,
        List<decimal>? recentWeeklyPoints = null,
        int gamesInSeason = 17)
    {
        // 1. Weighted PPG
        var wPpg = WeightedPpgCalculator.Calculate(seasonHistory);

        // 2. Age adjustment
        var ageFactor = AgingCurve.GetFactor(position, age);
        var ageAdjPpg = Math.Round(wPpg * ageFactor, 2);

        // 3. Durability
        var durabilityPct = DurabilityCalculator.Calculate(seasonHistory);
        var durabilityMult = DurabilityCalculator.GetMultiplier(durabilityPct);

        // 4. Trend
        var eligibleSeasons = seasonHistory.Count(s => s.GamesPlayed >= 4);
        var trendPerYear = TrendCalculator.CalculateTrendPerYear(seasonHistory);
        var trendDirection = TrendCalculator.GetDirection(trendPerYear, eligibleSeasons);
        var trendAdj = TrendCalculator.GetTrendAdjustment(trendPerYear);

        // 5. Final projected PPG (age + durability + trend adjustments)
        var projectedPpg = Math.Round(ageAdjPpg * durabilityMult * (1m + trendAdj), 2);
        var projectedSeasonPts = Math.Round(projectedPpg * gamesInSeason, 1);

        // 6. VORP
        var vorp = VorpCalculator.Calculate(projectedPpg, position, _replacementLevels);

        // 7. Positional rank (by projected PPG — will be filled in by the caller if batch analyzing)
        var positionalRank = 0;

        // 8. Keeper surplus
        var surplus = canBeKept
            ? SurplusCalculator.Calculate(projectedPpg, keeperCostRound)
            : 0m;

        // 9. Consistency
        var weeklyPts = recentWeeklyPoints ?? [];
        var consistencyScore = ConsistencyCalculator.CalculateScore(weeklyPts);
        var posAvg = _replacementLevels.GetValueOrDefault(position?.ToUpperInvariant() ?? "", 8m);
        var boomRate = ConsistencyCalculator.CalculateBoomRate(weeklyPts, posAvg);
        var bustRate = ConsistencyCalculator.CalculateBustRate(weeklyPts, posAvg);

        // 10. Composite keeper score
        var keeperScore = CalculateCompositeScore(
            vorp, surplus, consistencyScore, durabilityPct, keeperCostRound, canBeKept);
        var keeperGrade = GetGrade(keeperScore);

        return new PlayerAnalysis(
            SleeperId: sleeperId,
            PlayerName: playerName,
            Position: position,
            Age: age,
            KeeperCostRound: keeperCostRound,
            CanBeKept: canBeKept,
            WeightedPpg: wPpg,
            AgeAdjustedPpg: ageAdjPpg,
            ProjectedSeasonPoints: projectedSeasonPts,
            ReplacementPpg: _replacementLevels.GetValueOrDefault(position?.ToUpperInvariant() ?? "", 0m),
            Vorp: vorp,
            PositionalRank: positionalRank,
            KeeperSurplus: surplus,
            ConsistencyScore: consistencyScore,
            BoomRate: boomRate,
            BustRate: bustRate,
            DurabilityPct: durabilityPct,
            TrendPerYear: trendPerYear,
            TrendDirection: trendDirection,
            KeeperScore: keeperScore,
            KeeperGrade: keeperGrade,
            SeasonHistory: seasonHistory
        );
    }

    /// <summary>
    /// Composite keeper score that balances all factors.
    /// Higher = better keeper candidate.
    /// </summary>
    private static decimal CalculateCompositeScore(
        decimal vorp, decimal surplus, decimal consistencyScore,
        decimal durabilityPct, int? keeperCostRound, bool canBeKept)
    {
        if (!canBeKept || keeperCostRound is null || keeperCostRound <= 0)
            return 0m;

        // Players with negative VORP get their surplus heavily capped —
        // a below-replacement player is never a "smash keep" no matter how cheap
        var effectiveSurplus = surplus;
        if (vorp < 0)
        {
            // Scale: VORP -1 => 60% surplus, VORP -3 => 20% surplus, VORP -5+ => 10% surplus
            var dampFactor = Math.Max(0.1m, 1m + vorp * 0.2m);
            effectiveSurplus = surplus * dampFactor;
        }

        // Surplus is the primary driver (50% weight)
        var surplusComponent = effectiveSurplus * 0.5m;

        // VORP as secondary (30%) — positional scarcity matters more than raw surplus
        var vorpComponent = vorp * 0.3m;

        // Consistency bonus (10%) — reliable players are worth more as keepers
        var consistencyComponent = (consistencyScore / 100m) * 2m * 0.1m;

        // Durability bonus (10%) — keepers who play all season
        var durabilityComponent = (durabilityPct / 100m) * 2m * 0.1m;

        var raw = surplusComponent + vorpComponent + consistencyComponent + durabilityComponent;

        // Mild cost scaling — cheaper keepers get a small boost, but not extreme
        var costMultiplier = 1.0m + (keeperCostRound.Value - 8m) * 0.01m;
        costMultiplier = Math.Max(0.9m, Math.Min(1.2m, costMultiplier));

        return Math.Round(raw * costMultiplier, 2);
    }

    private static string GetGrade(decimal keeperScore)
    {
        return keeperScore switch
        {
            > 5.0m => "S",     // Smash keep — elite value
            > 3.0m => "A",     // Strong keep
            > 1.5m => "B",     // Good keep
            > 0.5m => "C",     // Marginal — depends on alternatives
            > -0.5m => "D",    // Probably don't keep
            _ => "F"           // Definitely don't keep
        };
    }
}
