using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Wraps the SeasonAgent's prose body in deterministic chrome:
/// title + champion banner + final standings table + agent prose +
/// embedded charts + best-moments appendix. Also writes the manifest.
/// </summary>
internal static class SeasonComposer
{
    public static string Compose(
        SeasonAggregate agg,
        SeasonAwards awards,
        IReadOnlyList<WeeklyRecapDigest> weeklyDigests,
        string agentBody)
    {
        if (agg.Outcome is null)
            throw new InvalidOperationException("SeasonComposer requires a resolved SeasonOutcome.");

        var sb = new StringBuilder();
        sb.AppendLine($"# {agg.Season} {agg.LeagueName} — Season in Review");
        sb.AppendLine();
        sb.AppendLine($"_Generated {agg.GeneratedAt:yyyy-MM-dd HH:mm} UTC. League ID `{agg.LeagueId}`._");
        sb.AppendLine();

        // Champion banner (lifted from RecapAgent.Compose to keep the W17 finale and the season recap visually consistent).
        var so = agg.Outcome;
        sb.AppendLine($"## 🏆 {so.Champion.OwnerRealName}'s {so.Champion.TeamName} — {agg.Season} {agg.LeagueName} Champion");
        sb.AppendLine();
        sb.AppendLine($"_Defeated {so.RunnerUp.OwnerRealName}'s {so.RunnerUp.TeamName} in the championship. {so.ConsolationFifth.OwnerRealName}'s {so.ConsolationFifth.TeamName} won the consolation bowl and the 1.01 next year. {so.ConsolationLast.OwnerRealName}'s {so.ConsolationLast.TeamName} finishes last and forfeits a keeper next season (3 of 4 instead of 4)._");
        sb.AppendLine();

        // Charts go BEFORE the agent prose so when the prose says "the trajectory chart"
        // the reader has already seen it. Each chart's data-source is a JSON-pointer-ish
        // comment inside the SVG so a future agent can re-derive it from season-aggregate.json.
        sb.AppendLine("## Charts");
        sb.AppendLine();
        sb.AppendLine($"![Power-rank trajectory](charts/power-rank-trajectory.svg)");
        sb.AppendLine();
        sb.AppendLine($"![Cumulative points differential (PF − PA)](charts/cumulative-points-differential.svg)");
        sb.AppendLine();
        sb.AppendLine($"![Cumulative wins minus losses](charts/cumulative-record.svg)");
        sb.AppendLine();
        sb.AppendLine($"![Weekly score rank](charts/weekly-score-rank.svg)");
        sb.AppendLine();

        // Agent prose body (Champion crowned → Looking ahead). The agent does NOT emit the banner above
        // or the final-standings table below — only the inner sections.
        if (!string.IsNullOrWhiteSpace(agentBody))
        {
            sb.AppendLine(agentBody.Trim());
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Champion crowned");
            sb.AppendLine();
            sb.AppendLine($"_(Foundry agent unavailable — see `season-aggregate.json` and `season-awards.json` for the full machine-readable record.)_");
            sb.AppendLine();
        }

        // Final standings table — split regular-season W-L-T from playoff-inclusive totals so
        // the columns are unambiguous to future agents reading this as a structured input.
        sb.AppendLine("## Final standings");
        sb.AppendLine();
        sb.AppendLine("| Final | Team | Owner | Reg-season W-L-T | Reg-season PF | Total W-L-T (incl. playoffs) | Total PF | Path | Next year's pick |");
        sb.AppendLine("|---:|---|---|:---:|---:|:---:|---:|---|:---:|");
        var places = new[] { so.Champion, so.RunnerUp, so.ThirdPlace, so.FourthPlace, so.ConsolationFifth, so.ConsolationSixth, so.ConsolationSeventh, so.ConsolationLast };
        // Owner-id lookup so we can pull both Reg-season and Total fields from agg.Teams.
        // (The placement objects in SeasonOutcome historically conflated reg-season and total
        // record; agg.Teams.RegularSeason* is the corrected source built from W1..W15 only.)
        var teamByUser = agg.Teams.ToDictionary(t => t.UserId, t => t);
        foreach (var p in places)
        {
            string medal = p.FinalPlace switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => p.FinalPlace.ToString() };
            string pickCell = p.FinalPlace == 8 ? $"**1.0{p.DraftPick}** (forfeits a keeper)" : $"**1.0{p.DraftPick}**";
            string regRecord = $"{p.RegularSeasonWins}-{p.RegularSeasonLosses}-{p.RegularSeasonTies}";
            string regPf = p.RegularSeasonPointsFor.ToString("F2");
            string totalRecord = "—";
            string totalPf = "—";
            if (teamByUser.TryGetValue(p.UserId, out var t))
            {
                regRecord = $"{t.RegularSeasonWins}-{t.RegularSeasonLosses}-{t.RegularSeasonTies}";
                regPf = t.RegularSeasonPointsFor.ToString("F2");
                totalRecord = $"{t.TotalWins}-{t.TotalLosses}-{t.TotalTies}";
                totalPf = t.TotalPointsFor.ToString("F2");
            }
            sb.AppendLine($"| {medal} | {p.TeamName} | {p.OwnerRealName} | {regRecord} | {regPf} | {totalRecord} | {totalPf} | {p.Path} | {pickCell} |");
        }
        sb.AppendLine();
        sb.AppendLine("_Next year's draft order: champion picks last (1.08); consolation-bowl winner picks first (1.01); last place forfeits one keeper (3 of 4 instead of 4)._");
        sb.AppendLine();

        // Awards table — the deterministic source of truth. The agent narrates these in its `Awards`
        // section; this table is the canonical machine-readable echo for casual readers.
        sb.AppendLine("## Awards (canonical)");
        sb.AppendLine();
        sb.AppendLine("| Award | Winner | Metric | Value | Citation |");
        sb.AppendLine("|---|---|---|---:|---|");
        foreach (var a in awards.Awards)
        {
            var who = a.OwnerRealName is null ? a.TeamName : $"{a.OwnerRealName} — {a.TeamName}";
            sb.AppendLine($"| **{a.Name}** | {who} | {a.Metric} | {a.Value:F2} | {a.Citation} |");
        }
        sb.AppendLine();

        // Best moments appendix — one bullet per played week.
        sb.AppendLine("## Best moments — week by week");
        sb.AppendLine();
        foreach (var d in weeklyDigests.OrderBy(d => d.Week))
        {
            string headline = string.IsNullOrWhiteSpace(d.Headline) ? d.Scoreboard : d.Headline!;
            sb.AppendLine($"- **Week {d.Week}** — {headline}");
        }
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"_Machine-readable companions: `season-aggregate.json`, `season-awards.json`, `season-outcome.json`, `manifest.json`. Each chart in `charts/` carries a `data-source:` comment naming the JSON field it was derived from, so a future agent can re-render them deterministically._");

        return sb.ToString();
    }

    /// <summary>
    /// Lifts a per-week digest from each <c>recaps/{season}/week-NN.md</c> on disk:
    /// the first `**Bolded.**` lede line of the first game story, plus a one-line
    /// scoreboard built by joining all the `### ...` H3s in the file.
    /// </summary>
    public static List<WeeklyRecapDigest> LoadWeeklyDigests(int season, int totalWeeks)
    {
        var list = new List<WeeklyRecapDigest>();
        for (int w = 1; w <= totalWeeks; w++)
        {
            var path = RecapPaths.RecapFile(season, w);
            if (!File.Exists(path)) continue;
            var lines = File.ReadAllLines(path);

            // First H3 game header, plus all of them concatenated as the scoreboard.
            var h3s = lines.Where(l => l.StartsWith("### ")).Select(l => l.Substring(4).Trim()).ToList();
            string scoreboard = h3s.Count == 0 ? $"Week {w} recap on file" : string.Join(" • ", h3s.Select(StripItalicTail));

            // Lede headline: first `**Bolded.**` line in the file. Falls back to the first H3.
            string? headline = null;
            var ledeRegex = new Regex(@"^\*\*([^*]+)\*\*", RegexOptions.Compiled);
            foreach (var line in lines)
            {
                var m = ledeRegex.Match(line.TrimStart());
                if (m.Success && m.Index == 0)
                {
                    var rest = line.TrimStart();
                    headline = rest.Length > 280 ? rest.Substring(0, 280).TrimEnd() + "..." : rest;
                    break;
                }
            }
            list.Add(new WeeklyRecapDigest(w, headline, scoreboard));
        }
        return list;
    }

    private static string StripItalicTail(string h3)
    {
        // Trim trailing " — _Championship_" style hooks for compactness in the scoreboard line.
        var idx = h3.IndexOf(" — _", StringComparison.Ordinal);
        return idx < 0 ? h3 : h3.Substring(0, idx);
    }

    /// <summary>
    /// Writes the season manifest — a single JSON file indexing every machine-readable
    /// artifact the agent produced this season. Future preview agents read this file
    /// instead of scraping prose.
    /// </summary>
    public static SeasonManifest BuildAndWriteManifest(SeasonAggregate agg)
    {
        int season = agg.Season;
        var seasonDir = Path.GetDirectoryName(RecapPaths.SeasonRecap(season))!;

        string Rel(string absPath) => Path.GetRelativePath(seasonDir, absPath).Replace('\\', '/');

        var artifacts = new List<ManifestArtifact>
        {
            new(Rel(RecapPaths.SeasonRecap(season)),         "season-recap-markdown",  "text/markdown",   "Composed season-in-review document (humans + agents)."),
            new(Rel(RecapPaths.SeasonAggregateJson(season)), "season-aggregate",       "application/json", "Per-team weekly series, season highs/lows, weekly champions, final outcome. Source of truth for every chart."),
            new(Rel(RecapPaths.SeasonAwardsJson(season)),    "season-awards",          "application/json", "Deterministic awards (name, winner, metric, citation). The agent narrates these; it does not pick them."),
            new(Rel(RecapPaths.SeasonOutcomeJson(season)),   "season-outcome",         "application/json", "Final placements + next year's draft picks."),
            new(Rel(RecapPaths.PowerHistory(season)),        "power-history",          "application/json", "Per-week power-rank snapshots written by the weekly recap pipeline."),
            new(Rel(RecapPaths.TeamNameHistory(season)),     "team-name-history",      "application/json", "Owner team-name changes by week."),
            new(Rel(RecapPaths.SeasonChartFile(season, "power-rank-trajectory")),         "chart-svg", "image/svg+xml", "Power-rank trajectory chart. Derived from season-aggregate.json#Teams[].Weekly[].PowerRank."),
            new(Rel(RecapPaths.SeasonChartFile(season, "cumulative-points-differential")), "chart-svg", "image/svg+xml", "Cumulative PF − PA differential chart (regular season). Derived from season-aggregate.json#Teams[].Weekly[].CumulativePointsDifferential."),
            new(Rel(RecapPaths.SeasonChartFile(season, "cumulative-record")),              "chart-svg", "image/svg+xml", "Cumulative W-L chart. Derived from season-aggregate.json#Teams[].Weekly[].(CumulativeWins-CumulativeLosses)."),
            new(Rel(RecapPaths.SeasonChartFile(season, "weekly-score-rank")),              "chart-svg", "image/svg+xml", "Weekly score-rank chart. Derived from season-aggregate.json#Teams[].Weekly[].ScoreRank.")
        };

        // Index every weekly recap that exists on disk.
        for (int w = 1; w <= agg.Schedule.ChampionshipWeek; w++)
        {
            var weeklyPath = RecapPaths.RecapFile(season, w);
            if (File.Exists(weeklyPath))
                artifacts.Add(new(Rel(weeklyPath), "weekly-recap-markdown", "text/markdown", $"Week {w} recap."));
        }

        var manifest = new SeasonManifest(
            Season: season,
            LeagueId: agg.LeagueId,
            LeagueName: agg.LeagueName,
            GeneratedAt: DateTimeOffset.UtcNow,
            Artifacts: artifacts);

        var manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOpts);
        File.WriteAllText(RecapPaths.SeasonManifestJson(season), manifestJson);
        return manifest;
    }

    private static readonly JsonSerializerOptions ManifestJsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
