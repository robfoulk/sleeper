using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Sleeper.Api.Injuries;

namespace Sleeper.Api.Tests;

public sealed class InjuryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sleeper-injuries-{Guid.NewGuid():N}");
    private readonly SqliteInjuryStore _store;

    public InjuryStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteInjuryStore(Options.Create(new InjuryStoreOptions
        {
            DatabasePath = Path.Combine(_directory, "injuries.db")
        }));
    }

    [Fact]
    public async Task HistoricalObservation_DoesNotMaterializeCurrentState()
    {
        await _store.RecordAsync(new InjuryObservationInput(
            "p1", "nflverse", "https://example.test/history", "out", "DNP", "Knee", null,
            "2025 REG week 4", "data-provider",
            ObservedAt: new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
            Scope: InjuryObservationScope.Historical,
            Season: 2025,
            Week: 4,
            SeasonType: "REG"));

        (await _store.GetCurrentAsync("p1")).Should().BeNull();
        var summary = await _store.GetHistorySummaryAsync("p1");
        summary.ObservationCount.Should().Be(1);
        summary.SeasonCount.Should().Be(1);
        summary.OutCount.Should().Be(1);
    }

    [Fact]
    public async Task CurrentState_UsesEffectiveTimeThenAuthorityAndHonorsExpiry()
    {
        var effectiveAt = DateTimeOffset.UtcNow;
        await _store.RecordBatchAsync([
            new InjuryObservationInput(
                "p1", "reporter", null, "questionable", null, "Hamstring", null, null, "reporter",
                effectiveAt.AddHours(1), effectiveAt.AddHours(1), Authority: 40, ExpiresAt: effectiveAt.AddDays(2)),
            new InjuryObservationInput(
                "p1", "team", null, "healthy", "full", null, null, null, "official",
                effectiveAt, effectiveAt, Authority: 90, ExpiresAt: effectiveAt.AddDays(2)),
            new InjuryObservationInput(
                "expired", "sleeper", null, "out", null, null, null, null, "platform",
                effectiveAt, effectiveAt, Authority: 60, ExpiresAt: effectiveAt.AddMinutes(-1))
        ]);

        (await _store.GetCurrentAsync("p1"))!.Status.Should().Be("questionable");
        (await _store.GetCurrentAsync("expired")).Should().BeNull();
    }

    [Fact]
    public async Task Deduplication_PreservesEffectiveDateAndConfidenceCorrections()
    {
        var observedAt = DateTimeOffset.UtcNow;
        await _store.RecordAsync(new InjuryObservationInput(
            "p1", "team", null, "questionable", null, "Knee", null, null, "reporter",
            observedAt, observedAt.AddDays(-1)));
        await _store.RecordAsync(new InjuryObservationInput(
            "p1", "team", null, "questionable", null, "Knee", null, null, "official",
            observedAt, observedAt));

        (await _store.GetTimelineAsync("p1")).Should().HaveCount(2);
        (await _store.GetCurrentAsync("p1"))!.Confidence.Should().Be("official");
    }

    [Fact]
    public async Task HistoricalDeduplication_IgnoresImportTime()
    {
        var input = new InjuryObservationInput(
            "p1", "nflverse", "https://example.test/2025", "questionable", "limited", "Knee", null,
            "2025 REG week 3", "data-provider",
            ObservedAt: DateTimeOffset.UtcNow,
            Scope: InjuryObservationScope.Historical,
            Season: 2025,
            Week: 3,
            SeasonType: "REG");
        var first = await _store.RecordAsync(input);
        var second = await _store.RecordAsync(input with { ObservedAt = DateTimeOffset.UtcNow.AddHours(1) });

        second.Id.Should().Be(first.Id);
        (await _store.GetHistoricalTimelineAsync("p1")).Should().ContainSingle();
    }

    [Fact]
    public async Task ReplaceCohort_ReplacesRowsInRankOrder()
    {
        var rankedAt = DateTimeOffset.UtcNow;
        await _store.ReplaceCohortAsync([
            new("p2", 2, "Second", "WR", "BUF", "g2", "test", rankedAt),
            new("p1", 1, "First", "RB", "ATL", "g1", "test", rankedAt)
        ]);
        await _store.ReplaceCohortAsync([
            new("p3", 1, "Replacement", "QB", "TEN", "g3", "test", rankedAt)
        ]);

        var cohort = await _store.GetCohortAsync();
        cohort.Should().ContainSingle();
        cohort[0].SleeperId.Should().Be("p3");
        cohort[0].Rank.Should().Be(1);
    }

    [Fact]
    public async Task Changes_ExcludeRepeatedSnapshotsAndHistoricalImports_AndIncludeRecovery()
    {
        var start = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var baseline = new InjuryObservationInput(
            "p1", "sleeper", null, "healthy", null, null, null, null, "platform",
            start.AddDays(-30), start.AddDays(-30));
        await _store.RecordBatchAsync([
            baseline,
            baseline with { Status = "out", ObservedAt = start, EffectiveAt = start },
            baseline with { Status = "out", ObservedAt = start.AddDays(1), EffectiveAt = start.AddDays(1) },
            baseline with { ObservedAt = start.AddDays(2), EffectiveAt = start.AddDays(2) },
            baseline with { SleeperId = "healthy", ObservedAt = start, EffectiveAt = start },
            baseline with { SleeperId = "historical", Status = "out", Scope = InjuryObservationScope.Historical,
                ObservedAt = start, EffectiveAt = start, Season = 2025, Week = 1 }
        ]);

        var changes = await _store.GetChangesAsync(start, start.AddDays(7));

        changes.Should().HaveCount(2);
        changes[0].Observation.Status.Should().Be("out");
        changes[0].PreviousObservation!.ObservedAt.Should().Be(start.AddDays(-30));
        changes[1].Observation.Status.Should().Be("healthy");
        changes[1].PreviousObservation!.Status.Should().Be("out");
    }

    [Fact]
    public async Task Changes_UseEffectiveHalfOpenWindow_AndCompareEachSourceSeparately()
    {
        var start = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var baseline = new InjuryObservationInput(
            "p1", "sleeper", null, "out", null, "Knee", null, null, "platform",
            start.AddDays(-1), start.AddDays(-1));
        await _store.RecordBatchAsync([
            baseline,
            baseline with { Source = "team", Status = "healthy", ObservedAt = start.AddHours(-1), EffectiveAt = start.AddHours(-1) },
            baseline with { ObservedAt = start, EffectiveAt = start },
            baseline with { SleeperId = "unknown-baseline", ObservedAt = start, EffectiveAt = start },
            baseline with { PrimaryInjury = "Ankle", ObservedAt = start.AddDays(1), EffectiveAt = start.AddDays(1) },
            baseline with { SleeperId = "late-report", ObservedAt = start.AddDays(1), EffectiveAt = start.AddDays(-2) },
            baseline with { SleeperId = "end-boundary", ObservedAt = start.AddDays(7), EffectiveAt = start.AddDays(7) }
        ]);

        var changes = await _store.GetChangesAsync(start.ToOffset(TimeSpan.FromHours(-4)), start.AddDays(7));

        changes.Should().HaveCount(2);
        changes[0].Observation.SleeperId.Should().Be("unknown-baseline");
        changes[0].PreviousObservation.Should().BeNull();
        changes[1].Observation.PrimaryInjury.Should().Be("Ankle");
        changes[1].PreviousObservation!.Source.Should().Be("sleeper");
        changes[1].PreviousObservation!.PrimaryInjury.Should().Be("Knee");
    }

    [Fact]
    public async Task Changes_RejectInvalidWindow()
    {
        var start = DateTimeOffset.UtcNow;
        var action = () => _store.GetChangesAsync(start, start);
        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task OnsetEvidence_RoundTripsAndIsAMaterialCorrection()
    {
        var start = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var input = new InjuryObservationInput("p1", "team", "https://example.test/injury", "out",
            null, "Ankle", null, null, "official", start.AddDays(2), start.AddDays(2));
        await _store.RecordAsync(input);
        var corrected = input with { InjuryOccurredAt = start.AddDays(1), SourcePublishedAt = start.AddDays(2) };
        var saved = await _store.RecordAsync(corrected);
        (await _store.RecordAsync(corrected)).Id.Should().Be(saved.Id);
        var timeline = await _store.GetTimelineAsync("p1");
        timeline[0].InjuryOccurredAt.Should().Be(corrected.InjuryOccurredAt);
        timeline[0].SourcePublishedAt.Should().Be(corrected.SourcePublishedAt);
        (await _store.GetChangesAsync(start, start.AddDays(7))).Should().HaveCount(2);
    }

    [Fact]
    public async Task OnsetEvidence_RequiresDatedSource()
    {
        var input = new InjuryObservationInput("p1", "sleeper", null, "out", null,
            "Knee", null, null, "platform", InjuryOccurredAt: DateTimeOffset.UtcNow);
        var action = () => _store.RecordAsync(input);
        await action.Should().ThrowAsync<ArgumentException>();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
