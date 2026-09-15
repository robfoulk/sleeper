using FluentAssertions;
using Sleeper.RosterReport.Copilot;

namespace Sleeper.RosterReport.Tests;

public class CopilotUsageLedgerTests
{
    [Fact]
    public void Create_AggregatesUsageByRoleWeekAndModel()
    {
        var records = new[]
        {
            Usage(1, "game", "model-a", 100, 20, 0.5, 500_000_000),
            Usage(1, "league", "model-a", 200, 30, 0.7, 700_000_000),
            Usage(2, "evaluation", "model-b", 300, 40, 1.0, 1_000_000_000)
        };

        var summary = CopilotUsageSummary.Create(records);

        summary.ModelCalls.Should().Be(3);
        summary.InputTokens.Should().Be(600);
        summary.OutputTokens.Should().Be(90);
        summary.Cost.Should().BeApproximately(2.2, 0.0001);
        summary.TotalAiu.Should().BeApproximately(2.2, 0.0001);
        summary.ByRole.Should().Contain(group => group.Name == "evaluation" && group.ModelCalls == 1);
        summary.ByWeek.Should().Contain(group => group.Name == "1" && group.ModelCalls == 2);
        summary.ByModel.Should().Contain(group => group.Name == "model-a" && group.ModelCalls == 2);
        summary.FullSeasonWriterProjection.Should().NotBeNull();
        summary.FullSeasonWriterProjection!.MeasuredWeeks.Should().Be(1);
        summary.FullSeasonWriterProjection.ModelCalls.Should().Be(34);
        summary.FullSeasonWriterProjection.TotalAiu.Should().BeApproximately(20.4, 0.0001);
    }

    private static CopilotUsageRecord Usage(
        int week,
        string role,
        string model,
        long input,
        long output,
        double cost,
        double nanoAiu)
        => new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString(),
            null,
            null,
            null,
            week,
            role,
            null,
            model,
            "medium",
            input,
            output,
            0,
            0,
            0,
            100,
            "stop",
            cost,
            nanoAiu,
            nanoAiu / 1_000_000_000d);
}
