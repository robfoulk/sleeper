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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
