using FluentAssertions;
using Sleeper.Api.Models;

namespace Sleeper.Api.Tests;

public class LeagueRosterConfigTests
{
    [Fact]
    public void FromLeague_ParsesYourLeague()
    {
        var league = new League("lg1", "FoulknFootball", "pre_draft", "nfl", "2026", null, 8, null, null,
            ["QB", "QB", "RB", "RB", "RB", "RB", "WR", "WR", "WR", "WR", "TE", "TE",
             "WRRB_FLEX", "WRRB_FLEX", "K", "K", "DEF", "DEF",
             "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN", "BN"],
            null, null, null);

        var config = LeagueRosterConfig.FromLeague(league);

        config.Teams.Should().Be(8);
        config.StarterSlots["QB"].Should().Be(2);
        config.StarterSlots["RB"].Should().Be(4);
        config.StarterSlots["WR"].Should().Be(4);
        config.StarterSlots["TE"].Should().Be(2);
        config.StarterSlots["K"].Should().Be(2);
        config.StarterSlots["DEF"].Should().Be(2);
        config.FlexSlots.Should().Be(2);
        config.FlexEligiblePositions.Should().BeEquivalentTo(["RB", "WR"]);
        config.GetEffectiveStarters("RB").Should().Be(6);
        config.GetEffectiveStarters("WR").Should().Be(6);
        config.GetEffectiveStarters("TE").Should().Be(2);
        config.BenchSlots.Should().Be(12);
        config.TotalRosterSize.Should().Be(30);
    }

    [Fact]
    public void GetEffectiveStarters_IncludesFlex()
    {
        var league = new League("lg1", "Test", "in_season", "nfl", "2025", null, 10, null, null,
            ["QB", "RB", "RB", "WR", "WR", "TE", "FLEX", "K", "DEF", "BN", "BN", "BN", "BN", "BN", "BN"],
            null, null, null);

        var config = LeagueRosterConfig.FromLeague(league);

        config.GetEffectiveStarters("QB").Should().Be(1);  // no flex for QB
        config.GetEffectiveStarters("RB").Should().Be(3);  // 2 + 1 flex
        config.GetEffectiveStarters("WR").Should().Be(3);  // 2 + 1 flex
        config.GetEffectiveStarters("TE").Should().Be(2);  // 1 + 1 flex
        config.GetEffectiveStarters("K").Should().Be(1);   // no flex
    }

    [Fact]
    public void FromLeague_SuperFlex()
    {
        var league = new League("lg1", "SuperFlex", "in_season", "nfl", "2025", null, 12, null, null,
            ["QB", "RB", "RB", "WR", "WR", "TE", "FLEX", "SUPER_FLEX", "K", "DEF", "BN", "BN", "BN", "BN", "BN", "BN"],
            null, null, null);

        var config = LeagueRosterConfig.FromLeague(league);

        config.StarterSlots["QB"].Should().Be(1);
        config.FlexSlots.Should().Be(2); // FLEX + SUPER_FLEX
    }
}
