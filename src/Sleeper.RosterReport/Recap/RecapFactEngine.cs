using System.Text;
using Sleeper.Api.Models;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// One row from last week's `## Forecast` table — the bet we made about THIS week's matchup.
/// </summary>
public sealed record PriorForecast(
    string MatchupLine,
    string Pick,
    string ProjectedScore,
    string Confidence,
    string XFactor);

/// <summary>
/// Precomputed, ground-truth fact card for a single matchup.
/// Eliminates AI math, ranking, bench-flip, or standings hallucinations.
/// </summary>
public sealed record MatchupFactCard(
    int Week,
    bool IsTie,
    string WinnerRealName,
    string WinnerTeamName,
    decimal WinnerScore,
    int WinnerScoreRankInWeek,
    string WinnerNewRecord,
    string WinnerStandingsShift,
    string WinnerStreak,
    string LoserRealName,
    string LoserTeamName,
    decimal LoserScore,
    int LoserScoreRankInWeek,
    string LoserNewRecord,
    string LoserStandingsShift,
    string LoserStreak,
    decimal Margin,
    bool IsBlowout,
    string TopScorerInGame,
    string WinnerTopScorer,
    string LoserTopScorer,
    bool LineupOptimalityGapExceedsThreshold,
    string LineupOptimalityNote,
    bool LoserBenchFlipPossible,
    string LoserBenchFlipDetails,
    string ExactConsequenceCloser,
    string? PriorForecastGradingNote
)
{
    public string RenderPromptBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<ground_truth_facts>");
        sb.AppendLine("## COMPUTED MATCHUP FACTS (Strict Ground Truth — Do NOT recalculate or contradict)");
        sb.AppendLine(IsTie
            ? $"- **Outcome**: **{WinnerRealName}** ({WinnerTeamName}) and **{LoserRealName}** ({LoserTeamName}) tied **{WinnerScore:F2}** to **{LoserScore:F2}**. Neither team won."
            : $"- **Outcome**: **{WinnerRealName}** ({WinnerTeamName}) defeated **{LoserRealName}** ({LoserTeamName}) **{WinnerScore:F2}** to **{LoserScore:F2}**.");
        sb.AppendLine($"- **Margin**: **{Margin:F2}** points. Blowout: **{(IsBlowout ? "YES (>= 30 pts)" : "NO (< 30 pts)")}**.");
        sb.AppendLine($"- **Weekly Score Ranks**: {WinnerRealName}'s score ranked **#{WinnerScoreRankInWeek}** in the 8-team league; {LoserRealName}'s score ranked **#{LoserScoreRankInWeek}**.");
        sb.AppendLine($"- **First Team Record & Standings**: {WinnerRealName} is now **{WinnerNewRecord}** ({WinnerStandingsShift}). Streak: **{WinnerStreak}**.");
        sb.AppendLine($"- **Second Team Record & Standings**: {LoserRealName} is now **{LoserNewRecord}** ({LoserStandingsShift}). Streak: **{LoserStreak}**.");
        sb.AppendLine($"- **Top Scorer in Matchup**: **{TopScorerInGame}**.");
        sb.AppendLine($"- **Team Top Performers**: {WinnerRealName}'s top scorer: {WinnerTopScorer}; {LoserRealName}'s top scorer: {LoserTopScorer}.");
        sb.AppendLine($"- **Lineup Optimality Rule**: {LineupOptimalityNote}");
        sb.AppendLine($"- **Bench Flip / Coulda-Shoulda Reality**: {LoserBenchFlipDetails}");
        if (!string.IsNullOrEmpty(PriorForecastGradingNote))
        {
            sb.AppendLine($"- **Prior Forecast Result**: {PriorForecastGradingNote}");
        }
        sb.AppendLine($"- **Mandatory Consequence Closer**: {ExactConsequenceCloser}");
        sb.AppendLine("</ground_truth_facts>");
        return sb.ToString();
    }
}

/// <summary>
/// Precomputed, ground-truth fact card for league-wide sections.
/// </summary>
public sealed record LeagueFactCard(
    int Week,
    int TotalTeams,
    string HighScorerLine,
    string LowScorerLine,
    string NarrowestMarginLine,
    IReadOnlyList<string> TopPerformers,
    IReadOnlyList<string> StandingsSummary,
    IReadOnlyList<string> MoverSummary,
    IReadOnlyList<string> StreakSummary
)
{
    public string RenderPromptBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## COMPUTED LEAGUE FACTS (Strict Ground Truth — Do NOT recalculate or contradict)");
        sb.AppendLine($"- **Weekly High Score**: {HighScorerLine}");
        sb.AppendLine($"- **Weekly Low Score**: {LowScorerLine}");
        sb.AppendLine($"- **Narrowest Game Margin**: {NarrowestMarginLine}");
        sb.AppendLine("- **Top Performers of the Week**:");
        foreach (var p in TopPerformers)
            sb.AppendLine($"  - {p}");
        sb.AppendLine("- **Standings Movers**:");
        foreach (var m in MoverSummary)
            sb.AppendLine($"  - {m}");
        sb.AppendLine("- **Active Streaks**:");
        foreach (var s in StreakSummary)
            sb.AppendLine($"  - {s}");
        return sb.ToString();
    }
}

public sealed record ForecastMatchupFactCard(
    int? MatchupId,
    string HomeTeamName,
    string HomeOwnerRealName,
    decimal HomeProjectedScore,
    string AwayTeamName,
    string AwayOwnerRealName,
    decimal AwayProjectedScore,
    string PickTeamName,
    string PickOwnerRealName,
    decimal Margin,
    string Confidence,
    string KeyXFactor,
    IReadOnlyList<string> PlayerNotes
);

public sealed record ForecastFactCard(
    int NextWeek,
    IReadOnlyList<ForecastMatchupFactCard> MatchupForecasts
)
{
    public string RenderPromptBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<ground_truth_forecast>");
        sb.AppendLine($"## COMPUTED NEXT-WEEK FORECAST FACTS (Week {NextWeek} — Ground Truth)");
        sb.AppendLine("The following projections, picks, margins, confidence ratings, and key player factors were calculated deterministically in C# from player PPG averages, injury statuses, and bye weeks.");
        sb.AppendLine("You MUST use these EXACT picks, projected score lines, and confidence ratings in your `## Forecast` table. Do NOT recalculate score totals, flip picks, or change confidence ratings.");
        sb.AppendLine();
        sb.AppendLine("| Matchup | Pick | Projected Score | Confidence | X-Factor |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var m in MatchupForecasts)
        {
            var scoreLine = $"{m.HomeProjectedScore:F1} – {m.AwayProjectedScore:F1}";
            sb.AppendLine($"| {m.HomeTeamName} vs {m.AwayTeamName} | {m.PickTeamName} | {scoreLine} | {m.Confidence} | {m.KeyXFactor} |");
        }
        if (MatchupForecasts.Any(m => m.PlayerNotes.Count > 0))
        {
            sb.AppendLine();
            sb.AppendLine("### Key Player Factors & Injury/Bye Notes:");
            foreach (var m in MatchupForecasts)
            {
                if (m.PlayerNotes.Count > 0)
                {
                    sb.AppendLine($"- **{m.HomeTeamName} ({m.HomeOwnerRealName}) vs {m.AwayTeamName} ({m.AwayOwnerRealName})**:");
                    foreach (var note in m.PlayerNotes)
                    {
                        sb.AppendLine($"  - {note}");
                    }
                }
            }
        }
        sb.AppendLine("</ground_truth_forecast>");
        return sb.ToString();
    }
}

/// <summary>
/// Deterministic engine for computing unambiguous facts from a <see cref="RecapEnvelope"/>.
/// </summary>
public static class RecapFactEngine
{
    public static MatchupFactCard ComputeMatchupCard(
        RecapEnvelope env,
        GameRecap game,
        PriorForecast? priorForecast)
    {
        var homeOwner = env.Owners.FirstOrDefault(o => o.UserId == game.Home.UserId);
        var awayOwner = env.Owners.FirstOrDefault(o => o.UserId == game.Away.UserId);

        var homeRealName = homeOwner?.RealName ?? homeOwner?.DisplayName ?? game.Home.OwnerDisplay;
        var awayRealName = awayOwner?.RealName ?? awayOwner?.DisplayName ?? game.Away.OwnerDisplay;

        bool isTie = game.Home.FinalScore == game.Away.FinalScore;
        bool homeWon = game.Home.FinalScore > game.Away.FinalScore || isTie;

        var winnerSide = homeWon ? game.Home : game.Away;
        var loserSide = homeWon ? game.Away : game.Home;

        var winnerRealName = homeWon ? homeRealName : awayRealName;
        var loserRealName = homeWon ? awayRealName : homeRealName;

        // 1. Weekly Score Ranks
        var scoresOrdered = env.Games
            .SelectMany(g => new[] { (g.Home.UserId, g.Home.FinalScore), (g.Away.UserId, g.Away.FinalScore) })
            .OrderByDescending(x => x.FinalScore)
            .ToList();

        int winnerRank = scoresOrdered.FindIndex(x => x.UserId == winnerSide.UserId) + 1;
        int loserRank = scoresOrdered.FindIndex(x => x.UserId == loserSide.UserId) + 1;

        // 2. Standings & Record
        var winnerStandings = env.Standings.FirstOrDefault(s => s.UserId == winnerSide.UserId);
        var loserStandings = env.Standings.FirstOrDefault(s => s.UserId == loserSide.UserId);

        string winnerRecord = winnerStandings is not null ? $"{winnerStandings.Wins}-{winnerStandings.Losses}" : "N/A";
        string loserRecord = loserStandings is not null ? $"{loserStandings.Wins}-{loserStandings.Losses}" : "N/A";

        string winnerShift = FormatStandingsShift(winnerStandings);
        string loserShift = FormatStandingsShift(loserStandings);

        string winnerStreak = winnerStandings?.Streak ?? "N/A";
        string loserStreak = loserStandings?.Streak ?? "N/A";

        // 3. Top Performers
        var winnerKey = winnerSide.KeyPerformer;
        var loserKey = loserSide.KeyPerformer;

        string winnerTopStr = winnerKey is not null ? $"{winnerKey.FullName} ({winnerKey.Position ?? "FLEX"}, {winnerKey.Points:F2} pts)" : "N/A";
        string loserTopStr = loserKey is not null ? $"{loserKey.FullName} ({loserKey.Position ?? "FLEX"}, {loserKey.Points:F2} pts)" : "N/A";

        var allStarters = winnerSide.Starters.Concat(loserSide.Starters).OrderByDescending(p => p.Points).ToList();
        var topStarter = allStarters.FirstOrDefault();
        string topScorerInGame = topStarter is not null ? $"{topStarter.FullName} ({topStarter.Position ?? "FLEX"}, {topStarter.Points:F2} pts)" : "N/A";

        // 4. Optimality Rule Check (Gap >= 15%)
        decimal homeOpt = game.LineupOptimalityHomePct ?? 100m;
        decimal awayOpt = game.LineupOptimalityAwayPct ?? 100m;
        decimal optGap = Math.Abs(homeOpt - awayOpt);
        bool optExceedsThreshold = optGap >= 15.0m;
        string optimalityNote = optExceedsThreshold
            ? $"Gap is {optGap:F1}% ({winnerRealName}: {(homeWon ? homeOpt : awayOpt):F1}%, {loserRealName}: {(homeWon ? awayOpt : homeOpt):F1}%). Include ONE sentence on lineup optimality."
            : $"Gap is {optGap:F1}% (less than 15.0% threshold). DO NOT include a lineup-optimality paragraph or sentence.";

        // 5. Bench Flip / Coulda-Shoulda Reality
        decimal loserCouldaShoulda = loserSide.CouldaShouldaPoints ?? 0m;
        bool loserBenchFlipPossible = !isTie && loserCouldaShoulda > game.Margin;
        string loserBenchFlipDetails = isTie
            ? "The game ended tied; do not describe either team's bench as flipping a win or loss."
            : loserBenchFlipPossible
                ? $"{loserRealName}'s bench optimization gained {loserCouldaShoulda:F2} pts, which EXCEEDS the {game.Margin:F2} pt margin. A perfect lineup WOULD HAVE flipped the game."
                : $"{loserRealName}'s bench optimization gained {loserCouldaShoulda:F2} pts, but the margin was {game.Margin:F2} pts. A bench swap COULD NOT have flipped the win.";

        // 6. Mandatory Consequence Closer
        string consequenceCloser = isTie
            ? $"{winnerRealName} moved to {winnerRecord} ({winnerShift}) and {loserRealName} moved to {loserRecord} ({loserShift}) after the tie."
            : $"{winnerRealName} moved to {winnerRecord} ({winnerShift}), while {loserRealName} fell to {loserRecord} ({loserShift}).";

        // 7. Prior Forecast Grading
        string? priorGrading = null;
        if (priorForecast is not null)
        {
            bool pickWasCorrect = !isTie && (string.Equals(priorForecast.Pick, winnerRealName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(priorForecast.Pick, winnerSide.CurrentTeamName, StringComparison.OrdinalIgnoreCase));

            priorGrading = isTie
                ? $"Prior call picked {priorForecast.Pick}, but the game ended tied {winnerSide.FinalScore:F2}–{loserSide.FinalScore:F2}; the pick did not win."
                : pickWasCorrect
                ? $"Prior call correctly picked {priorForecast.Pick} (projected {priorForecast.ProjectedScore}). {winnerRealName} won {winnerSide.FinalScore:F2}–{loserSide.FinalScore:F2}."
                : $"Prior call INCORRECTLY picked {priorForecast.Pick}. {winnerRealName} pulled off the upset {winnerSide.FinalScore:F2}–{loserSide.FinalScore:F2}. Own the miss cleanly.";
        }

        return new MatchupFactCard(
            Week: env.Meta.Week,
            IsTie: isTie,
            WinnerRealName: winnerRealName,
            WinnerTeamName: winnerSide.CurrentTeamName,
            WinnerScore: winnerSide.FinalScore,
            WinnerScoreRankInWeek: winnerRank,
            WinnerNewRecord: winnerRecord,
            WinnerStandingsShift: winnerShift,
            WinnerStreak: winnerStreak,
            LoserRealName: loserRealName,
            LoserTeamName: loserSide.CurrentTeamName,
            LoserScore: loserSide.FinalScore,
            LoserScoreRankInWeek: loserRank,
            LoserNewRecord: loserRecord,
            LoserStandingsShift: loserShift,
            LoserStreak: loserStreak,
            Margin: game.Margin,
            IsBlowout: game.Blowout,
            TopScorerInGame: topScorerInGame,
            WinnerTopScorer: winnerTopStr,
            LoserTopScorer: loserTopStr,
            LineupOptimalityGapExceedsThreshold: optExceedsThreshold,
            LineupOptimalityNote: optimalityNote,
            LoserBenchFlipPossible: loserBenchFlipPossible,
            LoserBenchFlipDetails: loserBenchFlipDetails,
            ExactConsequenceCloser: consequenceCloser,
            PriorForecastGradingNote: priorGrading
        );
    }

    public static LeagueFactCard ComputeLeagueCard(RecapEnvelope env)
    {
        var highScorer = env.Themes.HighestScore;
        string highStr = highScorer is not null
            ? $"{GetRealName(env, highScorer.UserId)} ({highScorer.TeamName}) with {highScorer.Score:F2} pts"
            : "N/A";

        var lowScorer = env.Themes.LowestScore;
        string lowStr = lowScorer is not null
            ? $"{GetRealName(env, lowScorer.UserId)} ({lowScorer.TeamName}) with {lowScorer.Score:F2} pts"
            : "N/A";

        var narrow = env.Themes.NarrowestMargin;
        string narrowStr = narrow is not null
            ? $"{GetRealName(env, narrow.OwnerA)} vs {GetRealName(env, narrow.OwnerB)}: {narrow.ScoreA:F2} to {narrow.ScoreB:F2} (margin: {Math.Abs(narrow.ScoreA - narrow.ScoreB):F2} pts)"
            : "N/A";

        var topPerformers = env.Themes.TopFivePerformers
            .Select((p, idx) => $"{idx + 1}. {p.FullName} ({p.Position ?? "FLEX"}, {p.RealNflTeam}) — {p.Points:F2} pts ({GetRealName(env, p.OwnedByUserId)})")
            .ToList();

        var standingsSummary = env.Standings
            .Select(s => $"#{s.Rank} {GetRealName(env, s.UserId)} ({s.TeamName}): {s.Wins}-{s.Losses}, {s.PointsFor:F2} PF ({s.Streak})")
            .ToList();

        var moverSummary = env.PreviouslyOnLeague?.RankShifts
            .Select(r => $"{r.OwnerRealName} ({r.TeamName}): {(r.Delta > 0 ? $"Climbed +{r.Delta} to #{r.ToRank}" : $"Fell {r.Delta} to #{r.ToRank}")}")
            .ToList() ?? new List<string>();

        var streakSummary = env.PreviouslyOnLeague?.StreakEvents
            .Select(s => $"{s.OwnerRealName} ({s.TeamName}): {s.Kind} streak {s.Streak}")
            .ToList() ?? new List<string>();

        return new LeagueFactCard(
            Week: env.Meta.Week,
            TotalTeams: env.Meta.TotalTeams,
            HighScorerLine: highStr,
            LowScorerLine: lowStr,
            NarrowestMarginLine: narrowStr,
            TopPerformers: topPerformers,
            StandingsSummary: standingsSummary,
            MoverSummary: moverSummary,
            StreakSummary: streakSummary
        );
    }

    public static ForecastFactCard ComputeForecastCard(RecapEnvelope env)
    {
        var nextWeek = env.LookAhead.NextWeek;
        var list = new List<ForecastMatchupFactCard>();

        foreach (var m in env.LookAhead.Matchups)
        {
            var homeOwner = env.Owners.FirstOrDefault(o => o.UserId == m.HomeUserId);
            var awayOwner = env.Owners.FirstOrDefault(o => o.UserId == m.AwayUserId);

            var homeName = homeOwner?.RealName ?? homeOwner?.DisplayName ?? m.HomeOwnerDisplay;
            var awayName = awayOwner?.RealName ?? awayOwner?.DisplayName ?? m.AwayOwnerDisplay;

            decimal hProj = m.HomeProjection ?? 100m;
            decimal aProj = m.AwayProjection ?? 100m;

            var pickTeam = m.Pick ?? (hProj >= aProj ? m.HomeTeamName : m.AwayTeamName);
            var pickOwner = string.Equals(pickTeam, m.HomeTeamName, StringComparison.OrdinalIgnoreCase) ? homeName : awayName;

            var margin = Math.Round(Math.Abs(hProj - aProj), 1);
            var confidence = m.Confidence ?? (margin >= 20.0m ? "Lock" : (margin >= 6.0m ? "Lean" : "Coin Flip"));
            var keyXFactor = m.KeyXFactor ?? $"Spread: {margin:F1} pts ({confidence})";
            var playerNotes = m.PlayerNotes ?? [];

            list.Add(new ForecastMatchupFactCard(
                MatchupId: m.MatchupId,
                HomeTeamName: m.HomeTeamName,
                HomeOwnerRealName: homeName,
                HomeProjectedScore: hProj,
                AwayTeamName: m.AwayTeamName,
                AwayOwnerRealName: awayName,
                AwayProjectedScore: aProj,
                PickTeamName: pickTeam,
                PickOwnerRealName: pickOwner,
                Margin: margin,
                Confidence: confidence,
                KeyXFactor: keyXFactor,
                PlayerNotes: playerNotes));
        }

        return new ForecastFactCard(nextWeek, list);
    }

    private static string FormatStandingsShift(StandingsRow? row)
    {
        if (row is null) return "N/A";
        if (row.RankDelta is null || row.RankDelta == 0) return $"Rank #{row.Rank}";
        return row.RankDelta > 0
            ? $"Climbed +{row.RankDelta} to #{row.Rank}"
            : $"Fell {row.RankDelta} to #{row.Rank}";
    }

    private static string GetRealName(RecapEnvelope env, string userId)
    {
        var owner = env.Owners.FirstOrDefault(o => o.UserId == userId);
        return owner?.RealName ?? owner?.DisplayName ?? userId;
    }
}
