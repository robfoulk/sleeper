using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record League(
    [property: JsonPropertyName("league_id")] string LeagueId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("sport")] string? Sport,
    [property: JsonPropertyName("season")] string? Season,
    [property: JsonPropertyName("season_type")] string? SeasonType,
    [property: JsonPropertyName("total_rosters")] int TotalRosters,
    [property: JsonPropertyName("draft_id")] string? DraftId,
    [property: JsonPropertyName("previous_league_id")] string? PreviousLeagueId,
    [property: JsonPropertyName("roster_positions")] List<string>? RosterPositions,
    [property: JsonPropertyName("settings")] Dictionary<string, JsonElement>? Settings,
    [property: JsonPropertyName("scoring_settings")] Dictionary<string, decimal>? ScoringSettings,
    [property: JsonPropertyName("avatar")] string? Avatar
)
{
    // Convenience: allow System.Text.Json.JsonElement usage
}
