using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record LeagueUser(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("avatar")] string? Avatar,
    [property: JsonPropertyName("is_owner")] bool? IsOwner,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata
);
