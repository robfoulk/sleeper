using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sleeper.RosterReport.Agents;
using Sleeper.RosterReport.Recap;

namespace Sleeper.RosterReport.Copilot;

public sealed record ProofreadResult(
    int Week,
    string Model,
    bool Pass,
    IReadOnlyList<string> Defects,
    string Summary,
    TimeSpan Elapsed
);

internal sealed class CopilotProofreaderRunner(
    CopilotAgentSettings settings,
    string proofreaderModel)
{
    private const string ProofreaderInstructions =
        "You are a strict, fast factual proofreader for a fantasy football recap newsletter. " +
        "Compare the supplied markdown article against the ground-truth evidence JSON. " +
        "Check for: incorrect scores, wrong winner/loser, wrong owner or team names, mathematical mistakes, " +
        "and ungrounded claims not supported by the evidence. " +
        "Return ONLY valid JSON matching: {\"Pass\": true/false, \"Defects\": [\"...\"], \"Summary\": \"...\"}. " +
        "No markdown fences or commentary outside the JSON.";

    public async Task RunAsync(
        int season,
        string runId,
        CancellationToken cancellationToken = default)
    {
        var runDirectory = Path.Combine(RecapPaths.WorkspaceRoot, "recap-runs", season.ToString(), runId);
        if (!Directory.Exists(runDirectory))
            throw new DirectoryNotFoundException($"Replay run directory '{runDirectory}' not found.");

        var copilotDir = Path.Combine(runDirectory, "copilot");
        var inputsDir = Path.Combine(runDirectory, "inputs");

        var usage = new CopilotUsageLedger();
        var proofSettings = settings with
        {
            WriterModel = proofreaderModel,
            EvaluatorModel = proofreaderModel
        };

        Console.WriteLine($"Starting proofreading spike on run '{runId}' using model '{proofreaderModel}'...");
        await using var host = await CopilotAgentHost.StartAsync(
            proofSettings,
            usage,
            cancellationToken,
            skipModelVerification: true).ConfigureAwait(false);

        var proofreader = host.CreateProofreader(ProofreaderInstructions, proofreaderModel);

        var results = new List<ProofreadResult>();

        for (var week = 1; week <= 17; week++)
        {
            var recapPath = Path.Combine(copilotDir, $"week-{week:D2}.md");
            var envelopePath = Path.Combine(inputsDir, $"week-{week:D2}-envelope.json");
            if (!File.Exists(recapPath) || !File.Exists(envelopePath))
                continue;

            Console.WriteLine($"Proofreading Week {week} with '{proofreaderModel}'...");
            var recapText = File.ReadAllText(recapPath);
            var envelopeJson = File.ReadAllText(envelopePath);

            var sw = Stopwatch.StartNew();
            var prompt = $"## GROUND TRUTH EVIDENCE (JSON)\n```json\n{envelopeJson}\n```\n\n## GENERATED ARTICLE TO PROOFREAD\n{recapText}";
            
            var response = await proofreader.GenerateAsync(
                prompt,
                new AgentCallContext(week, "proofreader", proofreaderModel),
                cancellationToken).ConfigureAwait(false);
            sw.Stop();

            var result = ParseResponse(week, proofreaderModel, response, sw.Elapsed);
            results.Add(result);

            Console.WriteLine($"  [Week {week}] Result: {(result.Pass ? "PASS" : "FAIL")} ({result.Defects.Count} defects found, {sw.Elapsed.TotalSeconds:F1}s)");
            foreach (var defect in result.Defects)
            {
                Console.WriteLine($"    - {defect}");
            }
        }

        var reportPath = Path.Combine(runDirectory, $"proofread-{proofreaderModel}.md");
        var reportText = BuildReport(runId, proofreaderModel, results, usage);
        File.WriteAllText(reportPath, reportText);

        Console.WriteLine();
        Console.WriteLine($"Proofreading report written to: {reportPath}");
    }

    private static ProofreadResult ParseResponse(int week, string model, string text, TimeSpan elapsed)
    {
        try
        {
            var json = ExtractJson(text);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool pass = root.TryGetProperty("Pass", out var p) && p.GetBoolean();
            var defects = new List<string>();
            if (root.TryGetProperty("Defects", out var dArr) && dArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in dArr.EnumerateArray())
                {
                    var val = el.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) defects.Add(val);
                }
            }
            string summary = root.TryGetProperty("Summary", out var s) ? s.GetString() ?? "" : "";
            return new ProofreadResult(week, model, pass, defects, summary, elapsed);
        }
        catch (Exception ex)
        {
            return new ProofreadResult(
                week,
                model,
                false,
                [$"Failed to parse proofreader JSON output: {ex.Message}"],
                text,
                elapsed);
        }
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start)
            return text;
        return text[start..(end + 1)];
    }

    private static string BuildReport(
        string runId,
        string model,
        IReadOnlyList<ProofreadResult> results,
        CopilotUsageLedger usage)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Proofreading Spike Report — {model}");
        sb.AppendLine();
        sb.AppendLine($"Target run: **{runId}**");
        sb.AppendLine($"Proofreader Model: **{model}**");
        sb.AppendLine();
        sb.AppendLine("| Week | Status | Defects Found | Elapsed (s) | Summary |");
        sb.AppendLine("|---:|---|---:|---:|---|");
        foreach (var r in results)
        {
            sb.AppendLine($"| {r.Week} | {(r.Pass ? "PASS" : "FAIL")} | {r.Defects.Count} | {r.Elapsed.TotalSeconds:F1} | {r.Summary} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Flagged Defects Detail");
        foreach (var r in results)
        {
            sb.AppendLine($"### Week {r.Week}");
            if (r.Defects.Count == 0)
            {
                sb.AppendLine("_No factual defects flagged by proofreader._");
            }
            else
            {
                foreach (var d in r.Defects)
                    sb.AppendLine($"- {d}");
            }
            sb.AppendLine();
        }

        var summary = CopilotUsageSummary.Create(usage.Records);
        sb.AppendLine("## Usage Telemetry");
        sb.AppendLine($"Total calls: **{summary.ModelCalls}** | Input tokens: **{summary.InputTokens:N0}** | Output tokens: **{summary.OutputTokens:N0}** | Total AIU: **{summary.TotalAiu:F4}**");
        return sb.ToString();
    }
}
