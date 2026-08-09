namespace Sleeper.Api.Injuries;

public interface IInjuryImporter
{
    Task<InjuryImportResult> ImportNflverseAsync(
        int season,
        bool dryRun = false,
        CancellationToken ct = default);

    Task<InjuryImportResult> ImportSleeperAsync(
        bool dryRun = false,
        CancellationToken ct = default);

    IReadOnlyList<InjurySourcePlan> GetCurrentSeasonSourcePlan();

    IReadOnlyList<TeamInjuryPage> GetTeamInjuryPages();
}
