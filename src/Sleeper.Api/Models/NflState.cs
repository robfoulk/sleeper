using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record NflState(
    [property: JsonPropertyName("week")] int Week,
    [property: JsonPropertyName("season")] string? Season,
    [property: JsonPropertyName("season_type")] string? SeasonType,
    [property: JsonPropertyName("season_start_date")] string? SeasonStartDate,
    [property: JsonPropertyName("previous_season")] string? PreviousSeason,
    [property: JsonPropertyName("leg")] int Leg,
    [property: JsonPropertyName("league_season")] string? LeagueSeason,
    [property: JsonPropertyName("league_create_season")] string? LeagueCreateSeason,
    [property: JsonPropertyName("display_week")] int DisplayWeek
);
