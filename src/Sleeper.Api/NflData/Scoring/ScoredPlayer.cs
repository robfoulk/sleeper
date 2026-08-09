namespace Sleeper.Api.NflData.Scoring;

/// <summary>
/// Result of scoring a player's stats against a league's scoring settings.
/// </summary>
public record ScoredPlayer(
    string PlayerId,
    string? PlayerName,
    string? Position,
    decimal TotalPoints,
    List<ScoreComponent> Breakdown
);

/// <summary>
/// A single scoring component showing how points were earned.
/// </summary>
public record ScoreComponent(
    string Category,
    string Label,
    decimal StatValue,
    decimal PointsPerUnit,
    decimal Points
);
