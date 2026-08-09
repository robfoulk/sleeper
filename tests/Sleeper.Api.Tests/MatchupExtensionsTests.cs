using FluentAssertions;
using Sleeper.Api.Models;

namespace Sleeper.Api.Tests;

public class MatchupExtensionsTests
{
    [Fact]
    public void HasScoringData_ReturnsFalse_WhenPointsAreMissing()
    {
        var matchup = new Matchup(1, 1, null, null, null, null, null, null);

        matchup.HasScoringData().Should().BeFalse();
        matchup.ScoreOrZero().Should().Be(0m);
    }

    [Fact]
    public void HasScoringData_ReturnsFalse_ForUnstartedZeroScoreLineups()
    {
        var matchup = new Matchup(
            1,
            1,
            0m,
            null,
            null,
            null,
            [0m, 0m],
            new Dictionary<string, decimal> { ["p1"] = 0m, ["p2"] = 0m });

        matchup.HasScoringData().Should().BeFalse();
    }

    [Fact]
    public void HasScoringData_ReturnsTrue_WhenAnyPlayerHasScored()
    {
        var matchup = new Matchup(
            1,
            1,
            0m,
            null,
            null,
            null,
            null,
            new Dictionary<string, decimal> { ["p1"] = 0m, ["p2"] = 4.2m });

        matchup.HasScoringData().Should().BeTrue();
    }

    [Fact]
    public void ScoreOrZero_PrefersCustomPoints()
    {
        var matchup = new Matchup(1, 1, 100m, 87.5m, null, null, null, null);

        matchup.HasScoringData().Should().BeTrue();
        matchup.ScoreOrZero().Should().Be(87.5m);
    }
}
