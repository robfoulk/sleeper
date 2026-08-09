using FluentAssertions;
using Sleeper.Api.Models;
using Sleeper.Api.NflData.Services;

namespace Sleeper.Api.Tests;

public class AnalysisServiceEdgeCaseTests
{
    [Fact]
    public void GetLastCompletedSeason_UsesFallbackWhenPreviousSeasonIsInvalidDuringOffseason()
    {
        var state = new NflState(0, "2026", "off", null, null, 0, null, null, 0);

        AnalysisService.GetLastCompletedSeason(state, 2026).Should().Be(2025);
    }
}
