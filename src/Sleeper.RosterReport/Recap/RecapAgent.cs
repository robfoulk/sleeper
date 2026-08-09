using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Microsoft Agent Framework orchestration:
/// - Game analyst: one ChatClientAgent call per matchup, run with bounded
///   parallelism.
/// - League analyst: one ChatClientAgent call for intro + themes + look-ahead +
///   manufactured "columnist's lines".
/// - Researcher (web search): handled by the existing Foundry Responses-API
///   pattern; surfaced to the analysts via their prompts (the per-game and
///   league prompts include an "Agent fetch hints" section the analysts can
///   reference; a simple lookup helper here is wired separately).
/// </summary>
internal sealed class RecapAgent
{
    private readonly AIAgent _gameAgent;
    private readonly AIAgent _leagueAgent;
    private readonly string _model;

    private RecapAgent(AIAgent gameAgent, AIAgent leagueAgent, string model)
    {
        _gameAgent = gameAgent;
        _leagueAgent = leagueAgent;
        _model = model;
    }

    public static async Task<RecapAgent?> TryCreateAsync(FoundryAgentSettings settings, CancellationToken ct = default)
    {
        var gameAgent = await FoundryAgentFactory.TryCreateAsync(
            settings,
            FoundryAgentRole.WeeklyGameAnalyst,
            FoundryAgentInstructions.WeeklyGameAnalyst,
            enableWebSearch: false,
            ct).ConfigureAwait(false);
        var leagueAgent = await FoundryAgentFactory.TryCreateAsync(
            settings,
            FoundryAgentRole.WeeklyLeagueAnalyst,
            FoundryAgentInstructions.WeeklyLeagueAnalyst,
            enableWebSearch: false,
            ct).ConfigureAwait(false);

        if (gameAgent is null || leagueAgent is null)
            return null;

        Console.WriteLine($"  (Recap Agents online: game + league Foundry agents)");
        return new RecapAgent(gameAgent.Agent, leagueAgent.Agent, gameAgent.ModelDeployment);
    }

    public async Task<string> WriteRecapAsync(RecapEnvelope env, int maxConcurrency = 3, CancellationToken ct = default)
    {
        // 1. Per-game stories in parallel (bounded).
        var sortedGames = SortGamesForNarrative(env);
        var stories = new string[sortedGames.Count];

        // 1a. Load prior week's Forecast table and randomly thread some rows into
        //     this week's per-game prompts so the analyst grades their own call.
        var priorByIdx = MatchPriorForecastsToGames(env, sortedGames);

        var sem = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = new List<Task>();
        for (int i = 0; i < sortedGames.Count; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    Console.WriteLine($"  Writing game story {idx + 1}/{sortedGames.Count}: {sortedGames[idx].Home.OwnerDisplay} vs {sortedGames[idx].Away.OwnerDisplay}");
                    priorByIdx.TryGetValue(idx, out var priorCall);
                    stories[idx] = await WriteGameStoryAsync(env, sortedGames[idx], priorCall, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    (game story failed: {ex.Message})");
                    stories[idx] = $"_(game story unavailable: {ex.Message})_";
                }
                finally { sem.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks);

        // 2. Build short summaries the league analyst can callback to (real names only — no usernames).
        var summaries = sortedGames.Zip(stories, (g, s) =>
        {
            var hOwner = env.Owners.FirstOrDefault(o => o.UserId == g.Home.UserId);
            var aOwner = env.Owners.FirstOrDefault(o => o.UserId == g.Away.UserId);
            var hLabel = $"{hOwner?.RealName ?? hOwner?.DisplayName ?? g.Home.CurrentTeamName} ({g.Home.CurrentTeamName})";
            var aLabel = $"{aOwner?.RealName ?? aOwner?.DisplayName ?? g.Away.CurrentTeamName} ({g.Away.CurrentTeamName})";
            return new
            {
                Header = $"{hLabel} {g.Home.FinalScore:F2} {(g.Home.FinalScore >= g.Away.FinalScore ? "def." : "lost to")} {aLabel} {g.Away.FinalScore:F2}",
                Hook = g.StoryHookLabel,
                FirstPara = FirstParagraph(s)
            };
        }).ToList();

        // 3. League-wide call (Intro + League Themes + Look-Ahead).
        Console.WriteLine($"  Writing league-wide commentary...");
        var leaguePiece = await WriteLeaguePieceAsync(env, summaries.Cast<object>().ToList(), ct);

        // 3b. Forecast call (Forecast table + Predictions). Separate so it can focus on next-week consistency.
        string forecastPiece = "";
        if (env.LookAhead.Matchups.Count > 0)
        {
            Console.WriteLine($"  Writing next-week forecast...");
            try { forecastPiece = await WriteForecastPieceAsync(env, ct); }
            catch (Exception ex) { Console.WriteLine($"    (forecast failed: {ex.Message})"); }
        }

        // 4. Compose final markdown.
        return Compose(env, sortedGames, stories, leaguePiece, forecastPiece);
    }

    // ----------------- Game story -----------------

    private async Task<string> WriteGameStoryAsync(RecapEnvelope env, GameRecap g, PriorForecast? priorCall, CancellationToken ct)
    {
        var homeOwner = env.Owners.FirstOrDefault(o => o.UserId == g.Home.UserId);
        var awayOwner = env.Owners.FirstOrDefault(o => o.UserId == g.Away.UserId);
        var homeName = homeOwner?.RealName ?? g.Home.OwnerDisplay;
        var awayName = awayOwner?.RealName ?? g.Away.OwnerDisplay;

        var sb = new StringBuilder();
        sb.AppendLine($"You are writing a single recap of one fantasy football matchup in week {env.Meta.Week} of the {env.Meta.Season} season.");
        sb.AppendLine($"League: **{env.Meta.LeagueName}** ({env.Meta.SeasonType}{(g.PlayoffRound is null ? "" : $", {g.PlayoffRound}")}). Use \"{env.Meta.LeagueName}\" as the league name; do NOT use 'The Foulkrod League' or any other label.");
        if (!string.IsNullOrWhiteSpace(g.PlayoffRound) && env.Meta.Week >= (env.Schedule?.PlayoffStartWeek ?? 16))
        {
            // Per-game playoff role: this matters most in week 17 when the championship vs the
            // 3rd-place game vs the consolation games can blur together. Tell the analyst exactly
            // what THIS game is.
            string roleNote = g.PlayoffRound switch
            {
                "Championship" => $"**THIS GAME IS THE CHAMPIONSHIP.** The winner is the {env.Meta.Season} {env.Meta.LeagueName} champion. The loser finishes 2nd. Treat this as the headline event of the entire season — crown the winner explicitly, name the trophy, give it the weight of every week that came before.",
                "3rd-place game" => "**THIS GAME IS THE 3RD-PLACE GAME** (winners-bracket consolation). It is NOT the championship. Winner finishes 3rd (next year's pick 1.06); loser finishes 4th (pick 1.05). Do NOT call the winner of this game the league champion.",
                "Consolation final" => "**THIS GAME IS THE CONSOLATION FINAL.** The winner gets next year's **1.01** (first overall pick). The loser gets pick 1.02. Frame the winner as the consolation champion picking up a real prize — not a participation trophy.",
                "7th-place game" => "**THIS GAME IS THE 7TH-PLACE GAME.** Winner finishes 7th (pick 1.03); loser finishes LAST for the season and **forfeits one keeper next year (3 of 4 instead of 4)**. The loser's penalty is the dominant stake here.",
                "Semifinal" => "**THIS GAME IS A WINNERS-BRACKET SEMIFINAL.** Winner advances to the championship next week; loser drops to the 3rd-place game.",
                "Consolation semifinal" => "**THIS GAME IS A CONSOLATION SEMIFINAL.** Winner advances to the consolation final (and a chance at the 1.01); loser drops to the 7th-place game (and risks the keeper-forfeit penalty).",
                _ => ""
            };
            if (!string.IsNullOrEmpty(roleNote))
            {
                sb.AppendLine();
                sb.AppendLine($"## Bracket role for this specific game");
                sb.AppendLine(roleNote);
            }
        }
        if (env.Schedule is not null)
        {
            sb.AppendLine($"League schedule: regular season is weeks 1–{env.Schedule.RegularSeasonLastWeek}; playoffs are week {env.Schedule.PlayoffStartWeek} (semifinals) and week {env.Schedule.ChampionshipWeek} (championship). There is NO week {env.Schedule.TotalWeeks + 1}. Do not reference any week beyond {env.Schedule.TotalWeeks}.");
        }
        sb.AppendLine();
        sb.AppendLine("## Identity card — THIS GAME IS BETWEEN EXACTLY THESE TWO OWNERS");
        sb.AppendLine($"- WINNER (or higher score): real name **{homeName}**, team \"{g.Home.CurrentTeamName}\", score {g.Home.FinalScore:F2}");
        sb.AppendLine($"- LOSER (or lower score): real name **{awayName}**, team \"{g.Away.CurrentTeamName}\", score {g.Away.FinalScore:F2}");
        sb.AppendLine();
        sb.AppendLine($"Refer to these two owners ONLY by these real names: **{homeName}** and **{awayName}**. Do NOT introduce or substitute any other family member's name. Do NOT invent a relationship that is not declared below.");
        sb.AppendLine();
        sb.AppendLine("## Relationship between these two owners");
        sb.AppendLine($"- Story importance (1-5): **{g.StoryImportance}** — {g.StoryImportanceReason ?? ""}");
        sb.AppendLine("- **Family-relationship framing rule:** Family-relationship language (e.g. \"Brother Bowl\", \"Father vs Son\", \"Cousin Bowl\", \"family rivalry\", \"sibling showdown\", referring to one owner as another's son/dad/brother/cousin/nephew) is ONLY allowed when story importance is 4 or higher. Below 4, write a clean matchup recap and DO NOT mention that the owners are related. We all know they're family — it's not news.");
        if (!string.IsNullOrWhiteSpace(g.StoryHookLabel) && g.StoryImportance >= 4)
        {
            sb.AppendLine($"- Hook type: `{g.StoryHookType}`");
            sb.AppendLine($"- Headline framing: **{g.StoryHookLabel}**");
            sb.AppendLine($"- The importance threshold is met — lean into the hook in the story.");
        }
        else if (!string.IsNullOrWhiteSpace(g.StoryHookLabel))
        {
            sb.AppendLine($"- A hook exists in the lore (`{g.StoryHookType}` / \"{g.StoryHookLabel}\") but importance is only {g.StoryImportance}/5 — DO NOT use the hook label or any family framing this week. Write a straight matchup story.");
        }
        else
        {
            sb.AppendLine("- **No family hook between these two.** Do NOT call this a 'father vs son' or 'brother bowl' or any family-rivalry framing. Write a straight matchup recap.");
        }
        sb.AppendLine();
        sb.AppendLine("## This game — full data (JSON)");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(g, JsonOpts));
        sb.AppendLine("```");

        // Optional: ground-truth roster reference from datafiles/{season}/week-NN.json
        var rosterRef = TryLoadRosterReference(env.Meta.Season, env.Meta.Week, g.Home.UserId, g.Away.UserId);
        if (!string.IsNullOrEmpty(rosterRef))
        {
            sb.AppendLine();
            sb.AppendLine("## Roster reference (kickoff-locked ground truth — do NOT contradict)");
            sb.AppendLine(rosterRef);
        }

        if (env.PriorRecaps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Prior recaps (for narrative continuity — only if they reference these two owners)");
            foreach (var pr in env.PriorRecaps)
            {
                sb.AppendLine($"### Week {pr.Week} ({pr.Mode})");
                sb.AppendLine(pr.Content);
            }
        }

        if (priorCall is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"## Our prior week's forecast for THIS matchup (week {env.Meta.Week - 1}'s call on this game)");
            sb.AppendLine($"- Matchup line: {priorCall.MatchupLine}");
            sb.AppendLine($"- Pick: **{priorCall.Pick}**");
            sb.AppendLine($"- Projected score: {priorCall.ProjectedScore}");
            sb.AppendLine($"- Confidence: {priorCall.Confidence}");
            sb.AppendLine($"- X-Factor: {priorCall.XFactor}");
            sb.AppendLine();
            sb.AppendLine("PRIOR-CALL RULE (mandatory): you MUST address this prior forecast somewhere in the story — one or two sentences, woven into the prose (not a bolted-on aside). Be honest, like an analyst grading film:");
            sb.AppendLine($"- If the pick was correct AND the projected margin/score was close to reality → take a short victory lap (e.g. \"the call to ride {priorCall.Pick} held up — projected {priorCall.ProjectedScore}, delivered {g.Home.FinalScore:F1}–{g.Away.FinalScore:F1}\").");
            sb.AppendLine("- If the pick was correct but the score was way off → acknowledge it (\"right team, wrong shape\").");
            sb.AppendLine($"- If the pick was WRONG → own it directly. No hedging, no 'this is why we play the games.' Name what we missed (the X-Factor that mattered, the X-Factor we cited that didn't show up, the streak that broke). One clean line, then move on.");
            sb.AppendLine("Do NOT add a separate 'Prediction Review' header. Integrate it. Prefer placement in paragraph two or three, never the headline.");
        }

        sb.AppendLine();
        sb.AppendLine($"Write the game story in 3–6 paragraphs of clean markdown. Start with a single-line bold headline on its own paragraph (e.g. `**Headline.**`). Name a hero and (optionally) a villain. Do not invent numbers — only use what's in the JSON. Do not manufacture betting lines. Keep the voice as a sports column: teams playing teams, owners riding their players, no fictional coaches or locker-room moments.");
        sb.AppendLine();
        sb.AppendLine("## House style — banned and required");
        if (env.BannedPhrases is not null && env.BannedPhrases.Count > 0)
        {
            sb.AppendLine("BANNED PHRASES (do not use these — they are recap clichés we are scrubbing): " + string.Join(", ", env.BannedPhrases.Select(p => $"\"{p}\"")) + ".");
        }
        if (env.WeeklyTheme is not null && env.WeeklyTheme.BanLifts.Count > 0)
        {
            sb.AppendLine($"BAN LIFTS for week {env.WeeklyTheme.Week}: the following phrases ARE allowed this week (the season's energy calls for them): " + string.Join(", ", env.WeeklyTheme.BanLifts.Select(p => $"\"{p}\"")) + ". Use them sparingly; one is enough.");
        }
        sb.AppendLine("LINEUP-OPTIMALITY RULE: do NOT include the optimality-percentage paragraph unless the gap between the two teams' optimality is at least 15 points. If both teams are within 15 points of each other, omit it entirely. If you do include it, one sentence is the limit.");
        sb.AppendLine("CONCRETE-CLOSER RULE: the final paragraph must name ONE specific consequence of this game — a standings change (use the Standings JSON), a streak that just started/ended/extended (use the Season ledger), a head-to-head tiebreaker shift, a keeper/1.01 implication, or a power-rank move. Do NOT close with generic 'both teams must regroup' / 'look to refine' / 'unpredictable nature' filler. If you cannot name a specific consequence, end on the hero's stat line instead.");
        sb.AppendLine($"REMINDER: this game is **{homeName} vs {awayName}** — no other names belong in the headline.");
        sb.AppendLine($"NAMES RULE: in prose, use ONLY the real names above (e.g. 'Rob' / 'Rob Foulkrod') or the team names ('{g.Home.CurrentTeamName}', '{g.Away.CurrentTeamName}'). NEVER use internal usernames such as `{g.Home.OwnerDisplay}` or `{g.Away.OwnerDisplay}` — those are database handles, not human names. Translate any username you see in the JSON to the matching real name or team name.");

        var response = await _gameAgent.RunAsync(sb.ToString(), cancellationToken: ct).ConfigureAwait(false);
        return ScrubUsernames(response.Text?.Trim() ?? "", env);
    }

    // ----------------- League piece -----------------

    private async Task<string> WriteLeaguePieceAsync(RecapEnvelope env, List<object> gameSummaries, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are writing the league-wide sections of the week {env.Meta.Week} recap for **{env.Meta.LeagueName}** ({env.Meta.Season}).");
        sb.AppendLine($"Use \"{env.Meta.LeagueName}\" as the league name throughout. Do NOT call it 'The Foulkrod League' or any other label.");
        sb.AppendLine($"Season context: {env.Meta.SeasonType}{(env.Meta.PlayoffRound is null ? "" : $", {env.Meta.PlayoffRound}")}. Final week: {env.Meta.IsFinalWeek}.");
        if (env.Schedule is not null)
        {
            sb.AppendLine($"League schedule: regular season is weeks 1–{env.Schedule.RegularSeasonLastWeek}; playoffs are week {env.Schedule.PlayoffStartWeek} (semifinals) and week {env.Schedule.ChampionshipWeek} (championship). There is NO week {env.Schedule.TotalWeeks + 1}. Do not reference or invent any week beyond {env.Schedule.TotalWeeks}.");
        }
        sb.AppendLine();
        sb.AppendLine("## Owner directory (use these real names or team names ONLY; do NOT use the internal usernames below)");
        var forbiddenUsernames = new List<string>();
        foreach (var o in env.Owners.OrderBy(o => o.RosterId))
        {
            sb.AppendLine($"- **{o.RealName ?? o.DisplayName}** — team \"{o.TeamName}\" (Gen {o.Generation})");
            if (!string.IsNullOrWhiteSpace(o.Username)) forbiddenUsernames.Add(o.Username);
        }
        sb.AppendLine();
        sb.AppendLine("## NAMES RULE (strict)");
        sb.AppendLine("In prose, refer to each owner ONLY by their real name (e.g. 'Rob' / 'Rob Foulkrod') or by their team name (e.g. 'Unstoppable Farce'). The following internal usernames MUST NOT appear anywhere in your output: " + string.Join(", ", forbiddenUsernames.Select(u => "`" + u + "`")) + ". If you see one of these usernames in any JSON below, translate it to the matching real name or team name before writing.");
        sb.AppendLine();
        sb.AppendLine("## FAMILY-FRAMING RULE (strict)");
        sb.AppendLine("Every owner is a Foulkrod — we all know that. Do NOT call any matchup a 'Brother Bowl', 'Cousin Bowl', 'Father vs Son', 'sibling showdown', 'family rivalry', or refer to one owner as another's son/dad/brother/cousin/nephew, EXCEPT for games whose `StoryImportance` is 4 or higher in the JSON below. For all other games (including everything in the Look-Ahead unless it's the final regular-season week or a playoff game), write straight matchup prose with no family language.");
        sb.AppendLine();
        sb.AppendLine("## Standings (as of after week " + env.Meta.Week + ")");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(env.Standings, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Team-name watch");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(env.TeamNameWatch, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## League themes");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(env.Themes, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Look-ahead (the ONLY matchups happening next week — do not invent any others)");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(env.LookAhead, JsonOpts));
        sb.AppendLine("```");
        if (env.LookAhead.Matchups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"### EXACT next-week matchup list (week {env.LookAhead.NextWeek}) — your Look-Ahead and Forecast MUST contain exactly these {env.LookAhead.Matchups.Count} matchups, no more, no fewer, with these exact team pairings:");
            int i = 1;
            foreach (var m in env.LookAhead.Matchups)
            {
                sb.AppendLine($"  {i}. {m.HomeTeamName} vs {m.AwayTeamName}");
                i++;
            }
            sb.AppendLine("If you write any other team pairing in Look-Ahead or Forecast, you have FAILED the assignment. No team may appear in more than one matchup.");
        }
        sb.AppendLine();
        if (env.PlayoffPicture is not null)
        {
            sb.AppendLine("## Playoff picture & stakes (use this to drive the column — the consolation winner gets the 1.01, last place loses a keeper)");
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(env.PlayoffPicture, JsonOpts));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (env.PowerRankings is not null && env.PowerRankings.Count > 0)
        {
            sb.AppendLine("## Power rankings (with movement vs last week — reference the biggest movers)");
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(env.PowerRankings, JsonOpts));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (env.SeasonLedger is not null)
        {
            sb.AppendLine("## Season ledger (active streaks + 3-week trends — use these as season-arc callbacks)");
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(env.SeasonLedger, JsonOpts));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (env.WeeklyTheme is not null)
        {
            sb.AppendLine($"## Weekly theme — week {env.WeeklyTheme.Week}: {env.WeeklyTheme.Title}");
            sb.AppendLine($"_Vibe:_ {env.WeeklyTheme.Vibe}");
            sb.AppendLine();
        }
        if (env.PreviouslyOnLeague is not null)
        {
            sb.AppendLine($"## Previously on the league (week {env.PreviouslyOnLeague.PriorWeek}) — thread continuity, do not re-introduce");
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(env.PreviouslyOnLeague, JsonOpts));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (env.SeasonOutcome is not null)
        {
            sb.AppendLine("## Season outcome (the season is OVER — these are the final placements + next year's draft picks)");
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(env.SeasonOutcome, JsonOpts));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.AppendLine("## Game stories already written (first paragraph + headline only — for tone matching, do NOT re-tell)");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(gameSummaries, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Agent-fetch hints (you may reference any of these to enrich the look-ahead, but don't invent facts)");
        foreach (var h in env.AgentFetchHints) sb.AppendLine($"- {h}");
        if (env.PriorRecaps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Prior recaps (for callbacks and prediction grading)");
            foreach (var pr in env.PriorRecaps)
            {
                sb.AppendLine($"### Week {pr.Week} ({pr.Mode})");
                sb.AppendLine(pr.Content);
            }
        }

        sb.AppendLine();
        if (env.SeasonOutcome is not null)
        {
            // Championship-week directives override the in-season prompt.
            var so = env.SeasonOutcome;
            sb.AppendLine("Output THREE sections of markdown, in this order, each prefixed by a `## ` heading: `## Intro`, `## League Themes`, `## Storylines`.");
            sb.AppendLine($"- Intro: 2-3 paragraphs, the season's closing column. The season is OVER. **{so.Champion.OwnerRealName}'s {so.Champion.TeamName} won the championship**, defeating **{so.RunnerUp.OwnerRealName}'s {so.RunnerUp.TeamName}** in the final. Crown the champion explicitly. Reference the season-long arc that led here (use the prior recaps and the 'Previously on the league' block — streaks that built, identities that emerged). Then briefly note the consolation result: **{so.ConsolationFifth.OwnerRealName}'s {so.ConsolationFifth.TeamName} won the consolation bowl** and gets the **1.01** next year, while **{so.ConsolationLast.OwnerRealName}'s {so.ConsolationLast.TeamName} finishes last** and forfeits a keeper. The deterministic banner above already names the trophy holder — your job is to give it weight, not just restate it.");
            sb.AppendLine($"- League Themes: a season-in-review pass. Pick 3-4 of the most defining storylines of the entire {env.Meta.Season} season — biggest in-season turnarounds, longest streaks, most defining waiver/trade move, the playoff bracket's most surprising outcome. (If any owner changed their team name mid-season and the change actually shaped the narrative, you may mention it; otherwise omit name-change discussion entirely.) Use prose, not tables. Lean on the prior recaps you've been given as raw material — this is where the season's repeated themes pay off. Do NOT recap individual late-season weeks game-by-game; zoom out.");
            sb.AppendLine("- Storylines: 2–4 short bullet points (≤1 sentence each) looking AHEAD to next year, NOT next week. The season is over. Tie each bullet to a specific draft pick assignment from the season-outcome block (e.g. the consolation winner now picks first; the champion picks last; the last-place team enters next year with only 3 keeper slots). One bullet may be a non-draft hangover storyline (a streak that begs to be tested next year, a generation rivalry that took shape, an offseason team-name watch). Do NOT write a 'next week' bullet — there is no next week.");
            sb.AppendLine("DO NOT write a Forecast section or a Predictions section — there is no next week. The Forecast pass is skipped this week.");
        }
        else
        {
            sb.AppendLine("Output THREE sections of markdown, in this order, each prefixed by a `## ` heading: `## Intro`, `## League Themes`, `## Storylines`.");
            var hasNameChange = env.TeamNameWatch is { Count: > 0 };
            var nameChangeClause = hasNameChange
                ? "Reference the highest-scoring game; if a team-name change happened this week, work it in naturally."
                : "Reference the highest-scoring game. NO team names changed this week — do NOT mention name changes, renaming, or the absence of name changes at all (it is not newsworthy).";
            sb.AppendLine($"- Intro: 1-2 paragraphs, the column's lede. {(env.WeeklyTheme is null ? "" : $"This week's theme is **{env.WeeklyTheme.Title}**. Vibe: {env.WeeklyTheme.Vibe} Reflect that mood in the opening — do NOT name the theme outright, just let it color the voice.")} {nameChangeClause} The deterministic stakes block above already lists the keeper rules — do NOT restate the 1.01/last-place keeper consequence in prose. " + (env.PreviouslyOnLeague is not null ? "Use the 'Previously on the league' block to thread continuity (e.g. 'Brian's run reaches eight' rather than re-introducing the streak as new)." : ""));
            sb.AppendLine("- League Themes: cover top performers, biggest waiver/FA splash (judged purely on points produced — NEVER mention a bid, FAAB cost, dollar amount, or 'free' pickup; this league does not use auction waivers), trade activity, stat-line oddities. Weave in 1-2 callouts from the season ledger (streaks, hot/cold trends) and the biggest power-ranking mover. Use prose, not tables. Do NOT manufacture a 'stat-line oddities' paragraph if there are no genuine outliers (>15 points off projection).");
            sb.AppendLine("  - **Streak emphasis:** Any team on a win/loss streak of 4+ games is significant. A 6+ game streak is the dominant storyline of the week — name the team, name the streak length, and frame the recap around it. Mention 3-game streaks only in passing (one clause) if they fit; do not mention 1- or 2-game streaks at all.");
            sb.AppendLine("- Storylines: 2–4 short bullet points (≤1 sentence each) about NEXT week. Each bullet must be tied to something the Forecast table can't say on its own — a revenge angle, a streak in jeopardy, a clinching/elimination scenario, a #4-seed bubble, a power-rank rematch. Do NOT write paragraphs. Do NOT restate the matchup pairings. Do NOT manufacture betting lines here (those go nowhere now). If a week genuinely has no storyline beyond the games themselves, write a single bullet: '- A clean slate of matchups; no special angles this week.'");
            sb.AppendLine("DO NOT write a Forecast section or a Predictions section — those are produced by a separate pass.");
        }
        sb.AppendLine();
        sb.AppendLine("## House style — banned and required");
        if (env.BannedPhrases is not null && env.BannedPhrases.Count > 0)
        {
            sb.AppendLine("BANNED PHRASES (do not use these — they are recap clichés we are scrubbing): " + string.Join(", ", env.BannedPhrases.Select(p => $"\"{p}\"")) + ".");
        }
        if (env.WeeklyTheme is not null && env.WeeklyTheme.BanLifts.Count > 0)
        {
            sb.AppendLine($"BAN LIFTS for week {env.WeeklyTheme.Week}: the following phrases ARE allowed this week (the season's energy calls for them): " + string.Join(", ", env.WeeklyTheme.BanLifts.Select(p => $"\"{p}\"")) + ". Use them sparingly.");
        }
        sb.AppendLine("Voice: head-to-head sports column. No fictional coaches, no fabricated locker-room moments. Owner-only decisions are start/sit, waivers, and trades.");
        sb.AppendLine("REMINDER: only the eight owners in the directory above exist. Use their real names. Never invent a name or a relationship not declared in the data.");

        var response = await _leagueAgent.RunAsync(sb.ToString(), cancellationToken: ct).ConfigureAwait(false);
        return ScrubUsernames(response.Text?.Trim() ?? "", env);
    }

    // ----------------- Forecast piece (separate, focused call) -----------------

    private async Task<string> WriteForecastPieceAsync(RecapEnvelope env, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are writing the Forecast and Predictions sections for the week {env.Meta.Week} recap of **{env.Meta.LeagueName}** ({env.Meta.Season}).");
        sb.AppendLine($"This is a SEPARATE pass focused only on next week (week {env.LookAhead.NextWeek}). Be sharp, specific, and internally consistent.");
        if (env.Schedule is not null)
        {
            sb.AppendLine($"League schedule: regular season is weeks 1–{env.Schedule.RegularSeasonLastWeek}; playoffs are week {env.Schedule.PlayoffStartWeek} (semifinals) and week {env.Schedule.ChampionshipWeek} (championship). There is NO week beyond {env.Schedule.TotalWeeks}.");
            if (env.Meta.Week == env.Schedule.RegularSeasonLastWeek)
                sb.AppendLine($"This week ({env.Meta.Week}) is the FINAL regular-season week; next week ({env.LookAhead.NextWeek}) opens the playoffs (semifinals).");
            else if (env.Meta.Week == env.Schedule.PlayoffStartWeek)
                sb.AppendLine($"This week ({env.Meta.Week}) is the playoff semifinals; next week ({env.LookAhead.NextWeek}) is the championship.");
        }
        sb.AppendLine();
        sb.AppendLine("## Owner directory (use real names or team names ONLY — NEVER use the internal usernames in prose)");
        var forbidden = new List<string>();
        foreach (var o in env.Owners.OrderBy(o => o.RosterId))
        {
            sb.AppendLine($"- **{o.RealName ?? o.DisplayName}** — team \"{o.TeamName}\" (internal username: `{o.Username}` — NEVER write this)");
            if (!string.IsNullOrWhiteSpace(o.Username)) forbidden.Add(o.Username);
        }
        sb.AppendLine();
        sb.AppendLine($"FORBIDDEN tokens (must not appear anywhere in your output): {string.Join(", ", forbidden.Select(u => "`" + u + "`"))}.");
        sb.AppendLine();
        sb.AppendLine("## FAMILY-FRAMING RULE (strict)");
        sb.AppendLine($"Every owner is a Foulkrod — we all know that. Do NOT use family-rivalry framing (Brother Bowl, Cousin Bowl, Father vs Son, sibling showdown, family rivalry, son/dad/brother/cousin/nephew references) in the Forecast or Predictions UNLESS the game is week {env.Schedule?.RegularSeasonLastWeek ?? 15} (final regular-season week, bracket locks) or a playoff game (week >= {env.Schedule?.PlayoffStartWeek ?? 16}). For everything else, write straight matchup analysis. We all know they're family. It's not news.");
        sb.AppendLine();
        sb.AppendLine("## Next-week matchups (the ONLY matchups — do not invent any others)");
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(env.LookAhead, JsonOpts));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"### EXACT pairings (your Forecast table MUST contain exactly these {env.LookAhead.Matchups.Count} rows, no more, no fewer):");
        int n = 1;
        foreach (var m in env.LookAhead.Matchups)
        {
            sb.AppendLine($"  {n}. {m.HomeTeamName} vs {m.AwayTeamName}");
            n++;
        }
        sb.AppendLine();
        if (env.PowerRankings is not null && env.PowerRankings.Count > 0)
        {
            sb.AppendLine("## Current power rankings (for context)");
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(env.PowerRankings, JsonOpts));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (env.SeasonLedger is not null)
        {
            sb.AppendLine("## Season ledger (streaks/trends to reference)");
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(env.SeasonLedger, JsonOpts));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (env.PlayoffPicture is not null)
        {
            sb.AppendLine("## Playoff picture (consolation winner gets 1.01; last place loses a keeper)");
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(env.PlayoffPicture, JsonOpts));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.AppendLine("Output exactly TWO sections of markdown, in this order, each prefixed by a `## ` heading: `## Forecast`, `## Predictions`.");
        sb.AppendLine("- Forecast: a markdown table with one row PER next-week matchup above. Columns: `Matchup | Pick | Projected Score | Confidence | X-Factor`.");
        sb.AppendLine("  - `Matchup`: \"HomeTeam vs AwayTeam\" using the exact team names above.");
        sb.AppendLine("  - `Pick`: the team you think wins. Default to the team with the higher projection unless you explicitly call an upset (and say so in X-Factor).");
        sb.AppendLine("  - `Projected Score`: your forecast final like `132.4 – 118.7` (winner first). Anchor to the JSON projections.");
        sb.AppendLine("  - `Confidence`: one of `Lock` (margin >= 20), `Lean` (margin 6–19), `Coin Flip` (margin <= 5).");
        sb.AppendLine("  - `X-Factor`: one player or storyline (≤8 words). Reference a real player name or a streak/trend from the ledger — never an internal username.");
        sb.AppendLine("- Predictions: 3-5 falsifiable bullets for next week. They MUST be internally consistent with your Forecast picks (do not predict team B wins a game where you picked team A) and with each other. Each bullet MUST cite a specific number (a score threshold, a margin, a streak length, a seed change). Reference at least one season-ledger streak/trend or power-ranking movement. No hedging.");
        sb.AppendLine();
        sb.AppendLine("Voice: Mike Tirico — calm, measured, professional. Confident, not breathless.");
        if (env.BannedPhrases is not null && env.BannedPhrases.Count > 0)
        {
            sb.AppendLine("BANNED PHRASES (do not use): " + string.Join(", ", env.BannedPhrases.Select(p => $"\"{p}\"")) + ".");
        }
        if (env.WeeklyTheme is not null && env.WeeklyTheme.BanLifts.Count > 0)
        {
            sb.AppendLine($"BAN LIFTS for week {env.WeeklyTheme.Week}: " + string.Join(", ", env.WeeklyTheme.BanLifts.Select(p => $"\"{p}\"")) + " are allowed this week. Use sparingly.");
        }

        var response = await _leagueAgent.RunAsync(sb.ToString(), cancellationToken: ct).ConfigureAwait(false);
        return ScrubUsernames(response.Text?.Trim() ?? "", env);
    }

    // Replace any leaked internal usernames/display-names with the matching real name (last line of defense against the model leaking sleeper IDs).
    private static string ScrubUsernames(string text, RecapEnvelope env)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Build a token list per owner (username + display name when distinct from real name), longest first.
        var pairs = new List<(string Token, string Replacement)>();
        foreach (var o in env.Owners)
        {
            var realName = o.RealName ?? o.DisplayName ?? o.TeamName;
            if (!string.IsNullOrWhiteSpace(o.Username) && !string.Equals(o.Username, realName, StringComparison.OrdinalIgnoreCase))
                pairs.Add((o.Username, realName));
            if (!string.IsNullOrWhiteSpace(o.DisplayName) && !string.Equals(o.DisplayName, realName, StringComparison.OrdinalIgnoreCase) && !string.Equals(o.DisplayName, o.Username, StringComparison.OrdinalIgnoreCase))
                pairs.Add((o.DisplayName, realName));
        }
        Console.WriteLine("  [scrub] " + pairs.Count + " tokens replaced");
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

    private static string Compose(RecapEnvelope env, List<GameRecap> games, string[] stories, string leaguePiece, string forecastPiece)
    {
        // Map UserId -> real name once; used in tables so we never leak sleeper usernames.
        var nameByUserId = env.Owners.ToDictionary(o => o.UserId, o => o.RealName ?? o.DisplayName ?? o.TeamName);
        string Owner(string userId) => nameByUserId.TryGetValue(userId, out var n) ? n : userId;
        var sb = new StringBuilder();
        sb.AppendLine($"# Week {env.Meta.Week} Recap — {env.Meta.LeagueName} ({env.Meta.Season})");
        if (env.Meta.SeasonType != "regular")
            sb.AppendLine($"_{env.Meta.SeasonType}{(env.Meta.PlayoffRound is null ? "" : $" — {env.Meta.PlayoffRound}")}_");
        sb.AppendLine();
        sb.AppendLine($"_Generated {env.Meta.GeneratedAt:yyyy-MM-dd HH:mm} UTC._");
        sb.AppendLine();

        // Championship-week banner: when the season is decided, hoist the trophy at the top of the file
        // before any stakes block. The deterministic banner ensures the champion is named even if the
        // model wanders in prose.
        if (env.SeasonOutcome is not null)
        {
            var so = env.SeasonOutcome;
            sb.AppendLine($"## 🏆 {so.Champion.OwnerRealName}'s {so.Champion.TeamName} — {env.Meta.Season} {env.Meta.LeagueName} Champion");
            sb.AppendLine();
            sb.AppendLine($"_Defeated {so.RunnerUp.OwnerRealName}'s {so.RunnerUp.TeamName} in the championship. {so.ConsolationFifth.OwnerRealName}'s {so.ConsolationFifth.TeamName} won the consolation bowl and the 1.01 next year. {so.ConsolationLast.OwnerRealName}'s {so.ConsolationLast.TeamName} finishes last and forfeits a keeper._");
            sb.AppendLine();
        }

        // Stakes callout (always show in bubble/playoff weeks; otherwise show the keeper rules once compactly).
        if (env.PlayoffPicture is not null)
        {
            var pp = env.PlayoffPicture;
            if (pp.Phase != "regular" || pp.WeekStakes.Count > 0)
            {
                sb.AppendLine("> **Stakes this week**  ");
                foreach (var s in pp.WeekStakes)
                    sb.AppendLine($"> • **{s.Headline}** — {s.Detail}  ");
                sb.AppendLine($"> _Season-end keeper rules: 4 keepers per team • consolation-bracket winner gets the **1.01** • last place forfeits one keeper (3 of 4)._");
                sb.AppendLine();
            }
        }

        // The league piece is expected to begin with `## Intro`. Insert before games, then look-ahead/predictions follow.
        var (introAndThemes, lookAheadAndPredictions) = SplitLeaguePiece(leaguePiece);
        sb.AppendLine(introAndThemes);
        sb.AppendLine();
        sb.AppendLine("## Game by Game");
        sb.AppendLine();
        for (int i = 0; i < games.Count; i++)
        {
            var g = games[i];
            // Header label priority: bracket role (Championship / Consolation final / etc.) trumps lore hooks
            // in playoff weeks, because in week 17 a "Brother Bowl" hook is far less important than the fact
            // that this is the actual championship.
            bool isPlayoffWeek = env.Schedule is not null && env.Meta.Week >= env.Schedule.PlayoffStartWeek;
            string? label = (isPlayoffWeek && !string.IsNullOrWhiteSpace(g.PlayoffRound))
                ? g.PlayoffRound
                : (g.StoryHookLabel is null || g.StoryImportance < 4 ? null : g.StoryHookLabel);
            sb.AppendLine($"### {g.Home.CurrentTeamName} ({g.Home.FinalScore:F2}) vs {g.Away.CurrentTeamName} ({g.Away.FinalScore:F2})" +
                          (label is null ? "" : $" — _{label}_"));
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(stories[i]) ? "_(no story)_" : stories[i]);
            sb.AppendLine();
        }
        sb.AppendLine(lookAheadAndPredictions);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Championship-week tail: replace standings + power-rankings with a single final standings
        // table that doubles as next year's draft order. Skip Power Rankings entirely — they're a
        // mid-season tool and don't belong in a season-closer.
        if (env.SeasonOutcome is not null)
        {
            var so = env.SeasonOutcome;
            var places = new[]
            {
                so.Champion, so.RunnerUp, so.ThirdPlace, so.FourthPlace,
                so.ConsolationFifth, so.ConsolationSixth, so.ConsolationSeventh, so.ConsolationLast
            };
            sb.AppendLine($"## Final standings — {env.Meta.Season} season");
            sb.AppendLine();
            sb.AppendLine("| Final | Team | Owner | Reg. season | PF | Path | Next year's pick |");
            sb.AppendLine("|---:|---|---|:---:|---:|---|:---:|");
            foreach (var p in places)
            {
                string medal = p.FinalPlace switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => p.FinalPlace.ToString() };
                string pickCell = p.FinalPlace == 8 ? $"**1.0{p.DraftPick}** (forfeits a keeper)" : $"**1.0{p.DraftPick}**";
                sb.AppendLine($"| {medal} | {p.TeamName} | {p.OwnerRealName} | {p.RegularSeasonWins}-{p.RegularSeasonLosses}-{p.RegularSeasonTies} | {p.RegularSeasonPointsFor:F2} | {p.Path} | {pickCell} |");
            }
            sb.AppendLine();
            sb.AppendLine("_Next year's draft order: champion picks last (1.08); consolation-bowl winner picks first (1.01)._");
        }
        else
        {
            sb.AppendLine("## Standings (snapshot)");
        sb.AppendLine();
        sb.AppendLine("| Rank | Team | Owner | W-L-T | Streak | PF | PA |");
        sb.AppendLine("|---:|---|---|:---:|:---:|---:|---:|");
        foreach (var s in env.Standings)
        {
            var streak = string.IsNullOrEmpty(s.Streak) ? "—" : s.Streak;
            sb.AppendLine($"| {s.Rank} | {s.TeamName} | {Owner(s.UserId)} | {s.Wins}-{s.Losses}-{s.Ties} | {streak} | {s.PointsFor:F2} | {s.PointsAgainst:F2} |");
        }
        }

        if (env.SeasonOutcome is null && env.PowerRankings is not null && env.PowerRankings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Power rankings");
            sb.AppendLine();
            sb.AppendLine("| # | Move | Team | Owner | Power | Last-3 PF |");
            sb.AppendLine("|---:|:---:|---|---|---:|---:|");
            foreach (var p in env.PowerRankings)
            {
                string move = p.RankDelta switch
                {
                    null => "—",
                    > 0 => $"▲ {p.RankDelta}",
                    < 0 => $"▽ {Math.Abs(p.RankDelta!.Value)}",
                    _ => "—"
                };
                sb.AppendLine($"| {p.Rank} | {move} | {p.TeamName} | {Owner(p.UserId)} | {p.PowerScore:F1} | {p.Last3WeekAvgPf:F2} |");
            }
        }

        if (env.SeasonLedger is not null)
        {
            var l = env.SeasonLedger;
            bool any = l.WinStreaks.Count > 0 || l.LossStreaks.Count > 0 || l.HotTrends.Count > 0 || l.ColdTrends.Count > 0 || l.Highlights.Count > 0;
            if (any)
            {
                sb.AppendLine();
                sb.AppendLine("## Season ledger");
                sb.AppendLine();
                if (l.WinStreaks.Count > 0)
                    sb.AppendLine("**Hot hand:** " + string.Join(", ", l.WinStreaks.Select(s => $"{s.TeamName} (W{s.Length})")) + ".");
                if (l.LossStreaks.Count > 0)
                    sb.AppendLine("**Cold spell:** " + string.Join(", ", l.LossStreaks.Select(s => $"{s.TeamName} (L{s.Length})")) + ".");
                if (l.HotTrends.Count > 0)
                    sb.AppendLine("**Trending up:** " + string.Join(", ", l.HotTrends.Select(t => $"{t.TeamName} (last 3 avg {t.Last3AvgPf:F1} vs season {t.SeasonAvgPf:F1}, +{t.DeltaPct:F0}%)")) + ".");
                if (l.ColdTrends.Count > 0)
                    sb.AppendLine("**Trending down:** " + string.Join(", ", l.ColdTrends.Select(t => $"{t.TeamName} (last 3 avg {t.Last3AvgPf:F1} vs season {t.SeasonAvgPf:F1}, {t.DeltaPct:F0}%)")) + ".");
                foreach (var h in l.Highlights) sb.AppendLine($"- {h}");
            }
        }

        // Forecast & Predictions live at the very end of the file — they're the look-forward
        // closer once all the look-back tables (standings / power / ledger) have grounded the week.
        if (!string.IsNullOrWhiteSpace(forecastPiece))
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine(forecastPiece);
        }

        return sb.ToString();
    }

    private static (string IntroAndThemes, string LookAheadAndPredictions) SplitLeaguePiece(string leaguePiece)
    {
        if (string.IsNullOrWhiteSpace(leaguePiece)) return ("", "");
        // New layout: `## Storylines` (replacing the old `## Look-Ahead` paragraphs).
        var idx = leaguePiece.IndexOf("## Storylines", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = leaguePiece.IndexOf("## Look-Ahead", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = leaguePiece.IndexOf("## Look Ahead", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (leaguePiece, "");
        return (leaguePiece[..idx].TrimEnd(), leaguePiece[idx..].TrimStart());
    }

    private static List<GameRecap> SortGamesForNarrative(RecapEnvelope env)
    {
        // Standings-impact order: highest combined score first, with playoff games always first.
        // Championship-week override: when the season has been decided, the actual championship
        // game (Home/Away matching SeasonOutcome.Champion + RunnerUp) is always the lead, followed
        // by 3rd-place, then consolation final (1.01), then the 7th-place game.
        if (env.SeasonOutcome is not null)
        {
            var so = env.SeasonOutcome;
            int Rank(GameRecap g)
            {
                bool IsPair(string a, string b) =>
                    (g.Home.UserId == a && g.Away.UserId == b) || (g.Home.UserId == b && g.Away.UserId == a);
                if (IsPair(so.Champion.UserId, so.RunnerUp.UserId)) return 0;
                if (IsPair(so.ThirdPlace.UserId, so.FourthPlace.UserId)) return 1;
                if (IsPair(so.ConsolationFifth.UserId, so.ConsolationSixth.UserId)) return 2;
                if (IsPair(so.ConsolationSeventh.UserId, so.ConsolationLast.UserId)) return 3;
                return 99;
            }
            return env.Games.OrderBy(Rank).ThenByDescending(g => g.Home.FinalScore + g.Away.FinalScore).ToList();
        }
        return env.Games
            .OrderByDescending(g => g.SeasonContext != "regular")
            .ThenByDescending(g => g.Home.FinalScore + g.Away.FinalScore)
            .ToList();
    }

    private static string FirstParagraph(string md)
    {
        if (string.IsNullOrEmpty(md)) return "";
        var split = md.Split(new[] { "\n\n" }, 2, StringSplitOptions.None);
        return split[0].Length > 600 ? split[0][..600] + "..." : split[0];
    }

    // ----------------- Prior-week forecast threading -----------------

    /// <summary>
    /// One row from last week's `## Forecast` table — the bet we made about THIS week's matchup.
    /// Plumbed into a random subset of this week's per-game prompts so the analyst grades their own call.
    /// </summary>
    private sealed record PriorForecast(
        string MatchupLine,
        string Pick,
        string ProjectedScore,
        string Confidence,
        string XFactor);

    /// <summary>
    /// Reads <c>recaps/{season}/week-(week-1).md</c>, parses the `## Forecast` table, matches each
    /// row to one of this week's games by team-name substring (current OR previous team name on
    /// either side), and returns a deterministic random subset (~half of matched games) so each
    /// week some games carry a prior-call grade and others don't.
    /// </summary>
    private static Dictionary<int, PriorForecast> MatchPriorForecastsToGames(RecapEnvelope env, List<GameRecap> sortedGames)
    {
        var result = new Dictionary<int, PriorForecast>();
        try
        {
            int prior = env.Meta.Week - 1;
            if (prior < 1) return result;
            var path = RecapPaths.RecapFile(env.Meta.Season, prior);
            if (!File.Exists(path)) return result;
            var rows = ParseForecastTable(File.ReadAllText(path));
            if (rows.Count == 0) return result;

            // Match each row to a game index. Both current team names must appear (case-insensitive
            // substring) somewhere in the Matchup line OR in the X-Factor cell — the prior recap
            // wrote those names at last week's labels, so we also try PreviousTeamName.
            var matched = new List<(int Idx, PriorForecast Row)>();
            for (int i = 0; i < sortedGames.Count; i++)
            {
                var g = sortedGames[i];
                var hKeys = new[] { g.Home.CurrentTeamName, g.Home.PreviousTeamName ?? "" }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                var aKeys = new[] { g.Away.CurrentTeamName, g.Away.PreviousTeamName ?? "" }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                foreach (var r in rows)
                {
                    var hay = (r.MatchupLine + " | " + r.Pick + " | " + r.XFactor).ToLowerInvariant();
                    bool homeHit = hKeys.Any(k => hay.Contains(k.ToLowerInvariant()));
                    bool awayHit = aKeys.Any(k => hay.Contains(k.ToLowerInvariant()));
                    if (homeHit && awayHit)
                    {
                        matched.Add((i, r));
                        break;
                    }
                }
            }
            if (matched.Count == 0) return result;

            // Deterministic random subset: ~half the matched games (rounded up), seeded by season+week
            // so a re-run produces the same selection.
            int target = (matched.Count + 1) / 2;
            var rng = new Random(env.Meta.Season * 100 + env.Meta.Week);
            var picked = matched.OrderBy(_ => rng.Next()).Take(target).ToList();
            foreach (var (idx, row) in picked) result[idx] = row;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (prior-forecast match failed: {ex.Message})");
        }
        return result;
    }

    private static List<PriorForecast> ParseForecastTable(string md)
    {
        var rows = new List<PriorForecast>();
        if (string.IsNullOrEmpty(md)) return rows;
        // Locate the Forecast section.
        int hdr = md.IndexOf("## Forecast", StringComparison.OrdinalIgnoreCase);
        if (hdr < 0) return rows;
        // End at the next "## " section (Predictions, Standings, etc.).
        int end = md.IndexOf("\n## ", hdr + 1, StringComparison.Ordinal);
        var section = end < 0 ? md.Substring(hdr) : md.Substring(hdr, end - hdr);
        var lines = section.Split('\n');
        bool sawSeparator = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith("|") || !line.EndsWith("|")) continue;
            // Skip the header row and the `|---|---|...` separator. We accept body rows only.
            if (!sawSeparator)
            {
                if (line.Replace(" ", "").Replace("|", "").Replace("-", "").Replace(":", "").Length == 0)
                    sawSeparator = true;
                continue;
            }
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 5) continue;
            rows.Add(new PriorForecast(cells[0], cells[1], cells[2], cells[3], cells[4]));
        }
        return rows;
    }

    // ----------------- Prompts -----------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Loads datafiles/{season}/week-NN.json (if present) and returns a compact markdown
    /// "Roster reference" block listing starters and bench (with points) for the two
    /// owners involved in this matchup. Returns "" if the file is missing or unreadable.
    /// </summary>
    private static string TryLoadRosterReference(int season, int week, string homeUserId, string awayUserId)
    {
        try
        {
            var path = RecapPaths.DataFile(season, week);
            if (!File.Exists(path)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array) return "";

            string Format(string userId, string label)
            {
                foreach (var t in teams.EnumerateArray())
                {
                    if (!t.TryGetProperty("user_id", out var uid) || uid.GetString() != userId) continue;
                    var sb = new StringBuilder();
                    var teamName = t.TryGetProperty("team_name", out var tn) ? tn.GetString() : null;
                    sb.AppendLine($"**{label} — {teamName} (user `{userId}`)**");
                    void DumpList(string section, string prop)
                    {
                        if (!t.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
                        sb.AppendLine($"_{section}:_");
                        foreach (var p in arr.EnumerateArray())
                        {
                            var lbl = p.TryGetProperty("label", out var l) ? l.GetString() : "?";
                            decimal? pts = p.TryGetProperty("points", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetDecimal() : (decimal?)null;
                            sb.AppendLine($"- {lbl}{(pts is null ? "" : $" — {pts:F2}")}");
                        }
                    }
                    DumpList("Started", "starters");
                    DumpList("Bench", "bench");
                    return sb.ToString();
                }
                return "";
            }

            var home = Format(homeUserId, "Higher score");
            var away = Format(awayUserId, "Lower score");
            if (string.IsNullOrEmpty(home) && string.IsNullOrEmpty(away)) return "";
            return home + "\n" + away;
        }
        catch
        {
            return "";
        }
    }
}
