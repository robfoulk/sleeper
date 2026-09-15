using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleeper.Api.Injuries;

public sealed record InjuryReportWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public void Validate()
    {
        if (End <= Start || End - Start > TimeSpan.FromDays(31))
            throw new ArgumentException("Injury window end must be after start and no more than 31 days later.");
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WeeklyInjuryCategory
{
    ConfirmedNewInjury,
    ExistingInjuryUpdate,
    Recovery,
    NonInjuryAbsence,
    UncertainTiming
}

public sealed record WeeklyInjuryEntry(
    string SleeperId,
    string PlayerName,
    int RosterId,
    bool Started,
    WeeklyInjuryCategory Category,
    string Reason,
    InjuryObservation Observation,
    InjuryObservation? PreviousObservation);

public sealed record WeeklyInjuryReport(
    string LeagueId,
    int Season,
    int Week,
    InjuryReportWindow Window,
    DateTimeOffset GeneratedAt,
    int RosteredPlayers,
    int TrackedPlayers,
    IReadOnlyList<WeeklyInjuryEntry> Entries,
    IReadOnlyList<string> Warnings)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string ToMarkdown()
    {
        var text = new StringBuilder();
        text.AppendLine($"# Week {Week} Injury Report ({Season})");
        text.AppendLine($"League: {LeagueId}. Effective-time window: {Window.Start:O} to {Window.End:O} (exclusive).");
        text.AppendLine($"Generated: {GeneratedAt:O}. Tracked cohort coverage: {TrackedPlayers}/{RosteredPlayers} rostered players.");
        foreach (var warning in Warnings)
            text.AppendLine($"- {warning}");
        foreach (var category in Enum.GetValues<WeeklyInjuryCategory>())
        {
            text.AppendLine();
            text.AppendLine($"## {category switch
            {
                WeeklyInjuryCategory.ConfirmedNewInjury => "Confirmed New Injuries",
                WeeklyInjuryCategory.ExistingInjuryUpdate => "Existing Injury Updates",
                WeeklyInjuryCategory.Recovery => "Recoveries",
                WeeklyInjuryCategory.NonInjuryAbsence => "Non-Injury Absences",
                _ => "Uncertain Timing"
            }}");
            var entries = Entries.Where(entry => entry.Category == category).ToList();
            if (entries.Count == 0)
                text.AppendLine("No qualifying recorded changes. This does not establish that no injuries occurred.");
            foreach (var entry in entries)
            {
                var observation = entry.Observation;
                text.AppendLine($"- **{Clean(entry.PlayerName)}** (roster {entry.RosterId}, {(entry.Started ? "starter" : "bench")})" +
                    $": {Clean(observation.Status)}; {Clean(observation.PrimaryInjury ?? "injury detail unknown")}. {entry.Reason}");
                text.AppendLine($"  Source: {Clean(observation.Source)}; URL: {Clean(observation.SourceUrl ?? "unavailable")}; " +
                    $"effective: {observation.EffectiveAt ?? observation.ObservedAt:O}; observed: {observation.ObservedAt:O}; " +
                    $"published: {observation.SourcePublishedAt?.ToString("O") ?? "unknown"}; " +
                    $"injury onset: {observation.InjuryOccurredAt?.ToString("O") ?? "unknown"}.");
                if (entry.PreviousObservation is { } previous)
                    text.AppendLine($"  Previous same-source status: {Clean(previous.Status)} at {previous.EffectiveAt ?? previous.ObservedAt:O}.");
                if (!string.IsNullOrWhiteSpace(observation.Notes))
                    text.AppendLine($"  Evidence notes: {Clean(observation.Notes)}");
            }
        }
        return text.ToString();
    }

    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace("*", "\\*");
}