using CsvHelper.Configuration.Attributes;

namespace Sleeper.Api.NflData.Models;

/// <summary>
/// One NFL game from the nflverse schedule feed.
/// </summary>
public sealed class NflGame
{
    [Name("season")]
    public int Season { get; set; }

    [Name("game_type")]
    public string? GameType { get; set; }

    [Name("week")]
    public int Week { get; set; }

    [Name("away_team")]
    public string? AwayTeam { get; set; }

    [Name("home_team")]
    public string? HomeTeam { get; set; }
}
