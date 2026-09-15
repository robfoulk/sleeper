using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Sleeper.RosterReport.Agents;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Single-pass season-recap agent. Produces the prose sections of the
/// season-in-review document (Champion crowned, Season arc, Awards
/// narration, Champion's path, Loser's saga, Power-rank story, Looking
/// ahead). Composer wraps the prose in deterministic outer chrome (banner,
/// final standings table, charts, manifest).
/// </summary>
internal sealed class SeasonAgent
{
    private readonly IReportTextAgent _agent;
    private readonly string _model;

    private SeasonAgent(IReportTextAgent agent, string model)
    {
        _agent = agent;
        _model = model;
    }

    /// <summary>
    /// Wires the season agent to any text backend (Copilot or Foundry).
    /// </summary>
    internal static SeasonAgent Create(IReportTextAgent agent, string model)
        => new(agent, model);

    public static async Task<SeasonAgent?> TryCreateAsync(FoundryAgentSettings settings, CancellationToken ct = default)
    {
        var runtime = await FoundryAgentFactory.TryCreateAsync(
            settings,
            FoundryAgentRole.SeasonAnalyst,
            FoundryAgentInstructions.SeasonAnalyst,
            enableWebSearch: false,
            ct).ConfigureAwait(false);

        if (runtime is null)
            return null;

        Console.WriteLine($"  (Season Agent online: Foundry agent '{runtime.AgentName}', model '{runtime.ModelDeployment}')");
        return new SeasonAgent(new FoundryReportTextAgent(runtime.Agent), runtime.ModelDeployment);
    }

    /// <summary>
    /// Runs the season-in-review prompt. Returns the model's markdown body.
    /// The composer wraps this with the champion banner + final-standings
    /// table + chart embeds, so the agent should NOT emit those itself.
    /// </summary>
    public async Task<string> WriteAsync(
        SeasonAggregate agg,
        SeasonAwards awards,
        IReadOnlyList<WeeklyRecapDigest> weeklyDigests,
        CancellationToken ct = default)
    {
        if (agg.Outcome is null)
            throw new InvalidOperationException("SeasonAgent requires a resolved SeasonOutcome (the season must be finished).");

        var sb = new StringBuilder();
        var so = agg.Outcome;

        sb.AppendLine($"You are writing the prose body of the **{agg.Season} {agg.LeagueName}** season-in-review document. The trophy banner, final-standings table, charts, and award table are emitted by a deterministic composer — your job is the column.");
        sb.AppendLine();
        sb.AppendLine("## Owner directory (use these real names or team names ONLY; never use the internal usernames below)");
        var forbidden = new List<string>();
        foreach (var o in agg.Owners.OrderBy(o => o.RosterId))
        {
            sb.AppendLine($"- **{o.RealName ?? o.DisplayName}** — team \"{o.TeamName}\"");
            if (!string.IsNullOrWhiteSpace(o.Username)) forbidden.Add(o.Username);
        }
        sb.AppendLine();
        sb.AppendLine($"FORBIDDEN tokens (must not appear anywhere in your output): {string.Join(", ", forbidden.Select(u => "`" + u + "`"))}.");
        sb.AppendLine();
        sb.AppendLine("## NAMES AND RIVALRY RULE");
        sb.AppendLine("Refer to owners by first name only. NEVER write a surname or a last initial, and never attach a family name to the league itself.");
        sb.AppendLine("NEVER frame anything around a personal relationship between owners — no father/son, brother, cousin, nephew, sibling, or generational framing. Rivalries in this league come from results only: head-to-head history, playoff eliminations, title rematches, standings stakes, and win streaks.");
        sb.AppendLine();

        // Pre-computed analytical hooks. The agent CONSISTENTLY hallucinates power-rank
        // movers when asked to read them off the chart. Hand them the deltas explicitly.
        var movers = ComputeMovers(agg);
        sb.AppendLine("## Power-rank movers (PRE-COMPUTED — use these exact numbers, do NOT guess from charts)");
        sb.AppendLine($"- **Biggest climbers (W1 → W{agg.Schedule.RegularSeasonLastWeek}, lower rank = better):**");
        foreach (var (team, w1, wEnd, delta) in movers.Climbers)
            sb.AppendLine($"  - {team.OwnerRealName}'s {team.FinalTeamName}: #{w1} → #{wEnd} (climbed {delta} spot{(delta == 1 ? "" : "s")})");
        sb.AppendLine($"- **Biggest fallers (W1 → W{agg.Schedule.RegularSeasonLastWeek}):**");
        foreach (var (team, w1, wEnd, delta) in movers.Fallers)
            sb.AppendLine($"  - {team.OwnerRealName}'s {team.FinalTeamName}: #{w1} → #{wEnd} (fell {delta} spot{(delta == 1 ? "" : "s")})");
        sb.AppendLine($"- **Held #1 power rank (weeks):** {string.Join(", ", movers.Top1Weeks.Select(t => $"{t.Team.FinalTeamName} ({t.Weeks})"))}");
        sb.AppendLine();
        sb.AppendLine("## Final cumulative points differential (regular season, PF − PA, sorted)");
        foreach (var (team, diff) in movers.DifferentialLeaderboard)
            sb.AppendLine($"- {team.OwnerRealName}'s {team.FinalTeamName}: {diff:+0.00;-0.00;0.00}");
        sb.AppendLine();
        sb.AppendLine("## Season outcome (final placements + next year's draft order)");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(so, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Season aggregate (per-team weekly series, season highs/lows, weekly score-leaders)");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(agg, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Deterministic awards (the composer emits the full table; you NARRATE these — do NOT pick different winners)");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(awards, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Weekly digests (one entry per played week — first-paragraph headline + scoreboard)");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(weeklyDigests, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Output format (MANDATORY)");
        sb.AppendLine("Output exactly these sections, each with a `## ` heading, in this order:");
        sb.AppendLine($"1. `## Champion crowned` — 1-2 paragraphs. The trophy lifts. Crown **{so.Champion.OwnerRealName}'s {so.Champion.TeamName}** explicitly. Reference the championship score line ({so.Champion.RegularSeasonPointsFor:F2} regular-season PF as context). Note the path through the bracket. The deterministic banner above the section will already announce the title — your job is to give it weight, not just restate it.");
        sb.AppendLine("2. `## Season arc` — 3-4 paragraphs. Zoom out. The shape of the year: who emerged, who collapsed, who held steady. You MUST reference at least two of the pre-computed power-rank movers above by name and direction (climber or faller). Reference at least one specific weekly-champion entry. Lean on the weekly digests for callbacks. NO bullet lists in this section.");
        sb.AppendLine($"3. `## Champion's path` — 2-3 paragraphs. The bracket walk: how **{so.Champion.TeamName}** got there. Reference the bracket-source data in the season outcome. Then 1 short paragraph on **{so.RunnerUp.OwnerRealName}'s {so.RunnerUp.TeamName}** as runner-up.");
        sb.AppendLine($"4. `## Loser's saga` — 1-2 paragraphs. **{so.ConsolationLast.OwnerRealName}'s {so.ConsolationLast.TeamName}** finishes last. Per league rules they get to keep only **3 of their 4 keepers** next year (the keeper-forfeiture penalty for the cellar). Mention this rule explicitly. Reference any losing streak from the aggregate. End with **{so.ConsolationFifth.OwnerRealName}'s {so.ConsolationFifth.TeamName}** winning the consolation final and the 1.01 — that's the silver lining of the bottom half.");
        sb.AppendLine("5. `## Power-rank story` — 2 paragraphs. Use the PRE-COMPUTED MOVERS list above as your source of truth. Name the biggest climber by exact spots, the biggest faller by exact spots, and (if any team appears in `Held #1`) the team that held the top rank longest. Refer to the trajectory chart and the cumulative-points-differential chart by name. Do NOT invent ordering or claims that contradict the pre-computed list.");
        sb.AppendLine("6. `## Looking ahead to next year` — bullet list, exactly one bullet per owner (8 bullets), ordered by the owner's NEXT-YEAR draft pick (1.01 first, 1.08 last). Each bullet uses this EXACT format: `**{Owner real name} — {Final team name} (1.0{N})** — one sentence tying last year's performance to next year's pick.` For the LAST place team (the one with `forfeitsKeeper:true` in the season outcome), append `Keeper forfeiture: keeps 3 of 4 next year.` to the bullet.");
        sb.AppendLine();
        sb.AppendLine("## Constraints");
        sb.AppendLine("- Do NOT emit a `# Title` heading. Do NOT emit a champion banner (`## 🏆 ...`). Do NOT emit a final-standings table. Do NOT emit chart markdown (the composer puts the charts above your prose). Do NOT emit a stand-alone `## Awards` section — the composer renders the canonical awards table after your prose, so your prose can simply weave award winners into the narrative.");
        sb.AppendLine("- Do NOT emit \"Best moments\" or weekly digest bullets — the composer adds an appendix.");
        sb.AppendLine("- Use only owner real names or team names. NEVER write any internal Sleeper username.");
        sb.AppendLine("- Refer to the season as \"the {season} season\" or \"this year\". This document is being read by a future agent next year, so calling it \"this season\" is fine.");

        Console.WriteLine($"  Calling SeasonAnalyst (model {_model})...");
        var response = await _agent.GenerateAsync(
            sb.ToString(),
            new AgentCallContext(null, "SeasonAnalyst", agg.Season.ToString()),
            ct).ConfigureAwait(false);
        return ScrubUsernames(response.Trim(), agg.Owners);
    }

    private static string ScrubUsernames(string text, IReadOnlyList<OwnerRef> owners)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var pairs = new List<(string Token, string Replacement)>();
        foreach (var o in owners)
        {
            var realName = o.RealName ?? o.DisplayName ?? o.TeamName;
            if (!string.IsNullOrWhiteSpace(o.Username) && !string.Equals(o.Username, realName, StringComparison.OrdinalIgnoreCase))
                pairs.Add((o.Username, realName));
            if (!string.IsNullOrWhiteSpace(o.DisplayName) && !string.Equals(o.DisplayName, realName, StringComparison.OrdinalIgnoreCase) && !string.Equals(o.DisplayName, o.Username, StringComparison.OrdinalIgnoreCase))
                pairs.Add((o.DisplayName, realName));
        }
        foreach (var (token, replacement) in pairs.OrderByDescending(p => p.Token.Length))
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"\b" + System.Text.RegularExpressions.Regex.Escape(token) + @"\b",
                replacement,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return text;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Pre-computes the analytical hooks the agent kept hallucinating in v1: top
    /// climbers/fallers by power-rank delta, who held #1 longest, and the regular-season
    /// PF − PA differential leaderboard. Hand-feeding these stops the agent from
    /// inventing storylines like "team X held #1 most of the year" when the data
    /// shows team X was middling.
    /// </summary>
    private static MoverSummary ComputeMovers(SeasonAggregate agg)
    {
        int endWeek = agg.Schedule.RegularSeasonLastWeek;
        var deltas = new List<(SeasonTeamSeries Team, int W1, int WEnd, int Delta)>();
        foreach (var t in agg.Teams)
        {
            int? w1 = t.Weekly.FirstOrDefault(e => e.Week == 1)?.PowerRank;
            int? wEnd = t.Weekly.FirstOrDefault(e => e.Week == endWeek)?.PowerRank;
            if (w1 is null || wEnd is null) continue;
            // Positive delta = climbed (rank number went DOWN: e.g. 8 → 3 = +5 climb).
            int delta = w1.Value - wEnd.Value;
            deltas.Add((t, w1.Value, wEnd.Value, delta));
        }
        var climbers = deltas.Where(d => d.Delta > 0).OrderByDescending(d => d.Delta).Take(3).ToList();
        var fallers = deltas.Where(d => d.Delta < 0).OrderBy(d => d.Delta).Take(3)
            .Select(d => (d.Team, d.W1, d.WEnd, Math.Abs(d.Delta))).ToList();

        // Who held #1 — count weeks at PowerRank == 1.
        var top1 = agg.Teams
            .Select(t => (Team: t, Weeks: t.Weekly.Count(e => e.PowerRank == 1)))
            .Where(x => x.Weeks > 0)
            .OrderByDescending(x => x.Weeks)
            .ToList();

        var diffBoard = agg.Teams
            .Select(t => (Team: t, Diff: t.Weekly
                .Where(e => e.Week <= endWeek)
                .Select(e => e.CumulativePointsDifferential)
                .DefaultIfEmpty(0m)
                .Last()))
            .OrderByDescending(x => x.Diff)
            .ToList();

        return new MoverSummary(climbers, fallers, top1, diffBoard);
    }

    private sealed record MoverSummary(
        List<(SeasonTeamSeries Team, int W1, int WEnd, int Delta)> Climbers,
        List<(SeasonTeamSeries Team, int W1, int WEnd, int Delta)> Fallers,
        List<(SeasonTeamSeries Team, int Weeks)> Top1Weeks,
        List<(SeasonTeamSeries Team, decimal Diff)> DifferentialLeaderboard);

}

/// <summary>
/// Compact per-week digest fed into the SeasonAgent prompt. Lifted by
/// SeasonComposer from each <c>recaps/{season}/week-NN.md</c>.
/// </summary>
public sealed record WeeklyRecapDigest(
    int Week,
    string? Headline,                     // first `**Bolded.**` lede line of the first game story
    string Scoreboard                     // one-line "Team A 132.4 def. Team B 118.7 | Team C ... " summary lifted from the file's H3s
);
