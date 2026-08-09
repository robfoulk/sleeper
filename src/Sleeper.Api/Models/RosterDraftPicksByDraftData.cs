using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

internal sealed record RosterDraftPicksByDraftData(
    [property: JsonPropertyName("roster_draft_picks_by_draft")] List<RosterDraftPickOwner> RosterDraftPicksByDraft);

public sealed record RosterDraftPickOwner(
    [property: JsonPropertyName("season")] string? Season,
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("roster_id")] int RosterId,
    [property: JsonPropertyName("owner_id")] int OwnerId);

