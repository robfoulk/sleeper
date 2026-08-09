using CsvHelper.Configuration.Attributes;

namespace Sleeper.Api.NflData.Models;

/// <summary>
/// Season-level player stats from nflverse. One row per player per season.
/// Source: https://github.com/nflverse/nflverse-data/releases/tag/stats_player
/// </summary>
public class SeasonPlayerStats
{
    [Name("player_id")]
    public string PlayerId { get; set; } = "";

    [Name("player_name")]
    public string? PlayerName { get; set; }

    [Name("player_display_name")]
    public string? PlayerDisplayName { get; set; }

    [Name("position")]
    public string? Position { get; set; }

    [Name("position_group")]
    public string? PositionGroup { get; set; }

    [Name("season")]
    public int Season { get; set; }

    [Name("season_type")]
    public string? SeasonType { get; set; }

    [Name("recent_team")]
    public string? RecentTeam { get; set; }

    [Name("games")]
    public int? Games { get; set; }

    // Passing
    [Name("completions")]
    public int? Completions { get; set; }

    [Name("attempts")]
    public int? Attempts { get; set; }

    [Name("passing_yards")]
    public decimal? PassingYards { get; set; }

    [Name("passing_tds")]
    public int? PassingTds { get; set; }

    [Name("passing_interceptions")]
    public int? PassingInterceptions { get; set; }

    [Name("sacks_suffered")]
    public int? SacksSuffered { get; set; }

    [Name("sack_fumbles")]
    public int? SackFumbles { get; set; }

    [Name("sack_fumbles_lost")]
    public int? SackFumblesLost { get; set; }

    [Name("passing_first_downs")]
    public int? PassingFirstDowns { get; set; }

    [Name("passing_2pt_conversions")]
    public int? Passing2PtConversions { get; set; }

    // Rushing
    [Name("carries")]
    public int? Carries { get; set; }

    [Name("rushing_yards")]
    public decimal? RushingYards { get; set; }

    [Name("rushing_tds")]
    public int? RushingTds { get; set; }

    [Name("rushing_fumbles")]
    public int? RushingFumbles { get; set; }

    [Name("rushing_fumbles_lost")]
    public int? RushingFumblesLost { get; set; }

    [Name("rushing_first_downs")]
    public int? RushingFirstDowns { get; set; }

    [Name("rushing_2pt_conversions")]
    public int? Rushing2PtConversions { get; set; }

    // Receiving
    [Name("receptions")]
    public int? Receptions { get; set; }

    [Name("targets")]
    public int? Targets { get; set; }

    [Name("receiving_yards")]
    public decimal? ReceivingYards { get; set; }

    [Name("receiving_tds")]
    public int? ReceivingTds { get; set; }

    [Name("receiving_fumbles")]
    public int? ReceivingFumbles { get; set; }

    [Name("receiving_fumbles_lost")]
    public int? ReceivingFumblesLost { get; set; }

    [Name("receiving_first_downs")]
    public int? ReceivingFirstDowns { get; set; }

    [Name("receiving_2pt_conversions")]
    public int? Receiving2PtConversions { get; set; }

    [Name("target_share")]
    public decimal? TargetShare { get; set; }

    // Special Teams
    [Name("special_teams_tds")]
    public int? SpecialTeamsTds { get; set; }

    // Kicking
    [Name("fg_made")]
    public int? FgMade { get; set; }

    [Name("fg_att")]
    public int? FgAtt { get; set; }

    [Name("fg_missed")]
    public int? FgMissed { get; set; }

    [Name("fg_long")]
    public int? FgLong { get; set; }

    [Name("fg_made_0_19")]
    public int? FgMade0_19 { get; set; }

    [Name("fg_made_20_29")]
    public int? FgMade20_29 { get; set; }

    [Name("fg_made_30_39")]
    public int? FgMade30_39 { get; set; }

    [Name("fg_made_40_49")]
    public int? FgMade40_49 { get; set; }

    [Name("fg_made_50_59")]
    public int? FgMade50_59 { get; set; }

    [Name("fg_made_60_")]
    public int? FgMade60Plus { get; set; }

    [Name("pat_made")]
    public int? PatMade { get; set; }

    [Name("pat_att")]
    public int? PatAtt { get; set; }

    [Name("pat_missed")]
    public int? PatMissed { get; set; }

    // Fantasy
    [Name("fantasy_points")]
    public decimal? FantasyPoints { get; set; }

    [Name("fantasy_points_ppr")]
    public decimal? FantasyPointsPpr { get; set; }
}
