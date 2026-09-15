using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sleeper.Api;
using Sleeper.Api.NflData;
using Sleeper.Api.Services;
using Sleeper.RosterReport.Agents;
using Sleeper.RosterReport.Recap;

namespace Sleeper.RosterReport.Copilot;

internal sealed class CopilotRecapReplayRunner(
    ISleeperClient sleeper,
    ISleeperService sleeperService,
    INflDataClient nflData,
    CopilotAgentSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private const string EvaluatorInstructions =
        "You are an impartial senior editor evaluating two fantasy-football recaps. " +
        "Candidate labels are randomized. Judge only the supplied evidence and texts. " +
        "Never infer which system wrote either candidate. Penalize invented facts heavily. " +
        "Return only valid JSON matching the requested schema, without markdown fences.";

    public async Task<string> RunAsync(
        string leagueId,
        int season,
        int startWeek,
        int endWeek,
        string? requestedRunId,
        CancellationToken cancellationToken = default)
    {
        if (startWeek < 1 || endWeek < startWeek || endWeek > 17)
            throw new ArgumentOutOfRangeException(nameof(startWeek), "Replay weeks must form a range within 1-17.");

        var runId = string.IsNullOrWhiteSpace(requestedRunId)
            ? DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
            : SanitizeRunId(requestedRunId);
        var runDirectory = Path.Combine(RecapPaths.WorkspaceRoot, "recap-runs", season.ToString(), runId);
        if (Directory.Exists(runDirectory))
            throw new InvalidOperationException($"Replay run '{runId}' already exists. Choose a new --run-id.");

        var copilotDirectory = Path.Combine(runDirectory, "copilot");
        var contextDirectory = Path.Combine(runDirectory, "context");
        var inputsDirectory = Path.Combine(runDirectory, "inputs");
        var evaluationsDirectory = Path.Combine(runDirectory, "evaluations");
        var usageDirectory = Path.Combine(runDirectory, "usage");
        Directory.CreateDirectory(copilotDirectory);
        Directory.CreateDirectory(contextDirectory);
        Directory.CreateDirectory(inputsDirectory);
        Directory.CreateDirectory(evaluationsDirectory);

        var canonicalDirectory = Path.Combine(RecapPaths.WorkspaceRoot, "recaps", season.ToString());
        var beforeHashes = HashDirectory(canonicalDirectory);
        SeedReplayContext(canonicalDirectory, contextDirectory, startWeek);
        var usage = new CopilotUsageLedger();
        var manifest = new CopilotReplayManifest(
            runId,
            leagueId,
            season,
            startWeek,
            endWeek,
            settings.WriterModel,
            settings.EvaluatorModel,
            settings.ReasoningEffort,
            typeof(GitHub.Copilot.CopilotClient).Assembly.GetName().Version?.ToString() ?? "unknown",
            GetGitCommit(),
            DateTimeOffset.UtcNow,
            null,
            "running",
            []);
        WriteJson(Path.Combine(runDirectory, "manifest.json"), manifest);

        try
        {
            await using var host = await CopilotAgentHost.StartAsync(
                settings,
                usage,
                cancellationToken).ConfigureAwait(false);
            var proofreader = host.CreateProofreader(
                "You are a strict, fast factual proofreader for a fantasy football recap newsletter. " +
                "Compare the supplied draft markdown recap against the game data and COMPUTED MATCHUP FACTS. " +
                "Check for: incorrect scores, wrong winner/loser, wrong owner or team names, mathematical mistakes, " +
                "roster misattributions, and ungrounded claims. " +
                "Return ONLY valid JSON matching: {\"Pass\": true/false, \"Defects\": [\"...\"]}. " +
                "No markdown fences or commentary outside the JSON.",
                "gpt-5-mini");

            var recapAgent = RecapAgent.Create(
                host.CreateWriter(FoundryAgentInstructions.WeeklyGameAnalyst),
                host.CreateWriter(FoundryAgentInstructions.WeeklyLeagueAnalyst),
                settings.WriterModel,
                proofreader);
            var evaluator = host.CreateEvaluator(EvaluatorInstructions);

            using (RecapPaths.UseRecapDirectory(contextDirectory))
            {
                for (var week = startWeek; week <= endWeek; week++)
                {
                    Console.WriteLine($"Replaying {season} week {week} with Copilot...");
                    var lore = LeagueLore.TryLoadLayers(
                        RecapPaths.LegacyLorePath,
                        RecapPaths.LoreDirectory,
                        season,
                        week) ?? LeagueLore.ParseFrom("");
                    var builder = new RecapEnvelopeBuilder(sleeper, sleeperService, nflData, lore);
                    var envelope = await builder.BuildAsync(
                        leagueId,
                        week,
                        season,
                        cancellationToken,
                        new RecapEnvelopeBuildOptions(PersistSnapshots: true)).ConfigureAwait(false);

                    WriteJson(
                        Path.Combine(inputsDirectory, $"week-{week:D2}-envelope.json"),
                        envelope);

                    var recap = await recapAgent.WriteRecapAsync(
                        envelope,
                        settings.MaxConcurrency,
                        cancellationToken).ConfigureAwait(false);
                    var contextRecapPath = RecapPaths.RecapFile(season, week);
                    var recapPath = Path.Combine(copilotDirectory, $"week-{week:D2}.md");
                    File.WriteAllText(contextRecapPath, recap);
                    File.WriteAllText(recapPath, recap);

                    var baselinePath = Path.Combine(canonicalDirectory, $"week-{week:D2}.md");
                    if (!File.Exists(baselinePath))
                        throw new FileNotFoundException($"Foundry baseline not found for week {week}.", baselinePath);

                    var evaluation = await EvaluateAsync(
                        evaluator,
                        envelope,
                        File.ReadAllText(baselinePath),
                        recap,
                        week,
                        cancellationToken).ConfigureAwait(false);
                    WriteJson(
                        Path.Combine(evaluationsDirectory, $"week-{week:D2}.json"),
                        evaluation);

                    usage.Write(usageDirectory);
                    manifest.Weeks.Add(new CopilotReplayWeek(
                        week,
                        Path.GetRelativePath(runDirectory, recapPath),
                        $"inputs/week-{week:D2}-envelope.json",
                        $"evaluations/week-{week:D2}.json",
                        "completed"));
                    WriteJson(Path.Combine(runDirectory, "manifest.json"), manifest);
                }
            }

            var evaluations = Enumerable.Range(startWeek, endWeek - startWeek + 1)
                .Select(week => JsonSerializer.Deserialize<BlindEvaluationResult>(
                    File.ReadAllText(Path.Combine(evaluationsDirectory, $"week-{week:D2}.json")),
                    JsonOptions)!)
                .ToList();
            File.WriteAllText(
                Path.Combine(runDirectory, "evaluation-summary.md"),
                BuildEvaluationSummary(evaluations));
            usage.Write(usageDirectory);

            var afterHashes = HashDirectory(canonicalDirectory);
            if (!beforeHashes.OrderBy(pair => pair.Key).SequenceEqual(afterHashes.OrderBy(pair => pair.Key)))
                throw new InvalidOperationException($"Canonical recap directory changed during replay: {canonicalDirectory}");

            manifest = manifest with
            {
                CompletedAt = DateTimeOffset.UtcNow,
                Status = "completed",
                CanonicalArtifactsVerifiedUnchanged = true
            };
            WriteJson(Path.Combine(runDirectory, "manifest.json"), manifest);
            return runDirectory;
        }
        catch
        {
            usage.Write(usageDirectory);
            manifest = manifest with
            {
                CompletedAt = DateTimeOffset.UtcNow,
                Status = "failed"
            };
            WriteJson(Path.Combine(runDirectory, "manifest.json"), manifest);
            throw;
        }
    }

    private static async Task<BlindEvaluationResult> EvaluateAsync(
        IReportTextAgent evaluator,
        RecapEnvelope envelope,
        string foundryRecap,
        string copilotRecap,
        int week,
        CancellationToken cancellationToken)
    {
        var copilotIsA = RandomNumberGenerator.GetInt32(2) == 0;
        var candidateA = copilotIsA ? copilotRecap : foundryRecap;
        var candidateB = copilotIsA ? foundryRecap : copilotRecap;
        var prompt = BuildEvaluationPrompt(envelope, candidateA, candidateB);
        string? lastRaw = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            lastRaw = await evaluator.GenerateAsync(
                prompt,
                new AgentCallContext(week, "evaluation", $"attempt-{attempt}"),
                cancellationToken).ConfigureAwait(false);
            try
            {
                var parsed = JsonSerializer.Deserialize<QualityEvaluation>(
                    ExtractJson(lastRaw),
                    JsonOptions) ?? throw new JsonException("Evaluator returned null.");
                var deterministic = new
                {
                    CandidateA = InspectCandidate(candidateA, envelope),
                    CandidateB = InspectCandidate(candidateB, envelope)
                };
                return new BlindEvaluationResult(
                    week,
                    copilotIsA ? "Copilot" : "Foundry",
                    copilotIsA ? "Foundry" : "Copilot",
                    parsed,
                    deterministic);
            }
            catch (JsonException) when (attempt < 2)
            {
                prompt += "\n\nYour previous response was not valid JSON. Return only the JSON object.";
            }
        }

        throw new InvalidOperationException($"Evaluator did not return valid JSON. Last response: {lastRaw}");
    }

    private static string BuildEvaluationPrompt(
        RecapEnvelope envelope,
        string candidateA,
        string candidateB)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Evaluate two Week {envelope.Meta.Week} recaps for {envelope.Meta.LeagueName}.");
        sb.AppendLine("The evidence JSON is authoritative. A confident but unsupported statement is a factual defect.");
        sb.AppendLine("Score each candidate from 1-10 on factualAccuracy, coverage, continuity, specificity, writingCraft, entertainment, and houseStyle.");
        sb.AppendLine("Return exactly this JSON shape:");
        sb.AppendLine("""
            {
              "candidateA": {
                "factualAccuracy": 0, "coverage": 0, "continuity": 0,
                "specificity": 0, "writingCraft": 0, "entertainment": 0, "houseStyle": 0,
                "overall": 0, "strengths": ["..."], "defects": ["..."]
              },
              "candidateB": {
                "factualAccuracy": 0, "coverage": 0, "continuity": 0,
                "specificity": 0, "writingCraft": 0, "entertainment": 0, "houseStyle": 0,
                "overall": 0, "strengths": ["..."], "defects": ["..."]
              },
              "preferredCandidate": "A",
              "confidence": 0,
              "reason": "..."
            }
            """);
        sb.AppendLine("preferredCandidate must be A, B, or Tie. confidence is 1-10.");
        sb.AppendLine();
        sb.AppendLine("## Authoritative evidence");
        sb.AppendLine(JsonSerializer.Serialize(envelope, JsonOptions));
        sb.AppendLine();
        sb.AppendLine("## Candidate A");
        sb.AppendLine(candidateA);
        sb.AppendLine();
        sb.AppendLine("## Candidate B");
        sb.AppendLine(candidateB);
        return sb.ToString();
    }

    private static CandidateInspection InspectCandidate(string recap, RecapEnvelope envelope)
    {
        var missingTeams = envelope.Owners
            .Where(owner => !recap.Contains(owner.TeamName, StringComparison.OrdinalIgnoreCase))
            .Select(owner => owner.TeamName)
            .ToList();
        var missingScores = envelope.Games
            .SelectMany(game => new[] { game.Home.FinalScore, game.Away.FinalScore })
            .Select(score => score.ToString("F2"))
            .Distinct()
            .Where(score => !recap.Contains(score, StringComparison.Ordinal))
            .ToList();
        var leakedUsernames = envelope.Owners
            .Where(owner => !string.IsNullOrWhiteSpace(owner.Username))
            .Where(owner => recap.Contains(owner.Username, StringComparison.OrdinalIgnoreCase))
            .Select(owner => owner.Username)
            .ToList();
        var gameHeadings = recap.Split('\n')
            .Count(line => line.StartsWith("### ", StringComparison.Ordinal));

        return new CandidateInspection(
            missingTeams,
            missingScores,
            leakedUsernames,
            gameHeadings,
            envelope.Games.Count);
    }

    private static string BuildEvaluationSummary(IReadOnlyList<BlindEvaluationResult> results)
    {
        var pairs = results.Select(result =>
        {
            var copilot = result.CandidateAProvider == "Copilot"
                ? result.Evaluation.CandidateA
                : result.Evaluation.CandidateB;
            var foundry = result.CandidateAProvider == "Foundry"
                ? result.Evaluation.CandidateA
                : result.Evaluation.CandidateB;
            var preferred = result.Evaluation.PreferredCandidate.Equals("Tie", StringComparison.OrdinalIgnoreCase)
                ? "Tie"
                : result.Evaluation.PreferredCandidate.Equals("A", StringComparison.OrdinalIgnoreCase)
                    ? result.CandidateAProvider
                    : result.CandidateBProvider;
            return (Result: result, Copilot: copilot, Foundry: foundry, Preferred: preferred);
        }).ToList();

        var copilotFactual = pairs.Average(pair => pair.Copilot.FactualAccuracy);
        var foundryFactual = pairs.Average(pair => pair.Foundry.FactualAccuracy);
        var copilotOverall = pairs.Average(pair => pair.Copilot.Overall);
        var foundryOverall = pairs.Average(pair => pair.Foundry.Overall);
        var gate = copilotFactual >= foundryFactual && copilotOverall >= foundryOverall
            ? "PASS"
            : "HOLD";

        var sb = new StringBuilder();
        sb.AppendLine("# Copilot vs Foundry blind evaluation");
        sb.AppendLine();
        sb.AppendLine($"**Migration gate: {gate}.** Copilot must match or exceed Foundry on both average factual accuracy and overall score.");
        sb.AppendLine();
        sb.AppendLine("| Week | Preferred | Confidence | Copilot overall | Foundry overall |");
        sb.AppendLine("|---:|---|---:|---:|---:|");
        foreach (var pair in pairs.OrderBy(pair => pair.Result.Week))
        {
            sb.AppendLine($"| {pair.Result.Week} | {pair.Preferred} | {pair.Result.Evaluation.Confidence:0.0} | {pair.Copilot.Overall:0.0} | {pair.Foundry.Overall:0.0} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Average scores");
        sb.AppendLine();
        sb.AppendLine("| Candidate | Factual | Coverage | Continuity | Specificity | Craft | Entertainment | House style | Overall | Defects |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        AppendCandidateAverage(sb, "Copilot", pairs.Select(pair => pair.Copilot).ToList());
        AppendCandidateAverage(sb, "Foundry", pairs.Select(pair => pair.Foundry).ToList());
        sb.AppendLine();
        sb.AppendLine($"Copilot preferences: **{pairs.Count(pair => pair.Preferred == "Copilot")}**; Foundry preferences: **{pairs.Count(pair => pair.Preferred == "Foundry")}**; ties: **{pairs.Count(pair => pair.Preferred == "Tie")}**.");
        sb.AppendLine();
        if (gate == "HOLD")
            sb.AppendLine("The full Foundry removal is paused. Improve factual guardrails and rerun under a new run ID before migration.");
        return sb.ToString();
    }

    private static void AppendCandidateAverage(
        StringBuilder sb,
        string name,
        IReadOnlyList<QualityCandidate> candidates)
    {
        sb.AppendLine(
            $"| {name} | {candidates.Average(c => c.FactualAccuracy):0.0} | " +
            $"{candidates.Average(c => c.Coverage):0.0} | " +
            $"{candidates.Average(c => c.Continuity):0.0} | " +
            $"{candidates.Average(c => c.Specificity):0.0} | " +
            $"{candidates.Average(c => c.WritingCraft):0.0} | " +
            $"{candidates.Average(c => c.Entertainment):0.0} | " +
            $"{candidates.Average(c => c.HouseStyle):0.0} | " +
            $"{candidates.Average(c => c.Overall):0.0} | " +
            $"{candidates.Sum(c => c.Defects.Count)} |");
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new JsonException("No JSON object found.");
        return text[start..(end + 1)];
    }

    private static Dictionary<string, string> HashDirectory(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(directory, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal static void SeedReplayContext(
        string canonicalDirectory,
        string contextDirectory,
        int startWeek)
    {
        if (!Directory.Exists(canonicalDirectory))
            return;

        Directory.CreateDirectory(contextDirectory);
        foreach (var fileName in new[] { "power-history.json", "team-name-history.json" })
        {
            var source = Path.Combine(canonicalDirectory, fileName);
            if (File.Exists(source))
                CopyHistoryBeforeWeek(source, Path.Combine(contextDirectory, fileName), startWeek);
        }

        for (var week = 1; week < startWeek; week++)
        {
            var fileName = $"week-{week:D2}.md";
            var source = Path.Combine(canonicalDirectory, fileName);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(contextDirectory, fileName));
        }
    }

    private static void CopyHistoryBeforeWeek(string source, string destination, int startWeek)
    {
        var document = JsonNode.Parse(File.ReadAllText(source))?.AsObject()
            ?? throw new JsonException($"History file is empty: {source}");
        var snapshots = document["Snapshots"]?.AsArray()
            ?? throw new JsonException($"History file has no Snapshots array: {source}");

        for (var index = snapshots.Count - 1; index >= 0; index--)
        {
            var week = snapshots[index]?["Week"]?.GetValue<int>()
                ?? throw new JsonException($"History snapshot has no Week value: {source}");
            if (week >= startWeek)
                snapshots.RemoveAt(index);
        }

        File.WriteAllText(destination, document.ToJsonString(JsonOptions));
    }

    private static string SanitizeRunId(string runId)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(runId.Where(character => !invalid.Contains(character)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new ArgumentException("Run ID contains no valid filename characters.", nameof(runId));
        return cleaned;
    }

    private static string? GetGitCommit()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = RecapPaths.WorkspaceRoot,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }
}

internal sealed record CopilotReplayManifest(
    string RunId,
    string LeagueId,
    int Season,
    int StartWeek,
    int EndWeek,
    string WriterModel,
    string EvaluatorModel,
    string? ReasoningEffort,
    string SdkVersion,
    string? GitCommit,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    List<CopilotReplayWeek> Weeks,
    bool? CanonicalArtifactsVerifiedUnchanged = null);

internal sealed record CopilotReplayWeek(
    int Week,
    string RecapPath,
    string EnvelopePath,
    string EvaluationPath,
    string Status);

internal sealed record BlindEvaluationResult(
    int Week,
    string CandidateAProvider,
    string CandidateBProvider,
    QualityEvaluation Evaluation,
    object DeterministicChecks);

internal sealed record QualityEvaluation(
    QualityCandidate CandidateA,
    QualityCandidate CandidateB,
    string PreferredCandidate,
    decimal Confidence,
    string Reason);

internal sealed record QualityCandidate(
    decimal FactualAccuracy,
    decimal Coverage,
    decimal Continuity,
    decimal Specificity,
    decimal WritingCraft,
    decimal Entertainment,
    decimal HouseStyle,
    decimal Overall,
    List<string> Strengths,
    List<string> Defects);

internal sealed record CandidateInspection(
    List<string> MissingTeams,
    List<string> MissingScores,
    List<string> LeakedUsernames,
    int GameHeadings,
    int ExpectedGames);
