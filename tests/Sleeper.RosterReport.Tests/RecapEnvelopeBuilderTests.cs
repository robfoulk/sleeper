using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using NSubstitute;
using Sleeper.Api;
using Sleeper.Api.Injuries;
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
    public async Task BuildAsync_IncludesSharedInjuryReportOnlyWhenWindowRequested()
    {
        var start = new DateTimeOffset(2025, 9, 3, 0, 0, 0, TimeSpan.Zero);
        var window = new InjuryReportWindow(start, start.AddDays(7));
        var weeks = new Dictionary<int, List<Matchup>>
        {
            [1] = [new(1, 1, 10, null, ["p1"], ["p1"], [10], null), new(2, 1, 9, null, ["p2"], ["p2"], [9], null)]
        };
        var store = Substitute.For<IInjuryStore>();
        store.GetCohortAsync(Arg.Any<CancellationToken>()).Returns(new List<InjuryCohortPlayer>());
        store.GetChangesAsync(start, window.End, Arg.Any<CancellationToken>()).Returns(new List<InjuryChange>());
        var envelope = await BuildEnvelopeAsync(1, 2, weeks, injuryStore: store, injuryWindow: window);
        envelope.InjuryReport.Should().NotBeNull();
        envelope.InjuryReport!.Window.Should().Be(window);
        envelope.AgentFetchHints.Should().Contain(hint => hint.Contains("2025 week 1"));
        envelope.AgentFetchHints.Should().NotContain(hint => hint.Contains("past 48 hours"));
        (await BuildEnvelopeAsync(1, 2, weeks)).InjuryReport.Should().BeNull();
    }

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

    [Fact]
    public void ProjectLineupDetailed_UsesScheduleByesAndLegalFlexReplacement()
    {
        var matchup = new Matchup(
            1,
            1,
            null,
            null,
            ["bye-rb", "starter-wr"],
            ["bye-rb", "starter-wr", "bench-rb", "bench-te"],
            null,
            null);
        var players = new Dictionary<string, Player>
        {
            // Current metadata says MIA, but historical stats correctly place him on BUF.
            ["bye-rb"] = CreatePlayer("bye-rb", "Bye", "Runner", "RB", "MIA"),
            ["starter-wr"] = CreatePlayer("starter-wr", "Starting", "Receiver", "WR", "MIA"),
            ["bench-rb"] = CreatePlayer("bench-rb", "Bench", "Runner", "RB", "NYJ"),
            ["bench-te"] = CreatePlayer("bench-te", "Bench", "Tight End", "TE", "NE")
        };
        var history = new Dictionary<string, List<(int Week, decimal Points)>>
        {
            ["bye-rb"] = [(1, 20m), (2, 20m)],
            ["starter-wr"] = [(1, 10m), (2, 10m)],
            ["bench-rb"] = [(1, 5m), (2, 5m)],
            ["bench-te"] = [(1, 15m), (2, 15m)]
        };
        var schedule = new List<NflGame>
        {
            new() { Season = 2025, GameType = "REG", Week = 3, HomeTeam = "MIA", AwayTeam = "NYJ" },
            new() { Season = 2025, GameType = "REG", Week = 3, HomeTeam = "NE", AwayTeam = "KC" }
        };
        var config = new LeagueRosterConfig(
            Teams: 2,
            StarterSlots: new Dictionary<string, int> { ["WR"] = 1 },
            FlexSlots: 1,
            BenchSlots: 2,
            TotalRosterSize: 4,
            MaxKeepers: 0);

        var (projection, notes) = RecapEnvelopeBuilder.ProjectLineupDetailed(
            matchup,
            players,
            history,
            new Dictionary<string, List<WeeklyPlayerStats>>
            {
                ["bye-rb"] = [new() { Week = 2, Team = "BUF" }],
                ["starter-wr"] = [new() { Week = 2, Team = "MIA" }],
                ["bench-rb"] = [new() { Week = 2, Team = "NYJ" }],
                ["bench-te"] = [new() { Week = 2, Team = "NE" }]
            },
            schedule,
            config,
            forWeek: 3,
            applyLiveAvailability: false);

        projection.Should().Be(25m);
        notes.Should().Contain(note => note.Contains("Bye Runner") && note.Contains("BYE"));
        notes.Should().Contain(note => note.Contains("bench sub Bench Tight End"));
    }

    [Fact]
    public void ProjectLineupDetailed_IgnoresLiveInjuryStatusForHistoricalReplay()
    {
        var matchup = new Matchup(1, 1, null, null, ["injured"], ["injured"], null, null);
        var players = new Dictionary<string, Player>
        {
            ["injured"] = CreatePlayer("injured", "Historical", "Starter", "QB", "BUF", "Out")
        };
        var history = new Dictionary<string, List<(int Week, decimal Points)>>
        {
            ["injured"] = [(1, 20m), (2, 20m)]
        };
        var schedule = new List<NflGame>
        {
            new() { Season = 2025, GameType = "REG", Week = 3, HomeTeam = "BUF", AwayTeam = "MIA" }
        };
        var config = new LeagueRosterConfig(
            Teams: 2,
            StarterSlots: new Dictionary<string, int> { ["QB"] = 1 },
            FlexSlots: 0,
            BenchSlots: 0,
            TotalRosterSize: 1,
            MaxKeepers: 0);

        var historical = RecapEnvelopeBuilder.ProjectLineupDetailed(
            matchup,
            players,
            history,
            new Dictionary<string, List<WeeklyPlayerStats>>
            {
                ["injured"] = [new() { Week = 2, Team = "BUF" }]
            },
            schedule,
            config,
            forWeek: 3,
            applyLiveAvailability: false);
        var live = RecapEnvelopeBuilder.ProjectLineupDetailed(
            matchup,
            players,
            history,
            new Dictionary<string, List<WeeklyPlayerStats>>(),
            schedule,
            config,
            forWeek: 3,
            applyLiveAvailability: true);

        historical.Projection.Should().Be(20m);
        live.Projection.Should().Be(0m);
        live.Notes.Should().Contain(note => note.Contains("INJURED (Out)"));
    }

    [Theory]
    [InlineData("LA", "LAR")]
    [InlineData("LAR", "LA")]
    public void ProjectLineupDetailed_RecognizesRamsAcrossTeamAbbreviations(string scheduleTeam, string playerTeam)
    {
        var matchup = new Matchup(1, 1, null, null, ["receiver"], ["receiver"], null, null);
        var players = new Dictionary<string, Player>
        {
            ["receiver"] = CreatePlayer("receiver", "Starting", "Receiver", "WR", playerTeam)
        };
        var history = new Dictionary<string, List<(int Week, decimal Points)>>
        {
            ["receiver"] = [(1, 18m)]
        };
        var schedule = new List<NflGame>
        {
            new() { Season = 2026, GameType = "REG", Week = 2, HomeTeam = scheduleTeam, AwayTeam = "NYG" }
        };
        var config = new LeagueRosterConfig(
            Teams: 2,
            StarterSlots: new Dictionary<string, int> { ["WR"] = 1 },
            FlexSlots: 0,
            BenchSlots: 0,
            TotalRosterSize: 1,
            MaxKeepers: 0);

        var result = RecapEnvelopeBuilder.ProjectLineupDetailed(
            matchup, players, history, new(), schedule, config, forWeek: 2, applyLiveAvailability: true);

        result.Projection.Should().Be(18m);
        result.Notes.Should().NotContain(note => note.Contains("BYE"));
    }

    [Fact]
    public void ComputeOptimalLineupGain_DoesNotReuseOneStarterSlot()
    {
        var starters = new[] { CreatePlayerLine("starter", "Starter", "WR", 5m) };
        var bench = new[]
        {
            CreatePlayerLine("bench-1", "Bench One", "WR", 20m),
            CreatePlayerLine("bench-2", "Bench Two", "WR", 15m)
        };
        var config = new LeagueRosterConfig(
            Teams: 2,
            StarterSlots: new Dictionary<string, int> { ["WR"] = 1 },
            FlexSlots: 0,
            BenchSlots: 2,
            TotalRosterSize: 3,
            MaxKeepers: 0);

        var gain = RecapEnvelopeBuilder.ComputeOptimalLineupGain(starters, bench, config);

        gain.Should().Be(15m);
    }

    [Fact]
    public void LeagueRosterConfig_PreservesFlexVariantEligibility()
    {
        var league = CreateLeague(2) with
        {
            RosterPositions = ["QB", "SUPER_FLEX", "WRRB_FLEX", "REC_FLEX", "BN"]
        };

        var config = LeagueRosterConfig.FromLeague(league);

        config.FlexSlotEligibilities.Should().HaveCount(3);
        config.FlexSlotEligibilities![0].Should().Contain("QB");
        config.FlexSlotEligibilities[1].Should().BeEquivalentTo(["WR", "RB"]);
        config.FlexSlotEligibilities[2].Should().BeEquivalentTo(["WR", "TE"]);
    }

    [Fact]
    public async Task BuildAsync_Throws_WhenRequestedSeasonDoesNotMatchLeagueSeason()
    {
        // --season only labels the output; it does not change which league is fetched.
        // A mismatch silently mislabels another season's data and overwrites that
        // season's recap on disk, so it must fail loudly instead.
        var client = Substitute.For<ISleeperClient>();
        var sleeperService = Substitute.For<ISleeperService>();
        var nfl = Substitute.For<INflDataClient>();

        client.GetLeagueAsync(LeagueId, Arg.Any<CancellationToken>()).Returns(CreateLeague(8));
        client.GetLeagueRostersAsync(LeagueId, Arg.Any<CancellationToken>()).Returns(CreateRosters(8));
        client.GetLeagueUsersAsync(LeagueId, Arg.Any<CancellationToken>()).Returns(CreateUsers(8));
        client.GetTransactionsAsync(LeagueId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Transaction>());
        client.GetWinnersBracketAsync(LeagueId, Arg.Any<CancellationToken>()).Returns([]);
        client.GetLosersBracketAsync(LeagueId, Arg.Any<CancellationToken>()).Returns([]);
        client.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>()).Returns(new Dictionary<string, Player>());
        client.GetLeagueMatchupsAsync(LeagueId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        nfl.GetWeeklyStatsBySleeperIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, List<WeeklyPlayerStats>>());

        var builder = new RecapEnvelopeBuilder(client, sleeperService, nfl, LeagueLore.ParseFrom(""));

        // The stub league is season 2025; ask for 2026.
        var act = async () => await builder.BuildAsync(
            LeagueId, 1, 2026, options: new RecapEnvelopeBuildOptions(PersistSnapshots: false));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("2026") && e.Message.Contains("2025"));
    }

    private static async Task<RecapEnvelope> BuildEnvelopeAsync(
        int week,
        int teamCount,
        Dictionary<int, List<Matchup>> weeks,
        List<PlayoffBracketMatch>? winners = null,
        List<PlayoffBracketMatch>? losers = null,
        IInjuryStore? injuryStore = null,
        InjuryReportWindow? injuryWindow = null)
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

        var builder = new RecapEnvelopeBuilder(client, sleeperService, nfl, LeagueLore.ParseFrom(""),
            injuryStore is null ? null : new WeeklyInjuryReportService(injuryStore, client));
        return await builder.BuildAsync(LeagueId, week, 2025,
            options: new RecapEnvelopeBuildOptions(PersistSnapshots: false, InjuryWindow: injuryWindow));
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

    private static Player CreatePlayer(
        string id,
        string firstName,
        string lastName,
        string position,
        string team,
        string? injuryStatus = null)
        => new(
            PlayerId: id,
            FirstName: firstName,
            LastName: lastName,
            Position: position,
            Team: team,
            Age: null,
            Status: "Active",
            Number: null,
            College: null,
            YearsExp: null,
            FantasyPositions: [position],
            InjuryStatus: injuryStatus,
            Weight: null,
            Height: null,
            SearchFullName: null,
            SearchFirstName: null,
            SearchLastName: null,
            SearchRank: null,
            DepthChartPosition: null,
            DepthChartOrder: null,
            Sport: "nfl",
            Hashtag: null,
            FantasyDataId: null,
            BirthCountry: null,
            EspnId: null,
            YahooId: null,
            RotowireId: null,
            RotoworldId: null,
            SportradarId: null,
            PracticeParticipation: null,
            InjuryStartDate: null);

    private static PlayerLine CreatePlayerLine(string id, string name, string position, decimal points)
        => new(
            id, name, position, null, null, null, points, null, null, false, false,
            null, null, null, null, null, null, null, null, null, null, null);

    [Fact]
    public void TeamNameEntry_MigratesLegacyDisplayName_AndDropsUsername()
    {
        // Older team-name-history.json snapshots stored {DisplayName, TeamName, Username}.
        // Rewriting one must keep the owner name and must never round-trip the platform handle.
        var entryType = typeof(RecapEnvelopeBuilder)
            .GetNestedType("TeamNameEntry", BindingFlags.NonPublic)!;

        const string legacy = """
            { "DisplayName": "Rob", "TeamName": "Unstoppable Farce", "Username": "robfoulk" }
            """;

        var entry = JsonSerializer.Deserialize(legacy, entryType)!;

        entryType.GetProperty("OwnerName")!.GetValue(entry).Should().Be("Rob");
        entryType.GetProperty("TeamName")!.GetValue(entry).Should().Be("Unstoppable Farce");

        var rewritten = JsonSerializer.Serialize(entry, entryType, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        rewritten.Should().Contain("\"OwnerName\":\"Rob\"");
        rewritten.Should().NotContain("robfoulk");
        rewritten.Should().NotContain("DisplayName");
        rewritten.Should().NotContain("Username");
    }

    private static bool HasRosters(GameRecap game, int rosterA, int rosterB)
        => new[] { game.Home.RosterId, game.Away.RosterId }.Order().SequenceEqual(new[] { rosterA, rosterB });
}
