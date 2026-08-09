namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Season-long aggregate built once at season's end. Persisted as
/// <c>recaps/{season}/season-aggregate.json</c> so future agents (next year's
/// preview, multi-year comparisons) can re-ingest it without scraping prose.
/// </summary>
/// <remarks>
/// Hard rule: every output of the season-recap pipeline must be re-ingestible
/// by future agents. SVGs get this JSON sidecar; awards live in
/// <see cref="SeasonAwards"/>; final placements/draft order in
/// <see cref="SeasonOutcome"/>; and a top-level <c>manifest.json</c> indexes
/// everything.
/// </remarks>
public sealed record SeasonAggregate(
    int Season,
    string LeagueId,
    string LeagueName,
    DateTimeOffset GeneratedAt,
    LeagueSchedule Schedule,
    List<OwnerRef> Owners,                          // same shape as RecapEnvelope.Owners
    List<SeasonTeamSeries> Teams,                   // one per owner; per-week series
    SeasonHighlights Highlights,                    // single-week highs/lows + biggest upsets
    List<WeeklyChampion> WeeklyChampions,           // one entry per week (highest scorer)
    SeasonOutcome? Outcome                          // final placements + next year draft picks (null if season not finished)
);

/// <summary>
/// Per-team weekly series. Aligned by <see cref="WeeklyEntry.Week"/>; not all
/// teams have entries for every week (a team that wasn't in the consolation
/// bracket won't have W16/W17 entries on the losers side, but every active
/// team has W1..W15 regular-season entries).
/// </summary>
public sealed record SeasonTeamSeries(
    int RosterId,
    string UserId,
    string OwnerRealName,                           // resolved from lore (display name fallback)
    string FinalTeamName,                           // team name as of the final week
    int Generation,                                 // 1 or 2 (from lore)
    List<WeeklyEntry> Weekly,                       // one per played week
    int RegularSeasonWins,
    int RegularSeasonLosses,
    int RegularSeasonTies,
    decimal RegularSeasonPointsFor,
    decimal RegularSeasonPointsAgainst,
    int TotalWins,                                  // includes playoff/consolation games
    int TotalLosses,
    int TotalTies,
    decimal TotalPointsFor,                         // includes playoff/consolation PF
    decimal TotalPointsAgainst,
    decimal SeasonHighScore,                        // best single-week PF
    int SeasonHighScoreWeek,
    decimal SeasonLowScore,
    int SeasonLowScoreWeek,
    int LongestWinStreak,
    int LongestLossStreak,
    int? FinalPlace,                                // 1..N when SeasonOutcome resolved
    int? NextYearDraftPick                          // 1..N (1 = consolation winner / 1.01)
);

public sealed record WeeklyEntry(
    int Week,
    decimal PointsFor,
    decimal PointsAgainst,
    int? Result,                                    // 1 = win, 0 = tie, -1 = loss; null = bye/no game
    int? PowerRank,                                 // from power-history.json (1..N)
    int? ScoreRank,                                 // 1..N where 1 = highest scorer of that week
    int CumulativeWins,                             // wins through-and-including this week (regular season only)
    int CumulativeLosses,
    int CumulativeTies,
    string? CurrentTeamName,                        // as of this week (drives the team-name watch)
    decimal CumulativePointsFor,                    // running total PF through-and-including this week (regular season only; flat after week 15)
    decimal CumulativePointsAgainst,                // running total PA through-and-including this week (regular season only)
    decimal CumulativePointsDifferential            // CumulativePointsFor - CumulativePointsAgainst
);

public sealed record WeeklyChampion(
    int Week,
    string UserId,
    string OwnerRealName,
    string TeamName,
    decimal PointsFor
);

public sealed record SeasonHighlights(
    SingleScoreNote? HighestSingleWeek,             // anyone, any week
    SingleScoreNote? LowestSingleWeek,              // anyone, any week
    GameNote? BiggestBlowout,                       // largest margin
    GameNote? NarrowestWin,                         // smallest non-tie margin
    GameNote? BiggestUpset,                         // largest margin where loser had a better power rank that week
    SingleScoreNote? BestRegularSeasonPF,           // total regular-season PF leader
    SingleScoreNote? WorstRegularSeasonPF
);

public sealed record SingleScoreNote(
    int Week,                                       // 0 for season-aggregate notes
    string UserId,
    string OwnerRealName,
    string TeamName,
    decimal Value,
    string? Context                                 // optional one-line annotation
);

public sealed record GameNote(
    int Week,
    string WinnerUserId,
    string WinnerTeamName,
    string LoserUserId,
    string LoserTeamName,
    decimal WinnerScore,
    decimal LoserScore,
    decimal Margin,
    int? WinnerPowerRankAtTime,                     // for upset detection
    int? LoserPowerRankAtTime
);

/// <summary>
/// Deterministic awards. The agent's job is to NARRATE these, never to pick
/// them — so MVP / Bust of the Year / etc. stay reproducible and queryable.
/// </summary>
public sealed record SeasonAwards(
    int Season,
    string LeagueId,
    DateTimeOffset GeneratedAt,
    List<SeasonAward> Awards
);

public sealed record SeasonAward(
    string Name,                                    // "MVP", "Bust of the Year", "Comeback Team", "Trade That Mattered", "Waiver of the Year", "Best Team-Name Change", "Heartbreak of the Year"
    string Description,                             // 1-line, deterministic description of what this award measures
    string? UserId,                                 // owner this award goes to (when applicable)
    string? OwnerRealName,
    string? TeamName,
    string? PlayerId,                               // when the award is about a player (MVP, Bust, Waiver)
    string? PlayerName,
    string Metric,                                  // metric used to pick the winner (e.g., "season-long fantasy points started", "biggest power-rank climb between W6 and W12")
    decimal? Value,                                 // numeric value of the metric
    int? Week,                                      // when the award is tied to a single week
    string? Citation                                // free-form 1-line citation (e.g., "Acquired W4, scored 187.3 over the rest of the season")
);

/// <summary>
/// Index of every machine-readable artifact for a season. Future preview
/// agents bootstrap by reading this file, then loading the artifacts they
/// need.
/// </summary>
public sealed record SeasonManifest(
    int Season,
    string LeagueId,
    string LeagueName,
    DateTimeOffset GeneratedAt,
    List<ManifestArtifact> Artifacts
);

public sealed record ManifestArtifact(
    string Path,                                    // workspace-relative path
    string Kind,                                    // "season-aggregate" | "season-awards" | "season-outcome" | "season-recap" | "weekly-recap" | "power-history" | "team-name-history" | "chart-svg"
    string ContentType,                             // "application/json" | "text/markdown" | "image/svg+xml"
    string Description
);
