namespace Sleeper.Api.Injuries;

public sealed record InjuryImportResult(
    string Source,
    int Season,
    string SourceUrl,
    int SourceRows,
    int SkippedRows,
    int MappedRows,
    int ImportedRows,
    int UnmappedRows,
    IReadOnlyList<string> UnmappedGsisIds,
    DateTimeOffset? ObservationAt = null);

public sealed record InjurySourcePlan(
    string Source,
    string Role,
    string Freshness,
    string Access,
    string Guidance);

public sealed record TeamInjuryPage(
    string TeamCode,
    string TeamName,
    string OfficialSiteUrl,
    string CandidateInjuryPageUrl,
    bool RequiresVerification);
