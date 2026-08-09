using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record Matchup(
    [property: JsonPropertyName("roster_id")] int RosterId,
    [property: JsonPropertyName("matchup_id")] int? MatchupId,
    [property: JsonPropertyName("points")] decimal? Points,
    [property: JsonPropertyName("custom_points")] decimal? CustomPoints,
    [property: JsonPropertyName("starters")] List<string>? Starters,
    [property: JsonPropertyName("players")] List<string>? Players,
    [property: JsonPropertyName("starters_points")] List<decimal>? StartersPoints,
    [property: JsonPropertyName("players_points")] Dictionary<string, decimal>? PlayersPoints
);
