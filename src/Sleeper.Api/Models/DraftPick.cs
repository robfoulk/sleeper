using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record DraftPick(
    [property: JsonPropertyName("player_id")] string? PlayerId,
    [property: JsonPropertyName("picked_by")] string? PickedBy,
    [property: JsonPropertyName("roster_id")] int? RosterId,
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("draft_slot")] int DraftSlot,
    [property: JsonPropertyName("pick_no")] int PickNo,
    [property: JsonPropertyName("is_keeper")] bool? IsKeeper,
    [property: JsonPropertyName("draft_id")] string? DraftId,
    [property: JsonPropertyName("metadata")] DraftPickMetadata? Metadata
);

public record DraftPickMetadata(
    [property: JsonPropertyName("player_id")] string? PlayerId,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")] string? LastName,
    [property: JsonPropertyName("position")] string? Position,
    [property: JsonPropertyName("team")] string? Team,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("sport")] string? Sport,
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("injury_status")] string? InjuryStatus,
    [property: JsonPropertyName("news_updated")] string? NewsUpdated
);
