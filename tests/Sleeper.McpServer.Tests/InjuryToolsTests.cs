using FluentAssertions;
using NSubstitute;
using Sleeper.Api.Injuries;
using Sleeper.McpServer.Tools;

namespace Sleeper.McpServer.Tests;

public class InjuryToolsTests
{
    [Fact]
    public async Task RecordInjuryObservation_ForwardsExplicitOnsetEvidence()
    {
        var store = Substitute.For<IInjuryStore>();
        var onset = new DateTimeOffset(2026, 9, 13, 17, 0, 0, TimeSpan.Zero);
        store.RecordAsync(Arg.Any<InjuryObservationInput>(), Arg.Any<CancellationToken>()).Returns(
            new InjuryObservation(1, "p1", onset, onset, "team", "https://example.test/report", "out",
                null, "Knee", null, null, "official", InjuryObservationScope.Current, 90, null, null, null, null));
        await InjuryTools.RecordInjuryObservation(store, "p1", "team", "out", primary_injury: "Knee",
            source_url: "https://example.test/report", confidence: "official", effective_at: onset,
            injury_occurred_at: onset, source_published_at: onset.AddHours(2));
        await store.Received().RecordAsync(Arg.Is<InjuryObservationInput>(input =>
            input.InjuryOccurredAt == onset && input.SourcePublishedAt == onset.AddHours(2)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WeeklyReport_RejectsInvalidFormatBeforeAccessingData()
    {
        var store = Substitute.For<IInjuryStore>();
        var client = Substitute.For<Sleeper.Api.ISleeperClient>();
        var result = await InjuryTools.GetWeeklyInjuryReport(new(store, client), 2026, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), format: "invalid");
        result.Should().StartWith("Error:");
        await client.DidNotReceiveWithAnyArgs().GetLeagueAsync(default!, default);
    }

    [Fact]
    public async Task GetInjuryChanges_PreservesEvidenceAndDisclosesTruncation()
    {
        var store = Substitute.For<IInjuryStore>();
        var start = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var observation = new InjuryObservation(
            1, "p1", start, start, "team", "https://example.test/report", "out", "DNP",
            "Knee", null, "Dated report", "official", InjuryObservationScope.Current, 90,
            start.AddDays(2), null, null, null);
        store.GetChangesAsync(start, start.AddDays(7), Arg.Any<CancellationToken>())
            .Returns(new List<InjuryChange>
            {
                new(observation, null),
                new(observation with { Id = 2, EffectiveAt = start.AddDays(1), Status = "healthy" }, observation)
            });
        store.GetCohortAsync(Arg.Any<CancellationToken>()).Returns(new List<InjuryCohortPlayer>
        {
            new("p1", 1, "Test Player", "TE", "LV", null, "test", start)
        });

        var result = await InjuryTools.GetInjuryChanges(store, start, start.AddDays(7), limit: 1);

        using var document = System.Text.Json.JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("TotalChanges").GetInt32().Should().Be(2);
        root.GetProperty("Truncated").GetBoolean().Should().BeTrue();
        root.GetProperty("Caveat").GetString().Should().Contain("not confirmed injury onset");
        var change = root.GetProperty("Changes")[0];
        change.GetProperty("PlayerName").GetString().Should().Be("Test Player");
        change.GetProperty("Observation").GetProperty("Status").GetString().Should().Be("healthy");
        change.GetProperty("PreviousObservation").GetProperty("SourceUrl").GetString().Should().Be("https://example.test/report");
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(32, 100)]
    [InlineData(7, 0)]
    [InlineData(7, 501)]
    public async Task GetInjuryChanges_RejectsInvalidWindowOrLimit(int days, int limit)
    {
        var store = Substitute.For<IInjuryStore>();
        var start = DateTimeOffset.UtcNow;

        var result = await InjuryTools.GetInjuryChanges(store, start, start.AddDays(days), limit);

        result.Should().StartWith("Error:");
        await store.DidNotReceiveWithAnyArgs().GetChangesAsync(default, default, default);
    }

    [Fact]
    public async Task RecordInjuryObservation_RejectsMissingRequiredFields()
    {
        var store = Substitute.For<IInjuryStore>();

        var result = await InjuryTools.RecordInjuryObservation(store, " ", "reporter", "out");

        result.Should().Be("Error: Sleeper player ID is required.");
        await store.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Fact]
    public async Task RecordInjuryObservation_RejectsUnsupportedConfidence()
    {
        var store = Substitute.For<IInjuryStore>();

        var result = await InjuryTools.RecordInjuryObservation(
            store,
            "p1",
            "reporter",
            "out",
            confidence: "guess");

        result.Should().Be("Error: Confidence must be 'official', 'reporter', or 'inferred'.");
        await store.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }
}
