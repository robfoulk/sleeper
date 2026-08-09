using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Sleeper.Api.Models;
using Sleeper.Api.NflData;
using Sleeper.Api.NflData.Models;
using Sleeper.Api.NflData.Scoring;
using Sleeper.Api.Services;

namespace Sleeper.Api.Tests;

public class FantasyServiceTests
{
    private static readonly Dictionary<string, decimal> TestScoring = new()
    {
        ["pass_yd"] = 0.04m,
        ["pass_td"] = 4m,
        ["pass_int"] = -2m,
        ["rush_yd"] = 0.1m,
        ["rush_td"] = 6m,
        ["rec"] = 0m,
        ["rec_yd"] = 0.1m,
        ["rec_td"] = 6m,
        ["fum_lost"] = -2m,
        ["xpm"] = 1m,
    };

    private static (FantasyService service, ISleeperClient sleeper, ISleeperService sleeperService, INflDataClient nflData) Create()
    {
        var sleeper = Substitute.For<ISleeperClient>();
        var sleeperService = Substitute.For<ISleeperService>();
        var nflData = Substitute.For<INflDataClient>();

        // Default league with scoring settings
        sleeper.GetLeagueAsync("lg1", Arg.Any<CancellationToken>())
            .Returns(new League("lg1", "Test League", "in_season", "nfl", "2025", null, 8, null, null, null, null, TestScoring, null));

        return (new FantasyService(sleeper, sleeperService, nflData), sleeper, sleeperService, nflData);
    }

    [Fact]
    public async Task GetPlayerWeekScoreAsync_ReturnsScore()
    {
        var (service, sleeper, _, nflData) = Create();

        nflData.GetWeeklyStatsBySleeperIdAsync(2025, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, List<WeeklyPlayerStats>>
            {
                ["4046"] = [
                    new WeeklyPlayerStats { PlayerId = "gsis1", PlayerDisplayName = "Patrick Mahomes", Position = "QB", Week = 5, PassingYards = 300, PassingTds = 3, RushingYards = 40 }
                ]
            });

        var result = await service.GetPlayerWeekScoreAsync("lg1", "4046", 2025, 5);

        result.Should().NotBeNull();
        result!.PlayerName.Should().Be("Patrick Mahomes");
        // 300*0.04=12 + 3*4=12 + 40*0.1=4 = 28
        result.TotalPoints.Should().Be(28m);
    }

    [Fact]
    public async Task GetPlayerWeekScoreAsync_ReturnsNull_WhenNoStats()
    {
        var (service, _, _, nflData) = Create();

        nflData.GetWeeklyStatsBySleeperIdAsync(2025, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, List<WeeklyPlayerStats>>());

        var result = await service.GetPlayerWeekScoreAsync("lg1", "9999", 2025, 5);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPlayerSeasonScoreAsync_ReturnsScore()
    {
        var (service, _, _, nflData) = Create();

        nflData.GetSeasonStatsBySleeperIdAsync(2025, "reg", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, SeasonPlayerStats>
            {
                ["4046"] = new SeasonPlayerStats { PlayerId = "gsis1", PlayerDisplayName = "Josh Allen", Position = "QB", PassingYards = 4000, PassingTds = 30, RushingYards = 500, RushingTds = 5 }
            });

        var result = await service.GetPlayerSeasonScoreAsync("lg1", "4046", 2025);

        result.Should().NotBeNull();
        // 4000*0.04=160 + 30*4=120 + 500*0.1=50 + 5*6=30 = 360
        result!.TotalPoints.Should().Be(360m);
    }

    [Fact]
    public async Task GetRosterWeekScoresAsync_ScoresAllPlayers()
    {
        var (service, sleeper, sleeperService, nflData) = Create();

        var roster = new Roster(1, "u1", "lg1", ["qb1", "rb1", "def1"], ["qb1", "rb1", "def1"], null, null, null);
        sleeperService.GetMyRosterAsync("lg1", "rob", Arg.Any<CancellationToken>()).Returns(roster);

        nflData.GetWeeklyStatsBySleeperIdAsync(2025, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, List<WeeklyPlayerStats>>
            {
                ["qb1"] = [new WeeklyPlayerStats { PlayerId = "g1", PlayerDisplayName = "QB Guy", Position = "QB", Week = 1, PassingYards = 250, PassingTds = 2 }],
                ["rb1"] = [new WeeklyPlayerStats { PlayerId = "g2", PlayerDisplayName = "RB Guy", Position = "RB", Week = 1, RushingYards = 100, RushingTds = 1 }],
            });

        sleeper.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Player>
            {
                ["def1"] = new Player("def1", "SEA", "DEF", "DEF", "SEA", null, "Active", null, null, null, ["DEF"], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
            });

        var results = await service.GetRosterWeekScoresAsync("lg1", "rob", 2025, 1);

        results.Should().HaveCount(3);

        // QB: 250*0.04=10 + 2*4=8 = 18
        var qb = results.First(r => r.PlayerId == "g1");
        qb.TotalPoints.Should().Be(18m);

        // RB: 100*0.1=10 + 1*6=6 = 16
        var rb = results.First(r => r.PlayerId == "g2");
        rb.TotalPoints.Should().Be(16m);

        // DEF: no stats, 0 points
        var def = results.First(r => r.PlayerId == "def1");
        def.TotalPoints.Should().Be(0m);

        // Sorted by points descending
        results[0].TotalPoints.Should().BeGreaterThanOrEqualTo(results[1].TotalPoints);
    }

    [Fact]
    public async Task GetRosterSeasonScoresAsync_ScoresAllPlayers()
    {
        var (service, sleeper, sleeperService, nflData) = Create();

        var roster = new Roster(1, "u1", "lg1", ["qb1", "wr1"], ["qb1", "wr1"], null, null, null);
        sleeperService.GetMyRosterAsync("lg1", "rob", Arg.Any<CancellationToken>()).Returns(roster);

        nflData.GetSeasonStatsBySleeperIdAsync(2025, "reg", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, SeasonPlayerStats>
            {
                ["qb1"] = new SeasonPlayerStats { PlayerId = "g1", PlayerDisplayName = "QB Season", Position = "QB", PassingYards = 4000, PassingTds = 25 },
                ["wr1"] = new SeasonPlayerStats { PlayerId = "g2", PlayerDisplayName = "WR Season", Position = "WR", Receptions = 80, ReceivingYards = 1200, ReceivingTds = 10 },
            });

        sleeper.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Player>());

        var results = await service.GetRosterSeasonScoresAsync("lg1", "rob", 2025);

        results.Should().HaveCount(2);

        // QB: 4000*0.04=160 + 25*4=100 = 260
        var qb = results.First(r => r.PlayerName == "QB Season");
        qb.TotalPoints.Should().Be(260m);

        // WR (non-PPR): 1200*0.1=120 + 10*6=60 = 180
        var wr = results.First(r => r.PlayerName == "WR Season");
        wr.TotalPoints.Should().Be(180m);
    }

    [Fact]
    public async Task GetRosterWeekScoresAsync_ReturnsEmpty_WhenNoRoster()
    {
        var (service, _, sleeperService, _) = Create();

        sleeperService.GetMyRosterAsync("lg1", "nobody", Arg.Any<CancellationToken>()).Returns((Roster?)null);

        var results = await service.GetRosterWeekScoresAsync("lg1", "nobody", 2025, 1);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPlayerWeekScoreAsync_ReturnsNull_WhenLeagueNotFound()
    {
        var (service, sleeper, _, nflData) = Create();

        sleeper.GetLeagueAsync("bad", Arg.Any<CancellationToken>()).Returns((League?)null);
        nflData.GetWeeklyStatsBySleeperIdAsync(2025, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, List<WeeklyPlayerStats>>());

        var result = await service.GetPlayerWeekScoreAsync("bad", "4046", 2025, 1);

        result.Should().BeNull();
    }
}
