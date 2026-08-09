namespace Sleeper.Api.Injuries;

public enum InjuryObservationScope
{
    Current,
    Historical
}

public sealed record InjuryObservationInput(
    string SleeperId,
    string Source,
    string? SourceUrl,
    string Status,
    string? PracticeStatus,
    string? PrimaryInjury,
    string? SecondaryInjury,
    string? Notes,
    string Confidence,
    DateTimeOffset? ObservedAt = null,
    DateTimeOffset? EffectiveAt = null,
    InjuryObservationScope Scope = InjuryObservationScope.Current,
    int Authority = 50,
    DateTimeOffset? ExpiresAt = null,
    int? Season = null,
    int? Week = null,
    string? SeasonType = null);

public sealed record InjuryObservation(
    long Id,
    string SleeperId,
    DateTimeOffset ObservedAt,
    DateTimeOffset? EffectiveAt,
    string Source,
    string? SourceUrl,
    string Status,
    string? PracticeStatus,
    string? PrimaryInjury,
    string? SecondaryInjury,
    string? Notes,
    string Confidence,
    InjuryObservationScope Scope,
    int Authority,
    DateTimeOffset? ExpiresAt,
    int? Season,
    int? Week,
    string? SeasonType);

public sealed record InjuryBatchWriteResult(
    IReadOnlyList<InjuryObservation> Observations,
    int InsertedCount,
    int DuplicateCount);

public sealed record CurrentInjury(
    string SleeperId,
    string Status,
    string? PracticeStatus,
    string? PrimaryInjury,
    string? SecondaryInjury,
    string? Notes,
    DateTimeOffset ObservedAt,
    DateTimeOffset? EffectiveAt,
    DateTimeOffset? ResolvedAt,
    string Source,
    string? SourceUrl,
    string Confidence,
    int Authority,
    DateTimeOffset? ExpiresAt);

public sealed record InjuryCohortPlayer(
    string SleeperId,
    int Rank,
    string Name,
    string Position,
    string? Team,
    string? GsisId,
    string RankingSource,
    DateTimeOffset RankedAt);

public sealed record InjuryHistorySummary(
    string SleeperId,
    int ObservationCount,
    int SeasonCount,
    int? FirstSeason,
    int? LastSeason,
    int OutCount,
    int DoubtfulCount,
    int QuestionableCount,
    int LimitedPracticeCount,
    int DidNotPracticeCount,
    DateTimeOffset? LastEffectiveAt);
