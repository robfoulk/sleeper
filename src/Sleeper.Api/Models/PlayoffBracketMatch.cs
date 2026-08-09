using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record PlayoffBracketMatch(
    [property: JsonPropertyName("r")] int Round,
    [property: JsonPropertyName("m")] int MatchId,
    [property: JsonPropertyName("t1")] int? Team1,
    [property: JsonPropertyName("t2")] int? Team2,
    [property: JsonPropertyName("w")] int? Winner,
    [property: JsonPropertyName("l")] int? Loser,
    [property: JsonPropertyName("t1_from")] BracketSource? Team1From,
    [property: JsonPropertyName("t2_from")] BracketSource? Team2From,
    [property: JsonPropertyName("p")] int? PlacementRank
);

public record BracketSource(
    [property: JsonPropertyName("w")] int? Winner,
    [property: JsonPropertyName("l")] int? Loser
);
