using System.Text.Json.Serialization;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// The complete data envelope handed to the AI analyst agents.
/// Designed to be the *ideal* payload — fields we don't have natively
/// are populated when possible and flagged via <see cref="AgentFetchHints"/>
/// when not (so the researcher agent web-searches them rather than the
/// analyst hallucinating).
/// </summary>
public sealed record RecapEnvelope(
    RecapMeta Meta,
    string LoreMarkdown,
    List<OwnerRef> Owners,
    List<StandingsRow> Standings,
    List<TeamNameChange> TeamNameWatch,
    List<GameRecap> Games,
    LeagueThemes Themes,
    LookAhead LookAhead,
    List<PriorRecap> PriorRecaps,
    List<string> AgentFetchHints,
    PlayoffPicture? PlayoffPicture = null,
    List<PowerRankingRow>? PowerRankings = null,
    SeasonLedger? SeasonLedger = null,
    LeagueSchedule? Schedule = null,
    WeeklyTheme? WeeklyTheme = null,
    PreviouslyOnLeague? PreviouslyOnLeague = null,
    List<string>? BannedPhrases = null,
    SeasonOutcome? SeasonOutcome = null
);

public sealed record RecapEnvelopeBuildOptions(bool PersistSnapshots = true);

/// <summary>
/// Final season standings + next year's draft order, populated only on the championship week
/// (week 17) once both brackets have produced winners. Drives the special end-of-season recap
/// composition: replaces the in-season Standings + Power Rankings with a single "Final Standings &amp;
/// Next Year's Draft Order" table, and tells the league prompt to write a season-in-review intro
/// (crowning the champion) and look-ahead-to-next-year storylines.
/// </summary>
public sealed record SeasonOutcome(
    SeasonPlacement Champion,                 // 1st — pick 1.08
    SeasonPlacement RunnerUp,                 // 2nd — pick 1.07
    SeasonPlacement ThirdPlace,               // 3rd — pick 1.06
    SeasonPlacement FourthPlace,              // 4th — pick 1.05 (last team in the winners bracket)
    SeasonPlacement ConsolationFifth,         // 5th — pick 1.01 (consolation-bowl winner)
    SeasonPlacement ConsolationSixth,         // 6th — pick 1.02
    SeasonPlacement ConsolationSeventh,       // 7th — pick 1.03
    SeasonPlacement ConsolationLast,          // 8th — pick 1.04 (forfeits one keeper next year)
    string LastPlaceKeeperPenalty             // Always: "Forfeits one keeper next year (3 of 4)."
);

public sealed record SeasonPlacement(
    int FinalPlace,        // 1..8 (1 = champion)
    int DraftPick,         // 1..8 (next year's first-round pick number)
    string UserId,
    string OwnerRealName,
    string TeamName,
    int RegularSeasonWins,
    int RegularSeasonLosses,
    int RegularSeasonTies,
    decimal RegularSeasonPointsFor,
    string Path            // "Won championship" | "Lost in championship" | "Won 3rd-place game" | "Lost 3rd-place game" | "Won consolation final" | "Lost consolation final" | "Won 7th-place game" | "Lost 7th-place game (last)"
);

/// <summary>
/// Per-week tone anchor loaded from data/weekly-themes.json. Drives the Intro voice
/// and may lift specific entries from <see cref="RecapEnvelope.BannedPhrases"/> for the week.
/// </summary>
public sealed record WeeklyTheme(
    int Week,
    string Title,
    string Vibe,
    List<string> BanLifts
);

/// <summary>
/// Cross-week narrative state — what changed since last week's recap. Powers the
/// "Previously" thread the League prompt uses to advance the storyline rather than
/// re-introduce it. All fields are derived from the current and prior week's
/// envelope; nothing here comes from a prompt or a model.
/// </summary>
public sealed record PreviouslyOnLeague(
    int PriorWeek,
    string? PriorWeekHeadline,             // e.g. "Brian's First in and First Out? extends winning streak to seven"
    List<RankShift> RankShifts,            // standings movers (delta >= 1)
    List<RankShift> PowerShifts,           // power-rank movers (delta >= 3)
    List<StreakEvent> StreakEvents,        // streaks that started, extended, or ended this week
    List<string> Notes                     // free-form one-liners ("Disappointment leapfrogged Sanders Boutte for the #3 seed")
);

public sealed record RankShift(
    string TeamName,
    string OwnerRealName,
    int FromRank,
    int ToRank,
    int Delta                              // positive = climbed
);

public sealed record StreakEvent(
    string TeamName,
    string OwnerRealName,
    string Kind,                           // "extended" | "started" | "ended"
    string Streak,                         // current streak label, e.g. "W7" or "L0 (broken)"
    int Length
);

/// <summary>
/// The actual played schedule of this league. Hardcoded per-league because
/// Sleeper's <c>playoff_week_start</c> setting does not match how this league
/// is run (regular season ends week 15; playoffs are weeks 16-17 only;
/// there is no week 18).
/// </summary>
public sealed record LeagueSchedule(
    int RegularSeasonLastWeek,   // last regular-season game week (inclusive)
    int PlayoffStartWeek,         // first playoff round
    int ChampionshipWeek,         // final / championship week
    int TotalWeeks                // last legal week (no recaps beyond this)
);

public sealed record RecapMeta(
    string LeagueId,
    string LeagueName,
    int Season,
    int Week,
    string SeasonType,        // "regular" | "playoffs_winners" | "playoffs_losers" | "consolation"
    string? PlayoffRound,     // e.g. "Quarterfinal", "Semifinal", "Championship", "3rd-place"
    bool IsFinalWeek,
    int TotalTeams,
    string ScoringSummary,    // e.g. "0.5 PPR, 6pt passing TD"
    DateTimeOffset GeneratedAt
);

public sealed record OwnerRef(
    string UserId,
    string Username,
    string DisplayName,
    string TeamName,
    int RosterId,
    int Generation,           // 1 or 2 (from lore); 0 if unknown
    string? RealName,
    string? LoreNotes
);

public sealed record StandingsRow(
    int Rank,
    int? RankDelta,           // change vs last week (positive = climbed); null if unknown
    string UserId,
    string OwnerDisplay,
    string TeamName,
    int Wins,
    int Losses,
    int Ties,
    decimal PointsFor,
    decimal PointsAgainst,
    decimal PointsForDiff,
    string Streak,            // "W3", "L1", etc.
    int? WaiverBudgetRemaining,
    int? TotalMoves
);

public sealed record TeamNameChange(
    string UserId,
    string OwnerDisplay,
    string PreviousName,
    string NewName,
    int WeekChanged
);

public sealed record GameRecap(
    int? MatchupId,
    string SeasonContext,         // "regular" | "playoffs_winners" | "playoffs_losers" | "consolation"
    string? PlayoffRound,         // e.g. "Semifinal" when applicable
    GameSide Home,
    GameSide Away,
    decimal Margin,
    bool Blowout,
    decimal? LineupOptimalityHomePct,
    decimal? LineupOptimalityAwayPct,
    string? StoryHookType,        // resolved from lore: father_son, brother, etc.
    string? StoryHookLabel,       // human-friendly label
    int StoryImportance,          // 1-5; family relationship framing is only allowed when >= 4
    string? StoryImportanceReason,// short explanation of why this game scored what it did
    HeadToHeadHistory? H2H
);

public sealed record GameSide(
    int RosterId,
    string UserId,
    string OwnerDisplay,
    string CurrentTeamName,
    string? PreviousTeamName,     // when changed this week
    decimal FinalScore,
    decimal? ProjectedScore,      // sum of pre-game projections (rolling 4-week PPG)
    List<PlayerLine> Starters,
    List<PlayerLine> Bench,
    PlayerLine? KeyPerformer,
    PlayerLine? BiggestBust,
    decimal? CouldaShouldaPoints  // total bench points that beat a starter at same position
);

public sealed record PlayerLine(
    string PlayerId,
    string FullName,
    string? Position,
    string? RealNflTeam,
    string? RealOpponent,
    string? KickoffSlot,          // "Thu" | "Sun-early" | "Sun-late" | "SNF" | "MNF" | "Sat" | null
    decimal Points,
    decimal? ProjectedPoints,
    decimal? ProjectionDelta,     // Points - ProjectedPoints
    bool BoomFlag,                // Points >= 2x projection (when projection available)
    bool BustFlag,                // Points <= 0.5x projection (when projection available)
    // Stat-line context (nullable when not available for the position)
    int? Targets,
    int? Receptions,
    decimal? ReceivingYards,
    int? ReceivingTds,
    int? Carries,
    decimal? RushingYards,
    int? RushingTds,
    decimal? PassingYards,
    int? PassingTds,
    int? PassingInterceptions,
    decimal? FantasyPoints        // raw nflverse fantasy_points (for cross-check)
);

public sealed record HeadToHeadHistory(
    int Wins,                     // for the Home side
    int Losses,
    int Ties,
    string? LastMeetingNote        // brief recall from prior recap, when available
);

public sealed record LeagueThemes(
    GameSideRef? HighestScore,
    GameSideRef? LowestScore,
    GameRef? NarrowestMargin,
    List<TopPerformer> TopFivePerformers,
    List<WaiverGrade> WaiverGrades,
    List<TradeSummary> Trades,
    List<string> StatLineOddities  // free-form callouts the builder identifies
);

public sealed record GameSideRef(string UserId, string OwnerDisplay, string TeamName, decimal Score);
public sealed record GameRef(int? MatchupId, string OwnerA, string OwnerB, decimal ScoreA, decimal ScoreB);

public sealed record TopPerformer(
    string PlayerId,
    string FullName,
    string? Position,
    string? RealNflTeam,
    decimal Points,
    string OwnedByUserId,
    string OwnedByDisplay,
    bool Started
);

public sealed record WaiverGrade(
    string TransactionId,
    string Status,                 // "complete" | "failed"
    string ClaimingUserId,
    string ClaimingDisplay,
    string PlayerId,
    string PlayerName,
    string? Position,
    int? FaabBid,
    decimal? WeekPoints,           // points scored this week by the claimed player
    string? DroppedPlayerId,
    string? DroppedPlayerName
);

public sealed record TradeSummary(
    string TransactionId,
    List<TradeSide> Sides,
    DateTimeOffset CompletedAt
);

public sealed record TradeSide(
    string UserId,
    string OwnerDisplay,
    List<string> ReceivedPlayers,         // FullName "(POS)"
    List<string> ReceivedDraftPicks,      // "2025 R3 (from owner X)"
    int FaabReceived,
    decimal? WeekPointsFromReceived       // points scored this week by received players (when applicable)
);

public sealed record LookAhead(
    int NextWeek,
    List<NextWeekMatchup> Matchups,
    List<PivotPlayer> PivotPlayers,
    List<string> OpenQuestions
);

public sealed record NextWeekMatchup(
    int? MatchupId,
    string HomeUserId,
    string HomeOwnerDisplay,
    string HomeTeamName,
    string AwayUserId,
    string AwayOwnerDisplay,
    string AwayTeamName,
    decimal? HomeProjection,
    decimal? AwayProjection,
    decimal? PowerRankingGap,
    string? StoryHookType,
    string? StoryHookLabel,
    SeedImplication? SeedImplication
);

public sealed record SeedImplication(
    string HomeUserId,
    string IfHomeWinsSeed,        // e.g. "stays #2"
    string IfHomeLosesSeed,       // e.g. "drops to #5"
    string AwayUserId,
    string IfAwayWinsSeed,
    string IfAwayLosesSeed
);

public sealed record PivotPlayer(
    string PlayerId,
    string FullName,
    string? Position,
    string OwnedByUserId,
    string OwnedByDisplay,
    string Reason                  // "high variance starter", "bye-week landmine", etc.
);

public sealed record PriorRecap(
    int Week,
    string Mode,                   // "verbatim" | "summary"
    string Content                 // full markdown when verbatim; bullet summary when summary
);

// ---------------- Feature: Power Rankings ----------------

public sealed record PowerRankingRow(
    int Rank,
    int? RankDelta,                // movement vs last week (positive = climbed); null if no prior data
    string UserId,
    string OwnerDisplay,
    string TeamName,
    decimal PowerScore,            // 0..100, higher is better
    decimal Last3WeekAvgPf,        // average points scored over the last 3 played weeks
    string Trend                   // "▲▲" hot, "▲" warming, "—" steady, "▽" cooling, "▽▽" cold
);

// ---------------- Feature: Playoff Picture & Stakes ----------------

public sealed record PlayoffPicture(
    string Phase,                  // "regular" | "bubble" | "playoffs" | "championship_week"
    int PlayoffWeekStart,          // e.g. 15
    int PlayoffTeams,              // top-N qualify
    List<BracketSpot> WinnersBracketProjection,    // top-N teams (ordered by current seed)
    List<BracketSpot> ConsolationBracketProjection,// remaining teams
    List<string> KeeperRules,      // human-readable bullet list of season-end consequences
    List<StakesNote> WeekStakes    // matchup-specific stakes for the current week (regular- or playoff-season)
);

public sealed record BracketSpot(
    int Seed,
    string UserId,
    string OwnerDisplay,
    string TeamName,
    int Wins,
    int Losses,
    int Ties,
    decimal PointsFor,
    string Status                  // "clinched", "in", "bubble", "out"
);

public sealed record StakesNote(
    int? MatchupId,
    string Headline,               // short label (e.g. "Win-and-in for the #4 seed", "Consolation final — 1.01 on the line", "Avoid last → keeper safe")
    string Detail                  // 1-sentence explanation
);

// ---------------- Feature: Season Ledger (streaks + trends) ----------------

public sealed record SeasonLedger(
    List<StreakNote> WinStreaks,           // teams currently on W2+
    List<StreakNote> LossStreaks,          // teams currently on L2+
    List<TrendNote> HotTrends,             // teams whose 3-week PF avg is well above their season avg
    List<TrendNote> ColdTrends,            // teams whose 3-week PF avg is well below their season avg
    List<string> Highlights                // free-form one-liners ("X has scored 150+ four straight weeks")
);

public sealed record StreakNote(
    string UserId,
    string OwnerDisplay,
    string TeamName,
    int Length,
    string Kind                            // "W" or "L"
);

public sealed record TrendNote(
    string UserId,
    string OwnerDisplay,
    string TeamName,
    decimal SeasonAvgPf,
    decimal Last3AvgPf,
    decimal DeltaPct                       // (last3 - season) / season * 100
);
