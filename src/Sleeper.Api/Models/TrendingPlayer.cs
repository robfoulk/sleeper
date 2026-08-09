using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record TrendingPlayer(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("count")] int Count
);
