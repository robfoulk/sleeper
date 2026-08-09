namespace Sleeper.Api.Models;

public record MatchupWithNames(
    int? MatchupId,
    string? Team1DisplayName,
    string? Team1TeamName,
    int Team1RosterId,
    decimal? Team1Points,
    string? Team2DisplayName,
    string? Team2TeamName,
    int? Team2RosterId,
    decimal? Team2Points
);
