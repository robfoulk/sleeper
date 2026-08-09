using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record TradedPick(
    [property: JsonPropertyName("season")] string? Season,
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("roster_id")] int RosterId,
    [property: JsonPropertyName("previous_owner_id")] int PreviousOwnerId,
    [property: JsonPropertyName("owner_id")] int OwnerId
);
