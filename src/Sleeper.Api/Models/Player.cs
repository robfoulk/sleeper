using System.Text.Json.Serialization;
using Sleeper.Api.Converters;

namespace Sleeper.Api.Models;

public record Player(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")] string? LastName,
    [property: JsonPropertyName("position")] string? Position,
    [property: JsonPropertyName("team")] string? Team,
    [property: JsonPropertyName("age")] int? Age,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("number")] int? Number,
    [property: JsonPropertyName("college")] string? College,
    [property: JsonPropertyName("years_exp")] int? YearsExp,
    [property: JsonPropertyName("fantasy_positions")] List<string>? FantasyPositions,
    [property: JsonPropertyName("injury_status")] string? InjuryStatus,
    [property: JsonPropertyName("weight")] string? Weight,
    [property: JsonPropertyName("height")] string? Height,
    [property: JsonPropertyName("search_full_name")] string? SearchFullName,
    [property: JsonPropertyName("search_first_name")] string? SearchFirstName,
    [property: JsonPropertyName("search_last_name")] string? SearchLastName,
    [property: JsonPropertyName("search_rank")] int? SearchRank,
    [property: JsonPropertyName("depth_chart_position")] string? DepthChartPosition,
    [property: JsonPropertyName("depth_chart_order")] int? DepthChartOrder,
    [property: JsonPropertyName("sport")] string? Sport,
    [property: JsonPropertyName("hashtag")] string? Hashtag,
    [property: JsonPropertyName("fantasy_data_id")] int? FantasyDataId,
    [property: JsonPropertyName("birth_country")] string? BirthCountry,
    [property: JsonPropertyName("espn_id"), JsonConverter(typeof(FlexibleStringConverter))] string? EspnId,
    [property: JsonPropertyName("yahoo_id")] int? YahooId,
    [property: JsonPropertyName("rotowire_id")] int? RotowireId,
    [property: JsonPropertyName("rotoworld_id")] int? RotoworldId,
    [property: JsonPropertyName("sportradar_id"), JsonConverter(typeof(FlexibleStringConverter))] string? SportradarId,
    [property: JsonPropertyName("practice_participation")] string? PracticeParticipation,
    [property: JsonPropertyName("injury_start_date")] string? InjuryStartDate
)
{
    public string FullName => $"{FirstName} {LastName}";
}
