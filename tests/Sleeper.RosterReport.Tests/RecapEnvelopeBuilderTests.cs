using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Sleeper.Api;
using Sleeper.Api.Models;
using Sleeper.Api.NflData;
using Sleeper.Api.NflData.Models;
using Sleeper.Api.Services;
using Sleeper.RosterReport.Recap;

namespace Sleeper.RosterReport.Tests;

public class RecapEnvelopeBuilderTests
{
    private const string LeagueId = "league-1";

    [Fact]
    public async Task BuildAsync_SkipsUnplayedNullScoreMatchups_WhenReconstructingStandings()
    {
        var envelope = await BuildEnvelopeAsync(
            week: 1,
            teamCount: 2,
            weeks: new Dictionary<int, List<Matchup>>
            {
                [1] =
                [
                    new Matchup(1, 1, null, null, null, null, null, null),
                    new Matchup(2, 1, null, null, null, null, null, null)
                ]
            });

        envelope.Standings.Should().OnlyContain(row => row.Wins == 0 && row.Losses == 0 && row.Ties == 0);
        envelope.Standings.Should().OnlyContain(row => row.PointsFor == 0m && row.PointsAgainst == 0m);
        envelope.SeasonLedger!.WinStreaks.Should().BeEmpty();
        envelope.SeasonLedger.LossStreaks.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_UsesCurrentWeekMatchups_ForCurrentWeekStakes()
    {
        var weeks = new Dictionary<int, List<Matchup>>
        {
            [15] =
            [
                ScoredMatchup(1, 1, 160m),
                ScoredMatchup(4, 1, 140m),
                ScoredMatchup(2, 2, 130m),
                ScoredMatchup(5, 2, 120m),
                ScoredMatchup(3, 3, 110m),
                ScoredMatchup(6, 3, 100m)
            ],
            [16] =
            [
                ScoredMatchup(2, 10, 0m),
                ScoredMatchup(6, 10, 0m)
            ]
        };

        var envelope = await BuildEnvelopeAsync(15, 6, weeks);

        envelope.PlayoffPicture.Should().NotBeNull();
        envelope.PlayoffPicture!.WeekStakes.Should().Contain(note => note.Headline.Contains("Team 1") && note.Headline.Contains("Team 4"));
        envelope.PlayoffPicture.WeekStakes.Should().NotContain(note => note.Headline.Contains("Team 2") && note.Headline.Contains("Team 6"));
    }

    [Fact]
    public async Task BuildAsync_AssignsPerGameContext_ForChampionshipAndConsolationGames()
    {
        var weeks = new Dictionary<int, List<Matchup>>
        {
            [17] =
            [
                ScoredMatchup(1, 1, 150m),
                ScoredMatchup(2, 1, 120m),
                ScoredMatchup(3, 2, 130m),
                ScoredMatchup(4, 2, 110m),
                ScoredMatchup(5, 3, 100m),
                ScoredMatchup(6, 3, 90m),
                ScoredMatchup(7, 4, 80m),
                ScoredMatchup(8, 4, 70m)
            ]
        };
        var winners = new List<PlayoffBracketMatch>
        {
            new(2, 1, 1, 2, 1, 2, null, null, 1),
            new(2, 2, 3, 4, 3, 4, null, null, 3)
        };
        var losers = new List<PlayoffBracketMatch>
        {
            new(2, 1, 5, 6, 5, 6, null, null, 1),
            new(2, 2, 7, 8, 7, 8, null, null, 3)
        };

        var envelope = await BuildEnvelopeAsync(17, 8, weeks, winners, losers);

        var championship = envelope.Games.Single(game => HasRosters(game, 1, 2));
        championship.SeasonContext.Should().Be("playoffs_winners");
        championship.PlayoffRound.Should().Be("Championship");
        championship.StoryImportance.Should().Be(5);

        var consolation = envelope.Games.Single(game => HasRosters(game, 5, 6));
        consolation.SeasonContext.Should().Be("consolation");
        consolation.PlayoffRound.Should().Be("Consolation final");
        consolation.StoryImportance.Should().Be(4);
        consolation.StoryImportanceReason.Should().Contain("1.01");
    }

    private static async Task<RecapEnvelope> BuildEnvelopeAsync(
        int week,
        int teamCount,
        Dictionary<int, List<Matchup>> weeks,
        List<PlayoffBracketMatch>? winners = null,
        List<PlayoffBracketMatch>? losers = null)
    {
        var client = Substitute.For<ISleeperClient>();
        var sleeperService = Substitute.For<ISleeperService>();
        var nfl = Substitute.For<INflDataClient>();

        client.GetLeagueAsync(LeagueId, Arg.Any<CancellationToken>()).Returns(CreateLeague(teamCount));
        client.GetLeagueRostersAsync(LeagueId, Arg.Any<CancellationToken>()).Returns(CreateRosters(teamCount));
        client.GetLeagueUsersAsync(LeagueId, Arg.Any<CancellationToken>()).Returns(CreateUsers(teamCount));
        client.GetTransactionsAsync(LeagueId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Transaction>());
        client.GetWinnersBracketAsync(LeagueId, Arg.Any<CancellationToken>()).Returns(winners ?? []);
        client.GetLosersBracketAsync(LeagueId, Arg.Any<CancellationToken>()).Returns(losers ?? []);
        client.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>()).Returns(new Dictionary<string, Player>());
        client.GetLeagueMatchupsAsync(LeagueId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(weeks.GetValueOrDefault(call.ArgAt<int>(1)) ?? []));

        nfl.GetWeeklyStatsBySleeperIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, List<WeeklyPlayerStats>>());

        var builder = new RecapEnvelopeBuilder(client, sleeperService, nfl, LeagueLore.ParseFrom(""));
        return await builder.BuildAsync(LeagueId, week, 2025, options: new RecapEnvelopeBuildOptions(PersistSnapshots: false));
    }

    private static League CreateLeague(int teamCount)
        => new(
            LeagueId,
            "Test League",
            "in_season",
            "nfl",
            "2025",
            "regular",
            teamCount,
            null,
            null,
            ["QB", "RB", "WR", "TE", "FLEX", "K", "DEF", "BN"],
            new Dictionary<string, JsonElement> { ["playoff_teams"] = JsonSerializer.SerializeToElement(4) },
            new Dictionary<string, decimal> { ["rec"] = 0.5m, ["pass_td"] = 6m },
            null);

    private static List<Roster> CreateRosters(int teamCount)
        => Enumerable.Range(1, teamCount)
            .Select(id => new Roster(id, $"user-{id}", LeagueId, [], [], null, null, new RosterSettings(0, 0, 0, 0, 0, 0, 0, 0, id, 0)))
            .ToList();

    private static List<LeagueUser> CreateUsers(int teamCount)
        => Enumerable.Range(1, teamCount)
            .Select(id => new LeagueUser(
                $"user-{id}",
                $"owner{id}",
                $"Owner {id}",
                null,
                true,
                new Dictionary<string, string> { ["team_name"] = $"Team {id}" }))
            .ToList();

    private static Matchup ScoredMatchup(int rosterId, int matchupId, decimal points)
        => new(rosterId, matchupId, points, null, null, null, null, points == 0m ? null : new Dictionary<string, decimal> { [$"p{rosterId}"] = points });

    private static bool HasRosters(GameRecap game, int rosterA, int rosterB)
        => new[] { game.Home.RosterId, game.Away.RosterId }.Order().SequenceEqual(new[] { rosterA, rosterB });
}
