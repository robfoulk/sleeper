using System.Collections.Concurrent;
using System.Text.Json;
using GitHub.Copilot;
using Sleeper.RosterReport.Agents;

namespace Sleeper.RosterReport.Copilot;

#pragma warning disable GHCP001 // Capture the SDK's experimental multiplier cost for spike analysis.

internal sealed class CopilotUsageLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ConcurrentQueue<CopilotUsageRecord> _records = new();

    public IReadOnlyList<CopilotUsageRecord> Records => _records.ToArray();

    public void Record(string sessionId, AgentCallContext context, AssistantUsageData usage)
    {
        _records.Enqueue(new CopilotUsageRecord(
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: sessionId,
            ApiCallId: usage.ApiCallId,
            ProviderCallId: usage.ProviderCallId,
            ServiceRequestId: usage.ServiceRequestId,
            Week: context.Week,
            Role: context.Role,
            Subject: context.Subject,
            Model: usage.Model,
            ReasoningEffort: usage.ReasoningEffort,
            InputTokens: usage.InputTokens,
            OutputTokens: usage.OutputTokens,
            ReasoningTokens: usage.ReasoningTokens,
            CacheReadTokens: usage.CacheReadTokens,
            CacheWriteTokens: usage.CacheWriteTokens,
            DurationMilliseconds: usage.Duration?.TotalMilliseconds,
            FinishReason: usage.FinishReason,
            Cost: usage.Cost,
            TotalNanoAiu: usage.CopilotUsage?.TotalNanoAiu,
            TotalAiu: usage.CopilotUsage?.TotalNanoAiu / 1_000_000_000d));
    }

    public void Write(string usageDirectory)
    {
        Directory.CreateDirectory(usageDirectory);
        var records = Records;
        var jsonl = string.Join(
            Environment.NewLine,
            records.Select(record => JsonSerializer.Serialize(record)));
        File.WriteAllText(
            Path.Combine(usageDirectory, "events.jsonl"),
            jsonl.Length == 0 ? "" : jsonl + Environment.NewLine);

        var summary = CopilotUsageSummary.Create(records);
        File.WriteAllText(
            Path.Combine(usageDirectory, "summary.json"),
            JsonSerializer.Serialize(summary, JsonOptions));
        File.WriteAllText(
            Path.Combine(usageDirectory, "summary.md"),
            summary.ToMarkdown());
    }
}

internal sealed record CopilotUsageRecord(
    DateTimeOffset Timestamp,
    string SessionId,
    string? ApiCallId,
    string? ProviderCallId,
    string? ServiceRequestId,
    int? Week,
    string Role,
    string? Subject,
    string Model,
    string? ReasoningEffort,
    long? InputTokens,
    long? OutputTokens,
    long? ReasoningTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    double? DurationMilliseconds,
    string? FinishReason,
    double? Cost,
    double? TotalNanoAiu,
    double? TotalAiu);

internal sealed record CopilotUsageSummary(
    int ModelCalls,
    long InputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    double Cost,
    double TotalNanoAiu,
    double TotalAiu,
    IReadOnlyList<CopilotUsageGroup> ByRole,
    IReadOnlyList<CopilotUsageGroup> ByWeek,
    IReadOnlyList<CopilotUsageGroup> ByModel,
    CopilotUsageProjection? FullSeasonWriterProjection)
{
    public static CopilotUsageSummary Create(IReadOnlyList<CopilotUsageRecord> records)
    {
        var writerRecords = records
            .Where(record => !string.Equals(record.Role, "evaluation", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var measuredWeeks = writerRecords
            .Where(record => record.Week is not null)
            .Select(record => record.Week!.Value)
            .Distinct()
            .Count();
        var projection = measuredWeeks == 0
            ? null
            : CopilotUsageProjection.Create(writerRecords, measuredWeeks, projectedWeeks: 17);

        return new(
            records.Count,
            records.Sum(r => r.InputTokens ?? 0),
            records.Sum(r => r.OutputTokens ?? 0),
            records.Sum(r => r.ReasoningTokens ?? 0),
            records.Sum(r => r.CacheReadTokens ?? 0),
            records.Sum(r => r.CacheWriteTokens ?? 0),
            records.Sum(r => r.Cost ?? 0),
            records.Sum(r => r.TotalNanoAiu ?? 0),
            records.Sum(r => r.TotalAiu ?? 0),
            Group(records, r => r.Role),
            Group(records, r => r.Week?.ToString() ?? "none"),
            Group(records, r => r.Model),
            projection);
    }

    private static List<CopilotUsageGroup> Group(
        IReadOnlyList<CopilotUsageRecord> records,
        Func<CopilotUsageRecord, string> key)
        => records
            .GroupBy(key)
            .OrderBy(group => group.Key)
            .Select(group => new CopilotUsageGroup(
                group.Key,
                group.Count(),
                group.Sum(r => r.InputTokens ?? 0),
                group.Sum(r => r.OutputTokens ?? 0),
                group.Sum(r => r.Cost ?? 0),
                group.Sum(r => r.TotalNanoAiu ?? 0),
                group.Sum(r => r.TotalAiu ?? 0)))
            .ToList();

    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# GitHub Copilot usage");
        sb.AppendLine();
        sb.AppendLine($"- Model calls: **{ModelCalls}**");
        sb.AppendLine($"- Input tokens: **{InputTokens:N0}**");
        sb.AppendLine($"- Output tokens: **{OutputTokens:N0}**");
        sb.AppendLine($"- Reasoning tokens: **{ReasoningTokens:N0}**");
        sb.AppendLine($"- SDK multiplier cost: **{Cost:0.####}**");
        sb.AppendLine($"- AI units: **{TotalAiu:0.########}** ({TotalNanoAiu:0} nano-AIU)");
        sb.AppendLine();
        sb.AppendLine("_AI units and multiplier cost are SDK telemetry, not a dollar invoice or guaranteed premium-request count._");
        sb.AppendLine();
        AppendTable(sb, "By role", ByRole);
        AppendTable(sb, "By week", ByWeek);
        AppendTable(sb, "By model", ByModel);
        if (FullSeasonWriterProjection is not null)
        {
            var projection = FullSeasonWriterProjection;
            sb.AppendLine("## Full-season writer projection");
            sb.AppendLine();
            sb.AppendLine($"Projected from **{projection.MeasuredWeeks} measured weeks** to **{projection.ProjectedWeeks} weeks**. Evaluator-only spike usage is excluded.");
            sb.AppendLine();
            sb.AppendLine($"- Model calls: **{projection.ModelCalls:0.##}**");
            sb.AppendLine($"- Input tokens: **{projection.InputTokens:0}**");
            sb.AppendLine($"- Output tokens: **{projection.OutputTokens:0}**");
            sb.AppendLine($"- SDK multiplier cost: **{projection.Cost:0.####}**");
            sb.AppendLine($"- AI units: **{projection.TotalAiu:0.########}**");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendTable(
        System.Text.StringBuilder sb,
        string heading,
        IReadOnlyList<CopilotUsageGroup> groups)
    {
        sb.AppendLine($"## {heading}");
        sb.AppendLine();
        sb.AppendLine("| Name | Calls | Input | Output | Cost | AIU |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var group in groups)
            sb.AppendLine($"| {group.Name} | {group.ModelCalls} | {group.InputTokens:N0} | {group.OutputTokens:N0} | {group.Cost:0.####} | {group.TotalAiu:0.########} |");
        sb.AppendLine();
    }
}

internal sealed record CopilotUsageGroup(
    string Name,
    int ModelCalls,
    long InputTokens,
    long OutputTokens,
    double Cost,
    double TotalNanoAiu,
    double TotalAiu);

internal sealed record CopilotUsageProjection(
    int MeasuredWeeks,
    int ProjectedWeeks,
    double ModelCalls,
    double InputTokens,
    double OutputTokens,
    double Cost,
    double TotalNanoAiu,
    double TotalAiu)
{
    public static CopilotUsageProjection Create(
        IReadOnlyList<CopilotUsageRecord> records,
        int measuredWeeks,
        int projectedWeeks)
    {
        var factor = (double)projectedWeeks / measuredWeeks;
        return new CopilotUsageProjection(
            measuredWeeks,
            projectedWeeks,
            records.Count * factor,
            records.Sum(record => record.InputTokens ?? 0) * factor,
            records.Sum(record => record.OutputTokens ?? 0) * factor,
            records.Sum(record => record.Cost ?? 0) * factor,
            records.Sum(record => record.TotalNanoAiu ?? 0) * factor,
            records.Sum(record => record.TotalAiu ?? 0) * factor);
    }
}
