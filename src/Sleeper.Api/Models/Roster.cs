using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record Roster(
    [property: JsonPropertyName("roster_id")] int RosterId,
    [property: JsonPropertyName("owner_id")] string? OwnerId,
    [property: JsonPropertyName("league_id")] string? LeagueId,
    [property: JsonPropertyName("players")] List<string>? Players,
    [property: JsonPropertyName("starters")] List<string>? Starters,
    [property: JsonPropertyName("reserve")] List<string>? Reserve,
    [property: JsonPropertyName("co_owners")] List<string>? CoOwners,
    [property: JsonPropertyName("settings")] RosterSettings? Settings,
    [property: JsonPropertyName("keepers")] List<string>? Keepers = null
);

public record RosterSettings(
    [property: JsonPropertyName("wins")] int Wins,
    [property: JsonPropertyName("losses")] int Losses,
    [property: JsonPropertyName("ties")] int Ties,
    [property: JsonPropertyName("fpts")] decimal Fpts,
    [property: JsonPropertyName("fpts_decimal")] decimal FptsDecimal,
    [property: JsonPropertyName("fpts_against")] decimal FptsAgainst,
    [property: JsonPropertyName("fpts_against_decimal")] decimal FptsAgainstDecimal,
    [property: JsonPropertyName("total_moves")] int TotalMoves,
    [property: JsonPropertyName("waiver_position")] int WaiverPosition,
    [property: JsonPropertyName("waiver_budget_used")] int WaiverBudgetUsed
);
