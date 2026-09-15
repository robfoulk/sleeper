using FluentAssertions;
using NSubstitute;
using Sleeper.Api.Injuries;
using Sleeper.Api.Models;

namespace Sleeper.Api.Tests;

public class WeeklyInjuryReportTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly InjuryReportWindow Window = new(Start, Start.AddDays(7));
    private static InjuryObservation Observation => new(1, "p1", Start.AddDays(2), Start.AddDays(2),
        "team", "https://example.test/report", "out", null, "Knee", null, null, "official",
        InjuryObservationScope.Current, 90, null, null, null, null);

    [Fact]
    public void Classify_RequiresExplicitDatedOnset_NotFreshImportOrHealthyBaseline()
    {
        var baseline = Observation with { Status = "healthy", EffectiveAt = Start.AddDays(-30) };
        WeeklyInjuryReportService.Classify(new(Observation, baseline), Window).Category.Should().Be(WeeklyInjuryCategory.UncertainTiming);
        var verified = Observation with { InjuryOccurredAt = Start.AddDays(1), SourcePublishedAt = Start.AddDays(2) };
        WeeklyInjuryReportService.Classify(new(verified, baseline), Window).Category.Should().Be(WeeklyInjuryCategory.ConfirmedNewInjury);
        WeeklyInjuryReportService.Classify(new(verified with { SourceUrl = null }, baseline), Window).Category.Should().Be(WeeklyInjuryCategory.UncertainTiming);
        WeeklyInjuryReportService.Classify(new(verified with { InjuryOccurredAt = Start.AddDays(-2) }, null), Window).Category.Should().Be(WeeklyInjuryCategory.ExistingInjuryUpdate);
        WeeklyInjuryReportService.Classify(new(verified with { SourcePublishedAt = Window.End }, null), Window).Category.Should().Be(WeeklyInjuryCategory.UncertainTiming);
    }

    [Fact]
    public void Classify_DistinguishesRecoveryAndNonInjuryAbsence()
    {
        WeeklyInjuryReportService.Classify(new(Observation with { Status = "healthy", PrimaryInjury = null }, Observation), Window)
            .Category.Should().Be(WeeklyInjuryCategory.Recovery);
        WeeklyInjuryReportService.Classify(new(Observation with { PrimaryInjury = "Coach's Decision" }, null), Window)
            .Category.Should().Be(WeeklyInjuryCategory.NonInjuryAbsence);
        WeeklyInjuryReportService.Classify(new(Observation with { PrimaryInjury = "Knee - restricted movement" }, null), Window)
            .Category.Should().Be(WeeklyInjuryCategory.UncertainTiming);
        WeeklyInjuryReportService.Classify(new(Observation, Observation with { EffectiveAt = Start.AddDays(1) }), Window)
            .Category.Should().Be(WeeklyInjuryCategory.ExistingInjuryUpdate);
    }

    [Fact]
    public async Task Build_UsesWeekRosterMembershipAndRetainsEvidence()
    {
        var store = Substitute.For<IInjuryStore>();
        var client = Substitute.For<ISleeperClient>();
        client.GetLeagueAsync("league", Arg.Any<CancellationToken>()).Returns(
            new League("league", "Test", "in_season", "nfl", "2026", "regular", 2, null, null, null, null, null, null));
        client.GetLeagueMatchupsAsync("league", 1, Arg.Any<CancellationToken>()).Returns(new List<Matchup>
        {
            new(7, 1, 10, null, ["p1"], ["p1", "untracked"], null, null)
        });
        store.GetChangesAsync(Window.Start, Window.End, Arg.Any<CancellationToken>()).Returns(new List<InjuryChange>
        {
            new(Observation, null), new(Observation with { SleeperId = "not-rostered" }, null)
        });
        store.GetCohortAsync(Arg.Any<CancellationToken>()).Returns(new List<InjuryCohortPlayer>
        {
            new("p1", 1, "Test Player", "TE", "LV", null, "test", Start)
        });
        var service = new WeeklyInjuryReportService(store, client);
        var report = await service.BuildAsync("league", 2026, 1, Window);
        report.Entries.Should().ContainSingle();
        report.Entries[0].RosterId.Should().Be(7);
        report.Entries[0].Started.Should().BeTrue();
        report.TrackedPlayers.Should().Be(1);
        report.RosteredPlayers.Should().Be(2);
        report.ToMarkdown().Should().Contain("Test Player").And.Contain("https://example.test/report");
        report.ToJson().Should().Contain("UncertainTiming");
        await client.DidNotReceiveWithAnyArgs().GetLeagueRostersAsync(default!, default);
        var mismatch = () => service.BuildAsync("league", 2025, 1, Window);
        await mismatch.Should().ThrowAsync<ArgumentException>();
    }
}