using FluentAssertions;
using NSubstitute;
using Sleeper.Api;
using Sleeper.Api.Models;
using Sleeper.Api.Services;
using Sleeper.McpServer.Tools;

namespace Sleeper.McpServer.Tests;

public class LeagueToolsTests
{
    [Theory]
    [InlineData("watch")]
    [InlineData("")]
    public async Task GetTrendingPlayers_ReturnsValidationError_WhenTypeIsInvalid(string type)
    {
        var client = Substitute.For<ISleeperClient>();

        var result = await LeagueTools.GetTrendingPlayers(client, type, 10);

        result.Should().Be("Error: Type must be 'add' or 'drop'.");
        await client.DidNotReceiveWithAnyArgs().GetTrendingPlayersAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task GetLeagueRankings_ReturnsValidationError_WhenTopIsNotPositive()
    {
        var analysis = Substitute.For<Sleeper.Api.NflData.Services.IAnalysisService>();

        var result = await LeagueTools.GetLeagueRankings(analysis, 2025, top: 0);

        result.Should().Be("Error: Top must be between 1 and 100.");
    }

    [Fact]
    public async Task GetDraftHistory_ReturnsFriendlyResult_WhenCompletedDraftHasNoPicks()
    {
        var client = Substitute.For<ISleeperClient>();
        client.GetLeagueDraftsAsync("league", Arg.Any<CancellationToken>())
            .Returns([new Draft("draft", "league", "complete", "snake", "2026", "nfl", null, null, null, null, null, null, null, null, null, null)]);
        client.GetDraftPicksAsync("draft", Arg.Any<CancellationToken>()).Returns([]);

        var result = await LeagueTools.GetDraftHistory(client, "league");

        result.Should().Be("Draft found for 2026, but no picks are available.");
    }

    [Fact]
    public async Task GetMatchupScoreboard_ReportsTie()
    {
        var client = Substitute.For<ISleeperClient>();
        var sleeperService = Substitute.For<ISleeperService>();
        client.GetLeagueAsync("league", Arg.Any<CancellationToken>())
            .Returns(new League("league", "Test", "in_season", "nfl", "2026", null, 2, null, null, null, null, null, null));
        sleeperService.GetWeekScoreboardAsync("league", 1, Arg.Any<CancellationToken>())
            .Returns([new MatchupWithNames(1, "One", "Team One", 1, 100m, "Two", "Team Two", 2, 100m)]);

        var result = await LeagueTools.GetMatchupScoreboard(client, sleeperService, 1, "league");

        result.Should().Contain("(Tie)");
        result.Should().NotContain("Winner:");
    }
}