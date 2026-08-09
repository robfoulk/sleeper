using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using Sleeper.Api.NflData.Models;

namespace Sleeper.Api.Tests;

public class NflDataModelTests
{
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = null,
        TrimOptions = CsvHelper.Configuration.TrimOptions.Trim,
    };

    [Fact]
    public void WeeklyPlayerStats_ParsesFromCsv()
    {
        // Minimal CSV with just the fields we care about — CsvHelper matches by header name
        var csv = "player_id,player_display_name,position,season,week,season_type,team,opponent_team,completions,attempts,passing_yards,passing_tds,passing_interceptions,carries,rushing_yards,receptions,targets,receiving_yards,receiving_tds,fantasy_points,fantasy_points_ppr\n" +
                  "00-0023459,Aaron Rodgers,QB,2025,1,REG,PIT,NYJ,22,30,244,4,0,1,-1,0,0,0,0,25.66,25.66";

        using var reader = new StringReader(csv);
        using var csvReader = new CsvReader(reader, CsvConfig);
        var records = csvReader.GetRecords<WeeklyPlayerStats>().ToList();

        records.Should().HaveCount(1);
        var stat = records[0];
        stat.PlayerId.Should().Be("00-0023459");
        stat.PlayerDisplayName.Should().Be("Aaron Rodgers");
        stat.Position.Should().Be("QB");
        stat.Season.Should().Be(2025);
        stat.Week.Should().Be(1);
        stat.Team.Should().Be("PIT");
        stat.OpponentTeam.Should().Be("NYJ");
        stat.Completions.Should().Be(22);
        stat.Attempts.Should().Be(30);
        stat.PassingYards.Should().Be(244);
        stat.PassingTds.Should().Be(4);
        stat.PassingInterceptions.Should().Be(0);
        stat.Carries.Should().Be(1);
        stat.RushingYards.Should().Be(-1);
        stat.FantasyPoints.Should().Be(25.66m);
        stat.FantasyPointsPpr.Should().Be(25.66m);
    }

    [Fact]
    public void SeasonPlayerStats_ParsesFromCsv()
    {
        // Minimal CSV with just the fields we care about — CsvHelper matches by header name
        var csv = "player_id,player_display_name,position,season,season_type,recent_team,games,passing_yards,passing_tds,rushing_yards,fantasy_points,fantasy_points_ppr\n" +
                  "00-0023459,Aaron Rodgers,QB,2025,REG,PIT,16,3322,24,61,226.08,227.08";

        using var reader = new StringReader(csv);
        using var csvReader = new CsvReader(reader, CsvConfig);
        var records = csvReader.GetRecords<SeasonPlayerStats>().ToList();

        records.Should().HaveCount(1);
        var stat = records[0];
        stat.PlayerId.Should().Be("00-0023459");
        stat.PlayerDisplayName.Should().Be("Aaron Rodgers");
        stat.Season.Should().Be(2025);
        stat.RecentTeam.Should().Be("PIT");
        stat.Games.Should().Be(16);
        stat.PassingYards.Should().Be(3322);
        stat.PassingTds.Should().Be(24);
        stat.RushingYards.Should().Be(61);
        stat.FantasyPoints.Should().Be(226.08m);
        stat.FantasyPointsPpr.Should().Be(227.08m);
    }

    [Fact]
    public void PlayerIdMapping_ParsesFromCsv()
    {
        var csv = """
            mfl_id,sportradar_id,fantasypros_id,gsis_id,pff_id,sleeper_id,nfl_id,espn_id,yahoo_id,fleaflicker_id,cbs_id,pfr_id,cfbref_id,rotowire_id,rotoworld_id,ktc_id,stats_id,stats_global_id,fantasy_data_id,swish_id,name,merge_name,position,team,birthdate,age,draft_year,draft_round,draft_pick,draft_ovr,twitter_username,height,weight,college,db_season
            17030,3c76cab3-3df2-43dd-acaa-57e055bd32d0,24755,00-0040676,133244,12522,58203,4688380,NA,NA,3168422,WardCa00,NA,16997,NA,1730,41786,0,25323,NA,Cam Ward,cam ward,QB,TEN,2002-05-25,23.7,2025,1,1,1,NA,74,219,Miami (FL),2025
            """;

        using var reader = new StringReader(csv);
        using var csvReader = new CsvReader(reader, CsvConfig);
        var records = csvReader.GetRecords<PlayerIdMapping>().ToList();

        records.Should().HaveCount(1);
        var mapping = records[0];
        mapping.GsisId.Should().Be("00-0040676");
        mapping.SleeperId.Should().Be("12522");
        mapping.Name.Should().Be("Cam Ward");
        mapping.Position.Should().Be("QB");
        mapping.Team.Should().Be("TEN");
    }
}
