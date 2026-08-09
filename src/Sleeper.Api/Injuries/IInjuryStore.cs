namespace Sleeper.Api.Injuries;

public interface IInjuryStore
{
    Task<InjuryObservation> RecordAsync(InjuryObservationInput input, CancellationToken ct = default);
    Task<InjuryBatchWriteResult> RecordBatchAsync(
        IReadOnlyList<InjuryObservationInput> inputs,
        CancellationToken ct = default);
    Task<CurrentInjury?> GetCurrentAsync(string sleeperId, CancellationToken ct = default);
    Task<IReadOnlyList<InjuryObservation>> GetTimelineAsync(string sleeperId, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<InjuryObservation>> GetHistoricalTimelineAsync(string sleeperId, int limit = 50, CancellationToken ct = default);
    Task<InjuryHistorySummary> GetHistorySummaryAsync(string sleeperId, CancellationToken ct = default);
    Task ReplaceCohortAsync(IReadOnlyList<InjuryCohortPlayer> players, CancellationToken ct = default);
    Task<IReadOnlyList<InjuryCohortPlayer>> GetCohortAsync(CancellationToken ct = default);
    Task<InjuryObservation> ResolveAsync(
        string sleeperId,
        string source,
        string? sourceUrl,
        string? notes,
        CancellationToken ct = default);
}
