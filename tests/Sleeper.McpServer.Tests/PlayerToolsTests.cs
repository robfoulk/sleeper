using FluentAssertions;
using NSubstitute;
using Sleeper.Api.Models;
using Sleeper.Api.NflData.Analytics;
using Sleeper.Api.NflData.Services;
using Sleeper.McpServer.Tools;

namespace Sleeper.McpServer.Tests;

public class PlayerToolsTests
{
    [Fact]
    public async Task SearchPlayers_ReturnsValidationError_WhenQueryIsBlank()
    {
        var analysis = Substitute.For<IAnalysisService>();

        var result = await PlayerTools.SearchPlayers(analysis, "   ");

        result.Should().Be("Error: Query is required.");
        await analysis.DidNotReceiveWithAnyArgs().SearchPlayersAsync(default!, default, default);
    }

    [Fact]
    public async Task SearchPlayers_PassesPositionToAnalysisService()
    {
        var analysis = Substitute.For<IAnalysisService>();
        var player = new Player("p1", "Patrick", "Mahomes", "QB", "KC", 30, "Active", 15, null, null, ["QB"], null, null, null, "patrickmahomes", "patrick", "mahomes", 1, null, null, "nfl", null, null, null, null, null, null, null, null, null, null);
        analysis.SearchPlayersAsync("mahomes", "QB", CancellationToken.None).Returns([player]);

        var result = await PlayerTools.SearchPlayers(analysis, "mahomes", "QB");

        result.Should().Contain("Patrick Mahomes");
        await analysis.Received(1).SearchPlayersAsync("mahomes", "QB", CancellationToken.None);
    }

    [Fact]
    public async Task SearchPlayers_DoesNotSwallowCancellation()
    {
        var analysis = Substitute.For<IAnalysisService>();
        analysis.SearchPlayersAsync("mahomes", null, CancellationToken.None)
            .Returns<Task<List<Player>>>(_ => throw new OperationCanceledException());

        Func<Task> act = async () => await PlayerTools.SearchPlayers(analysis, "mahomes");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PlayerDeepDive_PreservesActualWeekNumbers()
    {
        var analysis = Substitute.For<IAnalysisService>();
        var player = new Player("p1", "Test", "Player", "RB", "BUF", 25, "Active", 1, null, 2, ["RB"], null, null, null, "testplayer", "test", "player", 1, null, null, "nfl", null, null, null, null, null, null, null, null, null, null);
        var seasonHistory = new List<SeasonSummary> { new(2025, 2, 20m, 10m, 0m) };
        var playerAnalysis = new PlayerAnalysis(
            "p1", "Test Player", "RB", 25, null, false,
            10m, 10m, 170m, 7.5m, 2.5m, 1, 0m,
            100m, 0m, 0m, 100m, 0m, "Stable", 0m, "N/A", seasonHistory);
        analysis.GetPlayerDeepDiveAsync("league", "test", 3, Arg.Any<CancellationToken>())
            .Returns(new PlayerDeepDive(
                player,
                seasonHistory,
                new Dictionary<int, List<WeeklyFantasyScore>>
                {
                    [2025] = [new WeeklyFantasyScore(2, 8m), new WeeklyFantasyScore(4, 12m)]
                },
                playerAnalysis,
                new LeagueRankings([], [])));

        var result = await PlayerTools.PlayerDeepDive(analysis, "test", "league");

        result.Should().Contain("Week 2: 8.0 pts");
        result.Should().Contain("Week 4: 12.0 pts");
        result.Should().NotContain("Week 1:");
    }
}