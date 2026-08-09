using FluentAssertions;
using NSubstitute;
using Sleeper.Api.Models;
using Sleeper.Api.Services;

namespace Sleeper.Api.Tests;

public class SleeperServiceTests
{
    private static (SleeperService service, ISleeperClient client) Create()
    {
        var client = Substitute.For<ISleeperClient>();
        return (new SleeperService(client), client);
    }

    [Fact]
    public async Task GetMyRosterAsync_FindsRosterByUsername()
    {
        var (service, client) = Create();
        var user = new User("u1", "robfoulk", "Rob", null);
        client.GetUserAsync("robfoulk", Arg.Any<CancellationToken>()).Returns(user);

        var rosters = new List<Roster>
        {
            new(1, "other_user", "lg1", ["123"], ["123"], null, null, null),
            new(2, "u1", "lg1", ["456", "SEA"], ["456", "SEA"], null, null, null)
        };
        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>()).Returns(rosters);

        var roster = await service.GetMyRosterAsync("lg1", "robfoulk");

        roster.Should().NotBeNull();
        roster!.RosterId.Should().Be(2);
        roster.Players.Should().Contain("SEA");
    }

    [Fact]
    public async Task GetMyRosterAsync_ReturnsNull_WhenUserNotFound()
    {
        var (service, client) = Create();
        client.GetUserAsync("unknown", Arg.Any<CancellationToken>()).Returns((User?)null);

        var roster = await service.GetMyRosterAsync("lg1", "unknown");

        roster.Should().BeNull();
    }

    [Fact]
    public async Task FindPlayerDraftPickAsync_FindsByPartialName()
    {
        var (service, client) = Create();
        var drafts = new List<Draft>
        {
            new("d1", "lg1", "complete", "snake", "2025", "nfl", null, null, null, null, null, null, null, null, null, null)
        };
        client.GetLeagueDraftsAsync("lg1", Arg.Any<CancellationToken>()).Returns(drafts);

        var picks = new List<DraftPick>
        {
            new("111", "u1", 1, 1, 1, 1, null, "d1",
                new DraftPickMetadata("111", "Patrick", "Mahomes", "QB", "KC", "Active", "nfl", "15", null, null)),
            new("222", "u2", 2, 2, 2, 2, null, "d1",
                new DraftPickMetadata("222", "Josh", "Allen", "QB", "BUF", "Active", "nfl", "17", null, null))
        };
        client.GetDraftPicksAsync("d1", Arg.Any<CancellationToken>()).Returns(picks);

        var pick = await service.FindPlayerDraftPickAsync("lg1", "mahomes");

        pick.Should().NotBeNull();
        pick!.Metadata!.FirstName.Should().Be("Patrick");
        pick.Round.Should().Be(1);
    }

    [Fact]
    public async Task GetRostersWithOwnersAsync_EnrichesWithUserInfo()
    {
        var (service, client) = Create();
        var rosters = new List<Roster>
        {
            new(1, "u1", "lg1", ["123", "DET"], ["123", "DET"], null, null, null)
        };
        var users = new List<LeagueUser>
        {
            new("u1", "rob", "Rob F", null, true, new Dictionary<string, string> { ["team_name"] = "Champions" })
        };
        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>()).Returns(rosters);
        client.GetLeagueUsersAsync("lg1", Arg.Any<CancellationToken>()).Returns(users);

        var result = await service.GetRostersWithOwnersAsync("lg1");

        result.Should().HaveCount(1);
        result[0].OwnerDisplayName.Should().Be("Rob F");
        result[0].TeamName.Should().Be("Champions");
    }

    [Fact]
    public async Task GetRosterPlayersAsync_SynthesizesTeamDefenses()
    {
        var (service, client) = Create();
        var user = new User("u1", "rob", "Rob", null);
        client.GetUserAsync("rob", Arg.Any<CancellationToken>()).Returns(user);

        var rosters = new List<Roster>
        {
            new(1, "u1", "lg1", ["3086", "SEA"], ["3086", "SEA"], null, null, null)
        };
        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>()).Returns(rosters);

        var allPlayers = new Dictionary<string, Player>
        {
            ["3086"] = new Player("3086", "Tom", "Brady", "QB", "NE", 40, "Active", 12, "Michigan", 14, ["QB"],
                null, "220", "6'4\"", "tombrady", "tom", "brady", 24, null, null, "nfl", null, null, null, null, null, null, null, null, null, null)
            // Note: no "SEA" entry — it's a team defense and may be missing from /players
        };
        client.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>()).Returns(allPlayers);

        var result = await service.GetRosterPlayersAsync("lg1", "rob");

        result.Should().HaveCount(2);

        var brady = result.First(p => p.Player.PlayerId == "3086");
        brady.Player.FullName.Should().Be("Tom Brady");
        brady.IsStarter.Should().BeTrue();

        var sea = result.First(p => p.Player.PlayerId == "SEA");
        sea.Player.Position.Should().Be("DEF");
        sea.Player.Team.Should().Be("SEA");
        sea.Player.FullName.Should().Be("SEA DEF");
        sea.IsStarter.Should().BeTrue();
    }

    [Fact]
    public async Task GetLeagueIdForSeasonAsync_WalksPreviousLeagueChain()
    {
        var (service, client) = Create();
        client.GetLeagueAsync("lg2025", Arg.Any<CancellationToken>())
            .Returns(new League("lg2025", "L", "complete", "nfl", "2025", null, 8, null, "lg2024", null, null, null, null));
        client.GetLeagueAsync("lg2024", Arg.Any<CancellationToken>())
            .Returns(new League("lg2024", "L", "complete", "nfl", "2024", null, 8, null, "lg2023", null, null, null, null));
        client.GetLeagueAsync("lg2023", Arg.Any<CancellationToken>())
            .Returns(new League("lg2023", "L", "complete", "nfl", "2023", null, 8, null, null, null, null, null, null));

        var result = await service.GetLeagueIdForSeasonAsync("lg2025", "2024");
        result.Should().Be("lg2024");

        var result2 = await service.GetLeagueIdForSeasonAsync("lg2025", "2023");
        result2.Should().Be("lg2023");
    }

    [Fact]
    public async Task GetLeagueIdForSeasonAsync_ReturnsNull_WhenSeasonNotFound()
    {
        var (service, client) = Create();
        client.GetLeagueAsync("lg2025", Arg.Any<CancellationToken>())
            .Returns(new League("lg2025", "L", "complete", "nfl", "2025", null, 8, null, null, null, null, null, null));

        var result = await service.GetLeagueIdForSeasonAsync("lg2025", "2020");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLeagueIdForSeasonAsync_ReturnsNull_WhenPreviousLeagueChainCycles()
    {
        var (service, client) = Create();
        client.GetLeagueAsync("lg2025", Arg.Any<CancellationToken>())
            .Returns(new League("lg2025", "L", "complete", "nfl", "2025", null, 8, null, "lg2024", null, null, null, null));
        client.GetLeagueAsync("lg2024", Arg.Any<CancellationToken>())
            .Returns(new League("lg2024", "L", "complete", "nfl", "2024", null, 8, null, "lg2025", null, null, null, null));

        var result = await service.GetLeagueIdForSeasonAsync("lg2025", "2023");

        result.Should().BeNull();
        await client.Received(1).GetLeagueAsync("lg2025", Arg.Any<CancellationToken>());
        await client.Received(1).GetLeagueAsync("lg2024", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsTeamDefense_RecognizesAllNflTeams()
    {
        SleeperService.IsTeamDefense("SEA").Should().BeTrue();
        SleeperService.IsTeamDefense("DET").Should().BeTrue();
        SleeperService.IsTeamDefense("PHI").Should().BeTrue();
        SleeperService.IsTeamDefense("GB").Should().BeTrue();
        SleeperService.IsTeamDefense("KC").Should().BeTrue();
        SleeperService.IsTeamDefense("LAR").Should().BeTrue();
        SleeperService.IsTeamDefense("sea").Should().BeTrue(); // case insensitive
        SleeperService.IsTeamDefense("3086").Should().BeFalse();
        SleeperService.IsTeamDefense("INVALID").Should().BeFalse();
    }

    // -- KeeperValue.Calculate --

    [Fact]
    public void KeeperValue_Calculate_UndraftedPlayerCostsDefault10()
    {
        var (cost, canKeep) = KeeperValue.Calculate(null);
        cost.Should().Be(10);
        canKeep.Should().BeTrue();
    }

    [Fact]
    public void KeeperValue_Calculate_UndraftedPlayerUsesCustomCost()
    {
        var (cost, canKeep) = KeeperValue.Calculate(null, undraftedCost: 12);
        cost.Should().Be(12);
        canKeep.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void KeeperValue_Calculate_Rounds1Through3_CannotBeKept(int round)
    {
        var (cost, canKeep) = KeeperValue.Calculate(round);
        cost.Should().BeNull();
        canKeep.Should().BeFalse();
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(10, 7)]
    [InlineData(15, 12)]
    [InlineData(17, 14)]
    public void KeeperValue_Calculate_RoundMinusThree(int draftRound, int expectedCost)
    {
        var (cost, canKeep) = KeeperValue.Calculate(draftRound);
        cost.Should().Be(expectedCost);
        canKeep.Should().BeTrue();
    }

    // -- GetRosterKeeperValuesAsync --

    [Fact]
    public async Task GetRosterKeeperValuesAsync_CombinesDraftAndRosterData()
    {
        var (service, client) = Create();

        // League with previous league
        client.GetLeagueAsync("lg1", Arg.Any<CancellationToken>())
            .Returns(new League("lg1", "Test", "pre_draft", "nfl", "2025", null, 8, "d_current", "lg_prev", null, null, null, null));

        // No completed draft in current league
        client.GetLeagueDraftsAsync("lg1", Arg.Any<CancellationToken>())
            .Returns(new List<Draft>
            {
                new("d_current", "lg1", "pre_draft", "snake", "2025", "nfl", null, null, null, null, null, null, null, null, null, null)
            });

        // Previous league has a completed draft
        client.GetLeagueDraftsAsync("lg_prev", Arg.Any<CancellationToken>())
            .Returns(new List<Draft>
            {
                new("d_prev", "lg_prev", "complete", "snake", "2024", "nfl", null, null, null, null, null, null, null, null, null, null)
            });

        // Draft picks
        client.GetDraftPicksAsync("d_prev", Arg.Any<CancellationToken>())
            .Returns(new List<DraftPick>
            {
                new("p1", "u1", 1, 1, 1, 1, null, "d_prev",
                    new DraftPickMetadata("p1", "Star", "QB", "QB", "KC", "Active", "nfl", "15", null, null)),
                new("p2", "u1", 1, 5, 5, 5, null, "d_prev",
                    new DraftPickMetadata("p2", "Mid", "RB", "RB", "BUF", "Active", "nfl", "22", null, null)),
                new("p3", "u1", 1, 15, 3, 15, true, "d_prev",
                    new DraftPickMetadata("p3", "Late", "WR", "WR", "DAL", "Active", "nfl", "88", null, null)),
            });

        // User + roster
        var user = new User("u1", "rob", "Rob", null);
        client.GetUserAsync("rob", Arg.Any<CancellationToken>()).Returns(user);
        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>())
            .Returns(new List<Roster>
            {
                new(1, "u1", "lg1", ["p1", "p2", "p3", "p4"], ["p1", "p2"], null, null, null)
            });

        // Player data
        var allPlayers = new Dictionary<string, Player>
        {
            ["p1"] = new Player("p1", "Star", "QB", "QB", "KC", 28, "Active", 15, null, null, ["QB"], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
            ["p2"] = new Player("p2", "Mid", "RB", "RB", "BUF", 25, "Active", 22, null, null, ["RB"], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
            ["p3"] = new Player("p3", "Late", "WR", "WR", "DAL", 23, "Active", 88, null, null, ["WR"], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
            ["p4"] = new Player("p4", "Undrafted", "Guy", "TE", "NYG", 22, "Active", 85, null, null, ["TE"], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
        };
        client.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>()).Returns(allPlayers);

        var results = await service.GetRosterKeeperValuesAsync("lg1", "rob");

        results.Should().HaveCount(4);

        // Round 1 pick -> can't keep
        var starQb = results.First(r => r.Player.PlayerId == "p1");
        starQb.DraftRound.Should().Be(1);
        starQb.CanBeKept.Should().BeFalse();
        starQb.KeeperCostRound.Should().BeNull();
        starQb.IsStarter.Should().BeTrue();

        // Round 5 pick -> keeper cost round 2
        var midRb = results.First(r => r.Player.PlayerId == "p2");
        midRb.DraftRound.Should().Be(5);
        midRb.CanBeKept.Should().BeTrue();
        midRb.KeeperCostRound.Should().Be(2);
        midRb.IsStarter.Should().BeTrue();

        // Round 15 pick, was keeper -> keeper cost round 12
        var lateWr = results.First(r => r.Player.PlayerId == "p3");
        lateWr.DraftRound.Should().Be(15);
        lateWr.CanBeKept.Should().BeTrue();
        lateWr.KeeperCostRound.Should().Be(12);
        lateWr.WasKeptLastYear.Should().BeTrue();
        lateWr.IsStarter.Should().BeFalse();

        // Undrafted -> keeper cost round 10
        var undrafted = results.First(r => r.Player.PlayerId == "p4");
        undrafted.DraftRound.Should().BeNull();
        undrafted.CanBeKept.Should().BeTrue();
        undrafted.KeeperCostRound.Should().Be(10);
    }

    [Fact]
    public async Task GetRosterKeeperValuesAsync_ReturnsEmpty_WhenLeagueNotFound()
    {
        var (service, client) = Create();
        client.GetLeagueAsync("bad", Arg.Any<CancellationToken>()).Returns((League?)null);

        var results = await service.GetRosterKeeperValuesAsync("bad", "rob");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRosterKeeperValuesAsync_UsesCurrentLeagueDraft_WhenComplete()
    {
        var (service, client) = Create();

        client.GetLeagueAsync("lg1", Arg.Any<CancellationToken>())
            .Returns(new League("lg1", "Test", "in_season", "nfl", "2025", null, 8, "d1", "lg_prev", null, null, null, null));

        // Current league HAS a completed draft
        client.GetLeagueDraftsAsync("lg1", Arg.Any<CancellationToken>())
            .Returns(new List<Draft>
            {
                new("d1", "lg1", "complete", "snake", "2025", "nfl", null, null, null, null, null, null, null, null, null, null)
            });

        client.GetDraftPicksAsync("d1", Arg.Any<CancellationToken>())
            .Returns(new List<DraftPick>
            {
                new("p1", "u1", 1, 7, 1, 7, null, "d1",
                    new DraftPickMetadata("p1", "Test", "Player", "WR", "SF", "Active", "nfl", "1", null, null)),
            });

        var user = new User("u1", "rob", "Rob", null);
        client.GetUserAsync("rob", Arg.Any<CancellationToken>()).Returns(user);
        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>())
            .Returns(new List<Roster> { new(1, "u1", "lg1", ["p1"], ["p1"], null, null, null) });
        client.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Player>
            {
                ["p1"] = new Player("p1", "Test", "Player", "WR", "SF", 25, "Active", 1, null, null, ["WR"], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
            });

        var results = await service.GetRosterKeeperValuesAsync("lg1", "rob");

        results.Should().HaveCount(1);
        results[0].DraftRound.Should().Be(7);
        results[0].KeeperCostRound.Should().Be(4); // 7 - 3

        // Should NOT have called previous league drafts
        await client.DidNotReceive().GetLeagueDraftsAsync("lg_prev", Arg.Any<CancellationToken>());
    }

    // -- GetDeclaredKeepersAsync --

    [Fact]
    public async Task GetDeclaredKeepersAsync_ReturnsAllTeamsWithEmptyKeepers_WhenNoneDeclared()
    {
        var (service, client) = Create();

        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<Roster>
        {
            new(1, "u1", "lg1", ["p1"], ["p1"], null, null, null, Keepers: null),
            new(2, "u2", "lg1", ["p2"], ["p2"], null, null, null, Keepers: new List<string>()),
        });
        client.GetLeagueUsersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<LeagueUser>
        {
            new("u1", "alice", "Alice", null, true, new Dictionary<string, string> { ["team_name"] = "A's" }),
            new("u2", "bob",   "Bob",   null, true, null),
        });

        var result = await service.GetDeclaredKeepersAsync("lg1");

        result.Should().HaveCount(2);
        result.Should().OnlyContain(t => t.Keepers.Count == 0);

        var alice = result.Single(t => t.Username == "alice");
        alice.DisplayName.Should().Be("Alice");
        alice.TeamName.Should().Be("A's");

        var bob = result.Single(t => t.Username == "bob");
        bob.DisplayName.Should().Be("Bob");
        bob.TeamName.Should().BeNull();

        // No team has keepers => the player catalogue should NOT be downloaded
        await client.DidNotReceive().GetAllPlayersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDeclaredKeepersAsync_ResolvesPlayerIdsToPlayers_WhenAnyTeamDeclared()
    {
        var (service, client) = Create();

        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<Roster>
        {
            new(1, "u1", "lg1", ["3086", "SEA", "999"], ["3086"], null, null, null, Keepers: ["3086", "SEA"]),
            new(2, "u2", "lg1", ["p2"], ["p2"], null, null, null, Keepers: null),
        });
        client.GetLeagueUsersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<LeagueUser>
        {
            new("u1", "alice", "Alice", null, true, new Dictionary<string, string> { ["team_name"] = "A's" }),
            new("u2", "bob",   "Bob",   null, true, null),
        });
        client.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>()).Returns(new Dictionary<string, Player>
        {
            ["3086"] = new Player("3086", "Tom", "Brady", "QB", "NE", 40, "Active", 12, null, null, ["QB"], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
        });

        var result = await service.GetDeclaredKeepersAsync("lg1");

        result.Should().HaveCount(2);

        var alice = result.Single(t => t.Username == "alice");
        alice.Keepers.Should().HaveCount(2);
        alice.Keepers.Should().Contain(p => p.PlayerId == "3086" && p.LastName == "Brady");
        // Team defense should be synthesized
        alice.Keepers.Should().Contain(p => p.PlayerId == "SEA" && p.Position == "DEF");

        var bob = result.Single(t => t.Username == "bob");
        bob.Keepers.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDeclaredKeepersAsync_SkipsUnknownPlayerIds()
    {
        var (service, client) = Create();

        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<Roster>
        {
            new(1, "u1", "lg1", ["good", "ghost"], ["good"], null, null, null, Keepers: ["good", "ghost", ""]),
        });
        client.GetLeagueUsersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<LeagueUser>
        {
            new("u1", "alice", "Alice", null, true, null),
        });
        client.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>()).Returns(new Dictionary<string, Player>
        {
            ["good"] = new Player("good", "Real", "Guy", "RB", "DET", 25, "Active", 21, null, null, ["RB"], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
        });

        var result = await service.GetDeclaredKeepersAsync("lg1");

        result.Should().HaveCount(1);
        result[0].Keepers.Should().HaveCount(1);
        result[0].Keepers[0].PlayerId.Should().Be("good");
    }

    [Fact]
    public async Task GetDeclaredKeepersForUserAsync_ReturnsNull_WhenUserNotFound()
    {
        var (service, client) = Create();
        client.GetUserAsync("ghost", Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await service.GetDeclaredKeepersForUserAsync("lg1", "ghost");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetDeclaredKeepersForUserAsync_ReturnsNull_WhenUserHasNoRosterInLeague()
    {
        var (service, client) = Create();
        client.GetUserAsync("alice", Arg.Any<CancellationToken>()).Returns(new User("u1", "alice", "Alice", null));
        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<Roster>
        {
            new(1, "u_other", "lg1", [], [], null, null, null, Keepers: null),
        });

        var result = await service.GetDeclaredKeepersForUserAsync("lg1", "alice");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetDeclaredKeepersForUserAsync_ReturnsTeam_WithEmptyKeepers_WhenNoneDeclared()
    {
        var (service, client) = Create();
        client.GetUserAsync("alice", Arg.Any<CancellationToken>()).Returns(new User("u1", "alice", "Alice", null));
        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<Roster>
        {
            new(1, "u1", "lg1", ["p1"], ["p1"], null, null, null, Keepers: null),
        });
        client.GetLeagueUsersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<LeagueUser>
        {
            new("u1", "alice", "Alice", null, true, new Dictionary<string, string> { ["team_name"] = "A's" }),
        });

        var result = await service.GetDeclaredKeepersForUserAsync("lg1", "alice");

        result.Should().NotBeNull();
        result!.RosterId.Should().Be(1);
        result.Username.Should().Be("alice");
        result.TeamName.Should().Be("A's");
        result.Keepers.Should().BeEmpty();

        await client.DidNotReceive().GetAllPlayersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDeclaredKeepersForUserAsync_ResolvesDeclaredPlayers()
    {
        var (service, client) = Create();
        client.GetUserAsync("alice", Arg.Any<CancellationToken>()).Returns(new User("u1", "alice", "Alice", null));
        client.GetLeagueRostersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<Roster>
        {
            new(1, "u1", "lg1", ["3086"], ["3086"], null, null, null, Keepers: ["3086"]),
        });
        client.GetLeagueUsersAsync("lg1", Arg.Any<CancellationToken>()).Returns(new List<LeagueUser>
        {
            new("u1", "alice", "Alice", null, true, null),
        });
        client.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>()).Returns(new Dictionary<string, Player>
        {
            ["3086"] = new Player("3086", "Tom", "Brady", "QB", "NE", 40, "Active", 12, null, null, ["QB"], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
        });

        var result = await service.GetDeclaredKeepersForUserAsync("lg1", "alice");

        result.Should().NotBeNull();
        result!.Keepers.Should().ContainSingle();
        result.Keepers[0].LastName.Should().Be("Brady");
    }
}
