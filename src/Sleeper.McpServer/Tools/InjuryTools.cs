using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sleeper.Api.Injuries;

namespace Sleeper.McpServer.Tools;

[McpServerToolType]
public static class InjuryTools
{
    [McpServerTool, Description("Explain which injury sources to use for the current preseason and their freshness/authority policy.")]
    public static string GetCurrentSeasonInjurySourcePlan(IInjuryImporter importer)
    {
        var sb = new StringBuilder();
        foreach (var source in importer.GetCurrentSeasonSourcePlan())
            sb.AppendLine($"{source.Source} | role={source.Role} | freshness={source.Freshness} | access={source.Access} | {source.Guidance}");
        return sb.ToString();
    }

    [McpServerTool, Description("List deterministic candidate injury-report pages for all NFL teams. URLs are candidates and should be verified before treating them as authoritative.")]
    public static string GetTeamInjuryPageCatalog(IInjuryImporter importer)
    {
        var sb = new StringBuilder();
        foreach (var page in importer.GetTeamInjuryPages())
            sb.AppendLine($"{page.TeamCode} | {page.TeamName} | official={page.OfficialSiteUrl} | candidate={page.CandidateInjuryPageUrl} | requires_verification={page.RequiresVerification}");
        return sb.ToString();
    }

    [McpServerTool, Description("Preview or import the deterministic nflverse injury CSV for a season. Set commit=true to append mapped observations to the shared injury ledger.")]
    public static async Task<string> ImportNflverseInjuries(
        IInjuryImporter importer,
        [Description("NFL season, such as 2025 or 2026")] int season,
        [Description("Write observations to the shared ledger; false performs a dry run")] bool commit = false,
        CancellationToken ct = default)
    {
        if (ToolSupport.Positive(season, "Season") is { } validation)
            return validation;

        return await ToolSupport.TryAsync(async () =>
        {
            var result = await importer.ImportNflverseAsync(season, dryRun: !commit, ct);
            return $"nflverse {result.Season}: source_rows={result.SourceRows}, skipped_rows={result.SkippedRows}, mapped_rows={result.MappedRows}, " +
                   $"imported_rows={result.ImportedRows}, unmapped_rows={result.UnmappedRows}, dry_run={!commit}, url={result.SourceUrl}. " +
                   (result.UnmappedGsisIds.Count == 0
                       ? string.Empty
                       : $" Unmapped GSIS IDs (first {result.UnmappedGsisIds.Count}): {string.Join(", ", result.UnmappedGsisIds)}");
        });
    }

    [McpServerTool, Description("Preview or import the current Sleeper player injury snapshot. Set commit=true to append current statuses to the shared injury ledger.")]
    public static async Task<string> ImportSleeperInjuries(
        IInjuryImporter importer,
        [Description("Write observations to the shared ledger; false performs a dry run")] bool commit = false,
        CancellationToken ct = default)
    {
        return await ToolSupport.TryAsync(async () =>
        {
            var result = await importer.ImportSleeperAsync(dryRun: !commit, ct);
            return $"Sleeper snapshot: source_rows={result.SourceRows}, candidate_rows={result.MappedRows}, " +
                   $"imported_rows={result.ImportedRows}, dry_run={!commit}, observed_at={result.ObservationAt:O}.";
        });
    }

    [McpServerTool, Description("Get the current injury and availability status for a Sleeper player ID.")]
    public static async Task<string> GetInjuryStatus(
        IInjuryStore injuries,
        [Description("Sleeper player ID")] string sleeper_player_id,
        CancellationToken ct = default)
    {
        if (ToolSupport.Required(sleeper_player_id, "Sleeper player ID") is { } validation)
            return validation;

        return await ToolSupport.TryAsync(async () =>
        {
            var current = await injuries.GetCurrentAsync(sleeper_player_id, ct);
            if (current is null)
                return $"No current injury status recorded for Sleeper player {sleeper_player_id}.";

            return $"Sleeper player {current.SleeperId}: {current.Status}; practice: {current.PracticeStatus ?? "unknown"}; " +
                   $"injury: {current.PrimaryInjury ?? "none"}; observed: {current.ObservedAt:O}; " +
                   $"source: {current.Source}; confidence: {current.Confidence}; expires: {current.ExpiresAt?.ToString("O") ?? "never"}.";
        });
    }

    [McpServerTool, Description("Summarize historical injury-report appearances for a Sleeper player ID.")]
    public static async Task<string> GetInjuryHistorySummary(
        IInjuryStore injuries,
        [Description("Sleeper player ID")] string sleeper_player_id,
        CancellationToken ct = default)
    {
        if (ToolSupport.Required(sleeper_player_id, "Sleeper player ID") is { } validation)
            return validation;

        return await ToolSupport.TryAsync(async () =>
        {
            var summary = await injuries.GetHistorySummaryAsync(sleeper_player_id, ct);
            return $"Sleeper player {summary.SleeperId}: {summary.ObservationCount} historical reports across " +
                   $"{summary.SeasonCount} seasons ({summary.FirstSeason?.ToString() ?? "n/a"}-{summary.LastSeason?.ToString() ?? "n/a"}); " +
                   $"out={summary.OutCount}, doubtful={summary.DoubtfulCount}, questionable={summary.QuestionableCount}, " +
                   $"limited={summary.LimitedPracticeCount}, DNP={summary.DidNotPracticeCount}.";
        });
    }

    [McpServerTool, Description("Get the timestamped injury history for a Sleeper player ID.")]
    public static async Task<string> GetInjuryTimeline(
        IInjuryStore injuries,
        [Description("Sleeper player ID")] string sleeper_player_id,
        [Description("Maximum observations to return")] int limit = 20,
        CancellationToken ct = default)
    {
        if (ToolSupport.Required(sleeper_player_id, "Sleeper player ID") is { } validation)
            return validation;
        if (ToolSupport.Between(limit, 1, 500, "Limit") is { } limitValidation)
            return limitValidation;

        return await ToolSupport.TryAsync(async () =>
        {
            var timeline = await injuries.GetTimelineAsync(sleeper_player_id, limit, ct);
            if (timeline.Count == 0)
                return $"No injury history recorded for Sleeper player {sleeper_player_id}.";

            var sb = new StringBuilder();
            foreach (var entry in timeline)
                sb.AppendLine($"{entry.ObservedAt:O} | {entry.Status} | practice={entry.PracticeStatus ?? "unknown"} | " +
                              $"{entry.PrimaryInjury ?? "none"} | {entry.Source} | {entry.SourceUrl ?? "no URL"}");
            return sb.ToString();
        });
    }

    [McpServerTool, Description("Record a dated injury or availability observation for a Sleeper player ID.")]
    public static async Task<string> RecordInjuryObservation(
        IInjuryStore injuries,
        [Description("Sleeper player ID")] string sleeper_player_id,
        [Description("Source name, such as Sleeper, nflverse, team, or reporter")] string source,
        [Description("Current status, such as questionable, doubtful, out, IR, active, or healthy")] string status,
        [Description("Primary injury description")] string? primary_injury = null,
        [Description("Practice participation status")] string? practice_status = null,
        [Description("Source article or API URL")] string? source_url = null,
        [Description("Additional evidence or context")] string? notes = null,
        [Description("Confidence: official, reporter, or inferred")] string confidence = "reporter",
        [Description("When the source says this status became effective")] DateTimeOffset? effective_at = null,
        [Description("Hours before this current observation becomes stale")] int expires_in_hours = 168,
        CancellationToken ct = default)
    {
        if (ToolSupport.Required(sleeper_player_id, "Sleeper player ID") is { } idValidation)
            return idValidation;
        if (ToolSupport.Required(source, "Source") is { } sourceValidation)
            return sourceValidation;
        if (ToolSupport.Required(status, "Status") is { } statusValidation)
            return statusValidation;
        if (ToolSupport.Between(expires_in_hours, 1, 720, "Expiry hours") is { } expiryValidation)
            return expiryValidation;

        var normalizedConfidence = confidence.ToLowerInvariant();
        if (normalizedConfidence is not ("official" or "reporter" or "inferred"))
            return "Error: Confidence must be 'official', 'reporter', or 'inferred'.";

        return await ToolSupport.TryAsync(async () =>
        {
            var observedAt = DateTimeOffset.UtcNow;
            var authority = normalizedConfidence switch
            {
                "official" => 90,
                "reporter" => 50,
                _ => 30
            };
            var observation = await injuries.RecordAsync(new InjuryObservationInput(
                sleeper_player_id, source, source_url, status, practice_status,
                primary_injury, null, notes, normalizedConfidence, observedAt, effective_at ?? observedAt,
                InjuryObservationScope.Current, authority, observedAt.AddHours(expires_in_hours)), ct);
            return $"Recorded {observation.Status} for {observation.SleeperId} at {observation.ObservedAt:O}.";
        });
    }

    [McpServerTool, Description("Record that a Sleeper player's injury/availability concern has cleared without deleting prior history.")]
    public static async Task<string> ClearInjury(
        IInjuryStore injuries,
        [Description("Sleeper player ID")] string sleeper_player_id,
        [Description("Source confirming clearance")] string source,
        [Description("Source article or API URL")] string? source_url = null,
        [Description("Evidence or context")] string? notes = null,
        CancellationToken ct = default)
    {
        if (ToolSupport.Required(sleeper_player_id, "Sleeper player ID") is { } idValidation)
            return idValidation;
        if (ToolSupport.Required(source, "Source") is { } sourceValidation)
            return sourceValidation;

        return await ToolSupport.TryAsync(async () =>
        {
            var observation = await injuries.ResolveAsync(sleeper_player_id, source, source_url, notes, ct);
            return $"Cleared injury status for {observation.SleeperId} at {observation.ObservedAt:O}; prior observations remain in the timeline.";
        });
    }
}
