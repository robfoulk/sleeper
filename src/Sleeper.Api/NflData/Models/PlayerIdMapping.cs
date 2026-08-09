using CsvHelper.Configuration.Attributes;

namespace Sleeper.Api.NflData.Models;

/// <summary>
/// Player ID cross-reference mapping between platforms.
/// Source: https://github.com/dynastyprocess/data (db_playerids.csv)
/// </summary>
public class PlayerIdMapping
{
    [Name("gsis_id")]
    public string? GsisId { get; set; }

    [Name("sleeper_id")]
    public string? SleeperId { get; set; }

    [Name("espn_id")]
    public string? EspnId { get; set; }

    [Name("yahoo_id")]
    public string? YahooId { get; set; }

    [Name("pfr_id")]
    public string? PfrId { get; set; }

    [Name("fantasypros_id")]
    public string? FantasyProsId { get; set; }

    [Name("sportradar_id")]
    public string? SportradarId { get; set; }

    [Name("rotowire_id")]
    public string? RotowireId { get; set; }

    [Name("fantasy_data_id")]
    public string? FantasyDataId { get; set; }

    [Name("name")]
    public string? Name { get; set; }

    [Name("merge_name")]
    public string? MergeName { get; set; }

    [Name("position")]
    public string? Position { get; set; }

    [Name("team")]
    public string? Team { get; set; }
}
