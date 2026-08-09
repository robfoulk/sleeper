namespace Sleeper.Api.NflData.Analytics;

/// <summary>
/// Complete analytical profile for a player's keeper value.
/// </summary>
public record PlayerAnalysis(
    string SleeperId,
    string? PlayerName,
    string? Position,
    int? Age,
    int? KeeperCostRound,
    bool CanBeKept,

    // Core projection
    decimal WeightedPpg,
    decimal AgeAdjustedPpg,
    decimal ProjectedSeasonPoints,

    // Positional value
    decimal ReplacementPpg,
    decimal Vorp,
    int PositionalRank,

    // Surplus
    decimal KeeperSurplus,

    // Risk/reliability
    decimal ConsistencyScore,
    decimal BoomRate,
    decimal BustRate,
    decimal DurabilityPct,

    // Trajectory
    decimal TrendPerYear,
    string TrendDirection,

    // Composite
    decimal KeeperScore,
    string KeeperGrade,

    // Supporting data
    List<SeasonSummary> SeasonHistory
);

/// <summary>
/// Summary of a single season's performance.
/// </summary>
public record SeasonSummary(
    int Season,
    int GamesPlayed,
    decimal TotalPoints,
    decimal Ppg,
    decimal StdDev
);
