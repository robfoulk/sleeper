using System.Text.Json;
using Sleeper.Api;
using Sleeper.Api.Models;
using Sleeper.Api.NflData;
using Sleeper.Api.NflData.Models;
using Sleeper.Api.Services;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Builds the <see cref="RecapEnvelope"/> handed to the AI analyst agents.
/// Pulls everything from the existing Sleeper / nflverse clients; no new
/// network endpoints. Designed to be deterministic — no randomness, no LLM
/// calls.
/// </summary>
internal sealed class RecapEnvelopeBuilder
{
    private readonly ISleeperClient _sleeper;
    private readonly ISleeperService _sleeperService;
    private readonly INflDataClient _nfl;
    private readonly LeagueLore _lore;

    /// <summary>Threshold for the boom flag (multiplier of projection).</summary>
    private const decimal BoomMultiplier = 2.0m;

    /// <summary>Threshold for the bust flag (multiplier of projection).</summary>
    private const decimal BustMultiplier = 0.5m;

    /// <summary>Margin in points above which we flag a game as a blowout.</summary>
    private const decimal BlowoutMarginPoints = 30m;

    /// <summary>
    /// Hardcoded league schedule. Sleeper's <c>playoff_week_start</c> setting
    /// does not match how this league is actually played; the regular season
    /// runs through week 15, the playoffs are weeks 16 (semifinal/round 1) and
    /// 17 (championship), and there is no week 18.
    /// </summary>
    public static readonly LeagueSchedule Schedule = new(
        RegularSeasonLastWeek: 15,
        PlayoffStartWeek: 16,
        ChampionshipWeek: 17,
        TotalWeeks: 17);

    public RecapEnvelopeBuilder(
        ISleeperClient sleeper,
        ISleeperService sleeperService,
        INflDataClient nfl,
        LeagueLore lore)
    {
        _sleeper = sleeper;
        _sleeperService = sleeperService;
        _nfl = nfl;
        _lore = lore;
    }

    public async Task<RecapEnvelope> BuildAsync(
        string leagueId,
        int week,
        int? overrideSeason,
        CancellationToken ct = default,
        RecapEnvelopeBuildOptions? options = null)
    {
        options ??= new RecapEnvelopeBuildOptions();

        // 1. Pull league metadata, rosters, users, matchups, transactions in parallel.
        var leagueTask = _sleeper.GetLeagueAsync(leagueId, ct);
        var rostersTask = _sleeper.GetLeagueRostersAsync(leagueId, ct);
        var usersTask = _sleeper.GetLeagueUsersAsync(leagueId, ct);
        var matchupsTask = _sleeper.GetLeagueMatchupsAsync(leagueId, week, ct);
        var transactionsTask = _sleeper.GetTransactionsAsync(leagueId, week, ct);
        var winnersBracketTask = _sleeper.GetWinnersBracketAsync(leagueId, ct);
        var losersBracketTask = _sleeper.GetLosersBracketAsync(leagueId, ct);
        var allPlayersTask = _sleeper.GetAllPlayersAsync("nfl", ct);

        await Task.WhenAll(
            leagueTask, rostersTask, usersTask, matchupsTask, transactionsTask,
            winnersBracketTask, losersBracketTask, allPlayersTask).ConfigureAwait(false);

        var league = leagueTask.Result ?? throw new InvalidOperationException($"League {leagueId} not found.");
        var rosters = rostersTask.Result;
        var users = usersTask.Result;
        var matchups = matchupsTask.Result;
        var transactions = transactionsTask.Result;
        var winnersBracket = winnersBracketTask.Result;
        var losersBracket = losersBracketTask.Result;
        var players = allPlayersTask.Result;

        // 2. Determine season + season-type/playoff round.
        var season = overrideSeason ??
                     (int.TryParse(league.Season, out var parsed) ? parsed : DateTime.UtcNow.Year);

        var (seasonType, playoffRound, isFinalWeek) = ClassifyWeek(league, week, winnersBracket, losersBracket);

        // 3. Pull season-long weekly stats and filter to this week.
        Dictionary<string, List<WeeklyPlayerStats>> weeklyBySleeper;
        try
        {
            weeklyBySleeper = await _nfl.GetWeeklyStatsBySleeperIdAsync(season, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (warning: nflverse weekly stats unavailable for {season}: {ex.Message})");
            weeklyBySleeper = new();
        }

        // 4. Resolve owner refs (lore-merged).
        var owners = BuildOwnerRefs(rosters, users);
        var ownerByRosterId = owners.ToDictionary(o => o.RosterId, o => o);

        // 5. Standings (reconstructed as-of-week-N from per-week matchups).
        var allWeekMatchups = new Dictionary<int, List<Matchup>> { [week] = matchups };
        for (int w = 1; w < week; w++)
        {
            try
            {
                allWeekMatchups[w] = await _sleeper.GetLeagueMatchupsAsync(leagueId, w, ct).ConfigureAwait(false);
            }
            catch { allWeekMatchups[w] = []; }
        }
        var standings = BuildStandingsAsOfWeek(rosters, owners, allWeekMatchups, week);
        var perWeekPfByRoster = ComputePerWeekPfByRoster(rosters, allWeekMatchups, week);
        var streakByRoster = ComputeStreakByRoster(rosters, allWeekMatchups, week);
        // Apply streak strings into standings rows.
        standings = standings
            .Select(s =>
            {
                var rid = rosters.FirstOrDefault(r => r.OwnerId == s.UserId)?.RosterId;
                var streak = rid is null ? "" : streakByRoster.GetValueOrDefault(rid.Value, "");
                return s with { Streak = streak };
            })
            .ToList();

        // 6. Per-game recaps.
        var leagueConfig = LeagueRosterConfig.FromLeague(league);
        int playoffTeams = 4;
        if (league.Settings is not null && league.Settings.TryGetValue("playoff_teams", out var ptVal) && ptVal.ValueKind == JsonValueKind.Number)
            playoffTeams = ptVal.GetInt32();
        var games = BuildGames(matchups, ownerByRosterId, players, weeklyBySleeper, week, leagueConfig, seasonType, playoffRound, standings, playoffTeams);
        // 6b. Per-game playoff labels: in playoff weeks each matchup has a specific role
        // (Championship, 3rd-place game, Consolation final, 7th-place game, Semifinal). Override
        // the envelope-level PlayoffRound on each game so the per-game prompt knows the truth.
        games = ApplyBracketLabelsToGames(games, week, winnersBracket, losersBracket);

        // 7. League themes.
        var themes = BuildThemes(games, transactions, ownerByRosterId, players, weeklyBySleeper, week);

        // 8. Look-ahead.
        List<Matchup> nextWeekMatchups = [];
        if (!isFinalWeek)
        {
            try
            {
                nextWeekMatchups = await _sleeper.GetLeagueMatchupsAsync(leagueId, week + 1, ct).ConfigureAwait(false);
            }
            catch
            {
                // Schedule may not be available yet; that's fine.
            }
        }
        var lookAhead = BuildLookAhead(week, nextWeekMatchups, ownerByRosterId, players, weeklyBySleeper, standings, isFinalWeek);

        // 9. Agent fetch hints (the things the analyst should web-search).
        var hints = BuildAgentFetchHints(games, lookAhead, players);

        // 10. Prior recaps + team-name watch.
        var (priorRecaps, nameChanges) = LoadPriorRecapsAndNameWatch(season, week, owners);

        // 11. Build the meta + envelope.
        var meta = new RecapMeta(
            LeagueId: leagueId,
            LeagueName: league.Name ?? "Unnamed League",
            Season: season,
            Week: week,
            SeasonType: seasonType,
            PlayoffRound: playoffRound,
            IsFinalWeek: isFinalWeek,
            TotalTeams: league.TotalRosters,
            ScoringSummary: SummariseScoring(league),
            GeneratedAt: DateTimeOffset.UtcNow);

        // 11b. New feature blocks.
        var powerRankings = BuildPowerRankings(season, week, standings, perWeekPfByRoster, ownerByRosterId, options.PersistSnapshots);
        var playoffPicture = BuildPlayoffPicture(league, week, standings, ownerByRosterId, winnersBracket, losersBracket, matchups);
        var ledger = BuildSeasonLedger(standings, perWeekPfByRoster, streakByRoster, ownerByRosterId);
        var (weeklyTheme, bannedPhrases) = LoadWeeklyTheme(week);
        var previously = BuildPreviouslyOnLeague(season, week, standings, powerRankings, owners, perWeekPfByRoster, rosters, allWeekMatchups);
        var seasonOutcome = BuildSeasonOutcome(week, standings, owners, ownerByRosterId, winnersBracket, losersBracket);

        var envelope = new RecapEnvelope(
            Meta: meta,
            LoreMarkdown: _lore.RawMarkdown,
            Owners: owners,
            Standings: standings,
            TeamNameWatch: nameChanges,
            Games: games,
            Themes: themes,
            LookAhead: lookAhead,
            PriorRecaps: priorRecaps,
            AgentFetchHints: hints,
            PlayoffPicture: playoffPicture,
            PowerRankings: powerRankings,
            SeasonLedger: ledger,
            Schedule: Schedule,
            WeeklyTheme: weeklyTheme,
            PreviouslyOnLeague: previously,
            BannedPhrases: bannedPhrases,
            SeasonOutcome: seasonOutcome);

        // 12. Snapshot team names for next week's run.
        if (options.PersistSnapshots)
            SnapshotTeamNames(season, week, owners);

        return envelope;
    }

    // ---------------- Owners ----------------

    private List<OwnerRef> BuildOwnerRefs(List<Roster> rosters, List<LeagueUser> users)
    {
        var userById = users.ToDictionary(u => u.UserId, u => u);
        var result = new List<OwnerRef>();
        foreach (var r in rosters)
        {
            LeagueUser? user = r.OwnerId is not null && userById.TryGetValue(r.OwnerId, out var u) ? u : null;
            var username = user?.Username ?? "";
            var displayName = user?.DisplayName ?? username;
            // Sleeper's /league/{id}/users endpoint sometimes returns an empty `username` and only populates `display_name`.
            // Use the display name as a fallback key so lore lookups still resolve.
            var loreKey = string.IsNullOrWhiteSpace(username) ? displayName : username;
            string teamName = displayName;
            if (user?.Metadata is not null && user.Metadata.TryGetValue("team_name", out var tn) && !string.IsNullOrWhiteSpace(tn))
                teamName = tn;

            LoreOwner? lore = null;
            if (!string.IsNullOrWhiteSpace(loreKey))
                _lore.OwnersByUsername.TryGetValue(loreKey.ToLowerInvariant(), out lore);

            result.Add(new OwnerRef(
                UserId: r.OwnerId ?? "",
                Username: string.IsNullOrWhiteSpace(username) ? displayName : username,
                DisplayName: displayName,
                TeamName: teamName,
                RosterId: r.RosterId,
                Generation: lore?.Generation ?? 0,
                RealName: lore?.Name,
                LoreNotes: lore?.Notes));
        }
        return result;
    }

    // ---------------- Standings ----------------

    private static List<StandingsRow> BuildStandingsAsOfWeek(
        List<Roster> rosters,
        List<OwnerRef> owners,
        Dictionary<int, List<Matchup>> matchupsByWeek,
        int throughWeek)
    {
        var ownerByRosterId = owners.ToDictionary(o => o.RosterId, o => o);

        // Aggregate W/L/T + PF/PA for each roster across weeks 1..throughWeek.
        var agg = rosters.ToDictionary(r => r.RosterId, _ => new StandingsAccumulator());

        foreach (var (w, weekMatchups) in matchupsByWeek.OrderBy(kv => kv.Key))
        {
            if (w > throughWeek) continue;
            foreach (var grp in weekMatchups.GroupBy(m => m.MatchupId))
            {
                var pair = grp.ToList();
                if (pair.Count != 2) continue;
                var a = pair[0]; var b = pair[1];
                if (!agg.ContainsKey(a.RosterId) || !agg.ContainsKey(b.RosterId)) continue;
                if (!TryGetPlayedScores(a, b, out var aPts, out var bPts)) continue;
                agg[a.RosterId].PointsFor += aPts;
                agg[a.RosterId].PointsAgainst += bPts;
                agg[b.RosterId].PointsFor += bPts;
                agg[b.RosterId].PointsAgainst += aPts;
                if (aPts > bPts) { agg[a.RosterId].Wins++; agg[b.RosterId].Losses++; }
                else if (bPts > aPts) { agg[b.RosterId].Wins++; agg[a.RosterId].Losses++; }
                else { agg[a.RosterId].Ties++; agg[b.RosterId].Ties++; }
            }
        }

        // FAAB-used through week N is not directly available per week; fall back to current.
        return rosters
            .Select(r =>
            {
                var s = agg[r.RosterId];
                var owner = ownerByRosterId.GetValueOrDefault(r.RosterId);
                return new
                {
                    Roster = r,
                    Owner = owner,
                    Stats = s
                };
            })
            .OrderByDescending(x => x.Stats.Wins)
            .ThenByDescending(x => x.Stats.PointsFor)
            .Select((x, idx) => new StandingsRow(
                Rank: idx + 1,
                RankDelta: null,
                UserId: x.Owner?.UserId ?? "",
                OwnerDisplay: x.Owner?.DisplayName ?? $"Roster {x.Roster.RosterId}",
                TeamName: x.Owner?.TeamName ?? "",
                Wins: x.Stats.Wins,
                Losses: x.Stats.Losses,
                Ties: x.Stats.Ties,
                PointsFor: Math.Round(x.Stats.PointsFor, 2),
                PointsAgainst: Math.Round(x.Stats.PointsAgainst, 2),
                PointsForDiff: Math.Round(x.Stats.PointsFor - x.Stats.PointsAgainst, 2),
                Streak: "",
                WaiverBudgetRemaining: x.Roster.Settings is null ? null : 100 - x.Roster.Settings.WaiverBudgetUsed,
                TotalMoves: x.Roster.Settings?.TotalMoves))
            .ToList();
    }

    private sealed class StandingsAccumulator
    {
        public int Wins, Losses, Ties;
        public decimal PointsFor, PointsAgainst;
    }

    private static List<StandingsRow> BuildStandings(List<Roster> rosters, List<OwnerRef> owners)
    {
        var ownerById = owners.ToDictionary(o => o.RosterId, o => o);
        var rows = rosters
            .OrderByDescending(r => r.Settings?.Wins ?? 0)
            .ThenByDescending(r => (r.Settings?.Fpts ?? 0) + (r.Settings?.FptsDecimal ?? 0) / 100m)
            .Select((r, idx) =>
            {
                var s = r.Settings;
                var pf = (s?.Fpts ?? 0) + (s?.FptsDecimal ?? 0) / 100m;
                var pa = (s?.FptsAgainst ?? 0) + (s?.FptsAgainstDecimal ?? 0) / 100m;
                var owner = ownerById.GetValueOrDefault(r.RosterId);
                return new StandingsRow(
                    Rank: idx + 1,
                    RankDelta: null, // delta requires history we don't have yet
                    UserId: owner?.UserId ?? "",
                    OwnerDisplay: owner?.DisplayName ?? $"Roster {r.RosterId}",
                    TeamName: owner?.TeamName ?? "",
                    Wins: s?.Wins ?? 0,
                    Losses: s?.Losses ?? 0,
                    Ties: s?.Ties ?? 0,
                    PointsFor: Math.Round(pf, 2),
                    PointsAgainst: Math.Round(pa, 2),
                    PointsForDiff: Math.Round(pf - pa, 2),
                    Streak: "",
                    WaiverBudgetRemaining: s is null ? null : 100 - s.WaiverBudgetUsed,
                    TotalMoves: s?.TotalMoves);
            })
            .ToList();
        return rows;
    }

    // ---------------- Games ----------------

    private List<GameRecap> BuildGames(
        List<Matchup> matchups,
        Dictionary<int, OwnerRef> ownerByRosterId,
        Dictionary<string, Player> players,
        Dictionary<string, List<WeeklyPlayerStats>> weeklyBySleeper,
        int week,
        LeagueRosterConfig config,
        string seasonType,
        string? playoffRound,
        List<StandingsRow> standings,
        int playoffTeams)
    {
        var result = new List<GameRecap>();
        var grouped = matchups.Where(m => m.MatchupId.HasValue).GroupBy(m => m.MatchupId!.Value);
        foreach (var grp in grouped)
        {
            var teams = grp.ToList();
            if (teams.Count < 2) continue;
            var t1 = teams[0];
            var t2 = teams[1];

            var sideA = BuildGameSide(t1, ownerByRosterId, players, weeklyBySleeper, week, config);
            var sideB = BuildGameSide(t2, ownerByRosterId, players, weeklyBySleeper, week, config);

            var (home, away) = sideA.FinalScore >= sideB.FinalScore ? (sideA, sideB) : (sideB, sideA);
            var margin = Math.Round(Math.Abs(sideA.FinalScore - sideB.FinalScore), 2);

            var ownerA = ownerByRosterId.GetValueOrDefault(home.RosterId);
            var ownerB = ownerByRosterId.GetValueOrDefault(away.RosterId);
            (string Type, string Label)? hook = null;
            if (ownerA is not null && ownerB is not null)
                hook = _lore.ResolveHook(ownerA.Username, ownerB.Username);

            var (importance, importanceReason) = ComputeStoryImportance(
                home, away, week, seasonType, standings, playoffTeams);

            result.Add(new GameRecap(
                MatchupId: grp.Key,
                SeasonContext: seasonType,
                PlayoffRound: playoffRound,
                Home: home,
                Away: away,
                Margin: margin,
                Blowout: margin >= BlowoutMarginPoints,
                LineupOptimalityHomePct: ComputeOptimality(home, t1.RosterId == home.RosterId ? t1 : t2, players, config),
                LineupOptimalityAwayPct: ComputeOptimality(away, t1.RosterId == away.RosterId ? t1 : t2, players, config),
                StoryHookType: hook?.Type,
                StoryHookLabel: hook?.Label,
                StoryImportance: importance,
                StoryImportanceReason: importanceReason,
                H2H: null));
        }
        return result;
    }

    /// <summary>
    /// Scores a game's narrative importance on a 1-5 scale.
    /// Family-relationship framing (Brother Bowl, Father vs Son, Cousin Bowl, etc.)
    /// is only allowed when the score is >= 4 — otherwise the league is too tired
    /// of "we all know" to hear about it again.
    /// </summary>
    private static (int Importance, string Reason) ComputeStoryImportance(
        GameSide home,
        GameSide away,
        int week,
        string seasonType,
        List<StandingsRow> standings,
        int playoffTeams)
    {
        // Playoff games are always headline-importance.
        if (seasonType == "playoffs_winners")
        {
            // Championship rounds tend to be the final week of the season.
            if (week >= Schedule.ChampionshipWeek) return (5, "championship game");
            return (5, "playoff game");
        }
        if (seasonType == "playoffs_losers" || seasonType == "consolation")
        {
            if (week >= Schedule.ChampionshipWeek) return (4, "consolation final (1.01 on the line)");
            return (3, "consolation bracket game");
        }

        // Regular season: need standings to judge.
        var hRow = standings.FirstOrDefault(s => s.UserId == home.UserId);
        var aRow = standings.FirstOrDefault(s => s.UserId == away.UserId);

        // Final regular-season week — bracket locks tonight.
        if (week == Schedule.RegularSeasonLastWeek)
            return (4, "final regular-season week (bracket locks)");

        // Both undefeated after week 4 → marquee.
        if (week >= 4 && hRow is not null && aRow is not null
            && hRow.Losses == 0 && hRow.Ties == 0
            && aRow.Losses == 0 && aRow.Ties == 0)
            return (5, "both teams undefeated this late");

        // Top-of-table clash after week 6: both in top 2 OR one in #1 and the other within one game.
        if (week >= 6 && hRow is not null && aRow is not null)
        {
            int top1 = hRow.Rank == 1 || aRow.Rank == 1 ? 1 : 0;
            int top2 = (hRow.Rank <= 2 && aRow.Rank <= 2) ? 1 : 0;
            if (top2 == 1) return (4, "first-place clash (both teams in the top 2)");
            // Game between #1 and a 1-loss-or-fewer challenger
            if (top1 == 1)
            {
                var other = hRow.Rank == 1 ? aRow : hRow;
                if (other.Losses <= 1) return (4, "challenger taking on the #1 seed");
            }
        }

        // Bubble matchup in the last 2 weeks before the bracket locks.
        if (week >= Schedule.RegularSeasonLastWeek - 2 && week < Schedule.RegularSeasonLastWeek
            && hRow is not null && aRow is not null)
        {
            int line = playoffTeams; // last in vs first out
            bool hOnLine = hRow.Rank == line || hRow.Rank == line + 1;
            bool aOnLine = aRow.Rank == line || aRow.Rank == line + 1;
            if (hOnLine || aOnLine) return (4, "bubble watch — playoff seed on the line");
        }

        return (1, "regular weekly matchup");
    }

    private static GameSide BuildGameSide(
        Matchup m,
        Dictionary<int, OwnerRef> ownerByRosterId,
        Dictionary<string, Player> players,
        Dictionary<string, List<WeeklyPlayerStats>> weeklyBySleeper,
        int week,
        LeagueRosterConfig config)
    {
        var owner = ownerByRosterId.GetValueOrDefault(m.RosterId);
        var starters = new List<PlayerLine>();
        var bench = new List<PlayerLine>();
        var starterIds = new HashSet<string>(m.Starters ?? []);

        if (m.Players is not null)
        {
            foreach (var pid in m.Players)
            {
                if (string.IsNullOrEmpty(pid) || pid == "0") continue;
                var line = BuildPlayerLine(pid, m, players, weeklyBySleeper, week);
                if (starterIds.Contains(pid)) starters.Add(line);
                else bench.Add(line);
            }
        }

        var keyPerformer = starters.OrderByDescending(p => p.Points).FirstOrDefault();
        var biggestBust = starters
            .Where(p => p.ProjectedPoints.HasValue && p.ProjectedPoints.Value > 0)
            .OrderBy(p => p.Points - (p.ProjectedPoints ?? 0))
            .FirstOrDefault();

        // Coulda-shoulda: bench points exceeding the worst starter at the same fantasy position.
        decimal couldaShoulda = 0m;
        var startersByPos = starters.GroupBy(p => p.Position ?? "").ToDictionary(g => g.Key, g => g.OrderBy(x => x.Points).ToList());
        foreach (var b in bench)
        {
            var pos = b.Position ?? "";
            if (!startersByPos.TryGetValue(pos, out var sList) || sList.Count == 0) continue;
            var worstStarter = sList[0];
            if (b.Points > worstStarter.Points)
                couldaShoulda += b.Points - worstStarter.Points;
        }

        return new GameSide(
            RosterId: m.RosterId,
            UserId: owner?.UserId ?? "",
            OwnerDisplay: owner?.DisplayName ?? $"Roster {m.RosterId}",
            CurrentTeamName: owner?.TeamName ?? "",
            PreviousTeamName: null,
            FinalScore: Math.Round(m.ScoreOrZero(), 2),
            ProjectedScore: starters.Sum(s => s.ProjectedPoints) is decimal sum && sum > 0 ? Math.Round(sum, 2) : null,
            Starters: starters,
            Bench: bench,
            KeyPerformer: keyPerformer,
            BiggestBust: biggestBust,
            CouldaShouldaPoints: Math.Round(couldaShoulda, 2));
    }

    private static PlayerLine BuildPlayerLine(
        string playerId,
        Matchup m,
        Dictionary<string, Player> players,
        Dictionary<string, List<WeeklyPlayerStats>> weeklyBySleeper,
        int week)
    {
        players.TryGetValue(playerId, out var p);
        var fullName = p?.FullName ?? playerId;
        var pos = p?.Position;
        var realTeam = p?.Team;

        decimal points = 0;
        if (m.PlayersPoints is not null && m.PlayersPoints.TryGetValue(playerId, out var pts))
            points = pts;

        // Project from rolling 4-week PPG using nflverse weekly stats from prior weeks.
        decimal? projection = ComputeProjection(playerId, week, weeklyBySleeper);

        // This-week stat line.
        WeeklyPlayerStats? wk = null;
        if (weeklyBySleeper.TryGetValue(playerId, out var weeks))
            wk = weeks.FirstOrDefault(w => w.Week == week);

        var realOpp = wk?.OpponentTeam;

        var boom = projection is decimal proj && proj > 0 && points >= proj * BoomMultiplier;
        var bust = projection is decimal proj2 && proj2 > 0 && points <= proj2 * BustMultiplier;

        return new PlayerLine(
            PlayerId: playerId,
            FullName: fullName,
            Position: pos,
            RealNflTeam: realTeam,
            RealOpponent: realOpp,
            KickoffSlot: null,                 // We don't have an NFL schedule API; left for future enhancement.
            Points: Math.Round(points, 2),
            ProjectedPoints: projection,
            ProjectionDelta: projection.HasValue ? Math.Round(points - projection.Value, 2) : null,
            BoomFlag: boom,
            BustFlag: bust,
            Targets: wk?.Targets,
            Receptions: wk?.Receptions,
            ReceivingYards: wk?.ReceivingYards,
            ReceivingTds: wk?.ReceivingTds,
            Carries: wk?.Carries,
            RushingYards: wk?.RushingYards,
            RushingTds: wk?.RushingTds,
            PassingYards: wk?.PassingYards,
            PassingTds: wk?.PassingTds,
            PassingInterceptions: wk?.PassingInterceptions,
            FantasyPoints: wk?.FantasyPoints);
    }

    /// <summary>
    /// Rolling 4-week PPG projection from prior weeks of this season.
    /// Returns null when fewer than 1 prior week of data is available.
    /// </summary>
    private static decimal? ComputeProjection(string playerId, int week, Dictionary<string, List<WeeklyPlayerStats>> weekly)
    {
        if (week <= 1) return null;
        if (!weekly.TryGetValue(playerId, out var weeks) || weeks is null) return null;
        var prior = weeks
            .Where(w => w.Week < week && w.SeasonType == "REG" && w.FantasyPoints.HasValue)
            .OrderByDescending(w => w.Week)
            .Take(4)
            .ToList();
        if (prior.Count == 0) return null;
        return Math.Round(prior.Average(w => w.FantasyPoints!.Value), 2);
    }

    private static decimal? ComputeOptimality(GameSide side, Matchup _, Dictionary<string, Player> players, LeagueRosterConfig config)
    {
        // Approximate optimality: greedy fill of league slot map using all (starter+bench) players.
        // Slots: direct starters per position + flex.
        var pool = side.Starters.Concat(side.Bench).ToList();
        if (pool.Count == 0) return null;

        var slots = new List<string>();
        foreach (var (pos, count) in config.StarterSlots)
        {
            for (int i = 0; i < count; i++) slots.Add(pos);
        }
        for (int i = 0; i < config.FlexSlots; i++) slots.Add("FLEX");

        var byPos = pool.OrderByDescending(p => p.Points).ToList();
        var used = new HashSet<string>();
        decimal optimal = 0;

        // Direct positional slots first
        foreach (var slot in slots.Where(s => s != "FLEX"))
        {
            var pick = byPos.FirstOrDefault(p =>
                !used.Contains(p.PlayerId) && string.Equals(p.Position, slot, StringComparison.OrdinalIgnoreCase));
            if (pick is not null) { optimal += pick.Points; used.Add(pick.PlayerId); }
        }
        // Flex slots use the parsed league eligibility, e.g. WRRB_FLEX is RB/WR only.
        foreach (var slot in slots.Where(s => s == "FLEX"))
        {
            var pick = byPos.FirstOrDefault(p =>
                !used.Contains(p.PlayerId) && p.Position is not null && config.IsFlexEligible(p.Position));
            if (pick is not null) { optimal += pick.Points; used.Add(pick.PlayerId); }
        }

        if (optimal <= 0) return null;
        return Math.Round(side.FinalScore / optimal * 100m, 1);
    }

    // ---------------- Themes ----------------

    private static LeagueThemes BuildThemes(
        List<GameRecap> games,
        List<Transaction> transactions,
        Dictionary<int, OwnerRef> ownerByRosterId,
        Dictionary<string, Player> players,
        Dictionary<string, List<WeeklyPlayerStats>> weekly,
        int week)
    {
        GameSideRef? high = null, low = null;
        GameRef? narrow = null;

        foreach (var g in games)
        {
            foreach (var s in new[] { g.Home, g.Away })
            {
                if (high is null || s.FinalScore > high.Score)
                    high = new GameSideRef(s.UserId, s.OwnerDisplay, s.CurrentTeamName, s.FinalScore);
                if (low is null || s.FinalScore < low.Score)
                    low = new GameSideRef(s.UserId, s.OwnerDisplay, s.CurrentTeamName, s.FinalScore);
            }
            if (narrow is null || g.Margin < Math.Abs(narrow.ScoreA - narrow.ScoreB))
            {
                narrow = new GameRef(g.MatchupId, g.Home.OwnerDisplay, g.Away.OwnerDisplay, g.Home.FinalScore, g.Away.FinalScore);
            }
        }

        // Top 5 individual performances league-wide (this week's starters across all rosters).
        var allStarters = games.SelectMany(g => new[] { g.Home, g.Away }
            .SelectMany(s => s.Starters.Select(p => new { Side = s, Player = p, Started = true })))
            .OrderByDescending(x => x.Player.Points)
            .Take(5)
            .Select(x => new TopPerformer(
                PlayerId: x.Player.PlayerId,
                FullName: x.Player.FullName,
                Position: x.Player.Position,
                RealNflTeam: x.Player.RealNflTeam,
                Points: x.Player.Points,
                OwnedByUserId: x.Side.UserId,
                OwnedByDisplay: x.Side.OwnerDisplay,
                Started: x.Started))
            .ToList();

        // Waiver / FA grades: every successful add (waivers process Wed AM so all adds are eligible for the same week).
        var waiverGrades = new List<WaiverGrade>();
        foreach (var t in transactions.Where(t => t.Type is "waiver" or "free_agent" && t.Status == "complete"))
        {
            if (t.Adds is null) continue;
            foreach (var add in t.Adds)
            {
                var (playerId, rosterId) = (add.Key, add.Value);
                var owner = ownerByRosterId.GetValueOrDefault(rosterId);
                players.TryGetValue(playerId, out var p);
                int? bid = null;
                if (t.Settings is not null && t.Settings.TryGetValue("waiver_bid", out var waiverBid))
                {
                    if (waiverBid.ValueKind == JsonValueKind.Number) bid = waiverBid.GetInt32();
                }
                decimal? weekPts = null;
                if (weekly.TryGetValue(playerId, out var weeks))
                {
                    var thisWeek = weeks.FirstOrDefault(w => w.Week == week);
                    weekPts = thisWeek?.FantasyPoints;
                }
                string? droppedId = null, droppedName = null;
                if (t.Drops is not null && t.Drops.Count > 0)
                {
                    droppedId = t.Drops.Keys.FirstOrDefault();
                    if (droppedId is not null && players.TryGetValue(droppedId, out var dp))
                        droppedName = dp.FullName;
                }
                waiverGrades.Add(new WaiverGrade(
                    TransactionId: t.TransactionId,
                    Status: t.Status ?? "",
                    ClaimingUserId: owner?.UserId ?? "",
                    ClaimingDisplay: owner?.DisplayName ?? $"Roster {rosterId}",
                    PlayerId: playerId,
                    PlayerName: p?.FullName ?? playerId,
                    Position: p?.Position,
                    FaabBid: bid,
                    WeekPoints: weekPts.HasValue ? Math.Round(weekPts.Value, 2) : null,
                    DroppedPlayerId: droppedId,
                    DroppedPlayerName: droppedName));
            }
        }
        var orderedWaivers = waiverGrades
            .OrderByDescending(w => w.WeekPoints ?? 0)
            .Take(20)
            .ToList();

        // Trades: aggregate adds/drops/picks/FAAB into per-side summaries.
        var trades = new List<TradeSummary>();
        foreach (var t in transactions.Where(t => t.Type == "trade" && t.Status == "complete"))
        {
            var sides = new List<TradeSide>();
            var rosterIds = (t.RosterIds ?? []).Distinct().ToList();
            foreach (var rid in rosterIds)
            {
                var owner = ownerByRosterId.GetValueOrDefault(rid);
                var receivedPlayers = (t.Adds ?? new())
                    .Where(kv => kv.Value == rid)
                    .Select(kv =>
                    {
                        players.TryGetValue(kv.Key, out var p);
                        return $"{p?.FullName ?? kv.Key} ({p?.Position ?? "?"})";
                    })
                    .ToList();
                var receivedPicks = (t.DraftPicks ?? [])
                    .Where(dp => dp.OwnerId == rid)
                    .Select(dp => $"{dp.Season} R{dp.Round} (from roster {dp.PreviousOwnerId})")
                    .ToList();
                var faabReceived = (t.WaiverBudget ?? [])
                    .Where(wb => wb.Receiver == rid)
                    .Sum(wb => wb.Amount);
                decimal? wkPoints = null;
                foreach (var addId in (t.Adds ?? new()).Where(kv => kv.Value == rid).Select(kv => kv.Key))
                {
                    if (weekly.TryGetValue(addId, out var weeks))
                    {
                        var thisWeek = weeks.FirstOrDefault(w => w.Week == week);
                        if (thisWeek?.FantasyPoints is decimal fp) wkPoints = (wkPoints ?? 0) + fp;
                    }
                }
                sides.Add(new TradeSide(
                    UserId: owner?.UserId ?? "",
                    OwnerDisplay: owner?.DisplayName ?? $"Roster {rid}",
                    ReceivedPlayers: receivedPlayers,
                    ReceivedDraftPicks: receivedPicks,
                    FaabReceived: faabReceived,
                    WeekPointsFromReceived: wkPoints.HasValue ? Math.Round(wkPoints.Value, 2) : null));
            }
            var completedAt = DateTimeOffset.FromUnixTimeMilliseconds(t.StatusUpdated ?? t.Created ?? 0);
            trades.Add(new TradeSummary(t.TransactionId, sides, completedAt));
        }

        // Stat-line oddities.
        var oddities = BuildOddities(games);

        return new LeagueThemes(
            HighestScore: high,
            LowestScore: low,
            NarrowestMargin: narrow,
            TopFivePerformers: allStarters,
            WaiverGrades: orderedWaivers,
            Trades: trades,
            StatLineOddities: oddities);
    }

    private static List<string> BuildOddities(List<GameRecap> games)
    {
        var oddities = new List<string>();
        foreach (var g in games)
        {
            foreach (var side in new[] { g.Home, g.Away })
            {
                foreach (var p in side.Starters)
                {
                    // Receptions but no targets recorded
                    if ((p.Receptions ?? 0) > 0 && (p.Targets ?? 0) == 0)
                        oddities.Add($"{p.FullName} caught {p.Receptions} balls without a target on record (data check).");
                    // QB threw 3+ INTs but still scored 20+
                    if ((p.PassingInterceptions ?? 0) >= 3 && p.Points >= 20)
                        oddities.Add($"{p.FullName} threw {p.PassingInterceptions} INTs and still posted {p.Points:F1} fantasy points.");
                    // 0-target WR/TE who somehow scored
                    if (p.Position is "WR" or "TE" && (p.Targets ?? 0) == 0 && p.Points >= 8)
                        oddities.Add($"{p.FullName} ({p.Position}) scored {p.Points:F1} on zero targets.");
                    // 100+ yards rushing AND receiving (true workhorse line)
                    if ((p.RushingYards ?? 0) >= 100 && (p.ReceivingYards ?? 0) >= 100)
                        oddities.Add($"{p.FullName} went 100/100 — {p.RushingYards} rush, {p.ReceivingYards} receiving.");
                    // Big bust
                    if (p.BustFlag && p.ProjectedPoints is decimal proj && proj >= 12)
                        oddities.Add($"{p.FullName} cratered: {p.Points:F1} on a {proj:F1}-point projection.");
                    // Big boom
                    if (p.BoomFlag && p.Points >= 25)
                        oddities.Add($"{p.FullName} smashed: {p.Points:F1} on a {p.ProjectedPoints:F1}-point projection.");
                }
            }
        }
        return oddities.Distinct().Take(15).ToList();
    }

    // ---------------- Look-ahead ----------------

    private LookAhead BuildLookAhead(
        int week,
        List<Matchup> nextWeek,
        Dictionary<int, OwnerRef> ownerByRosterId,
        Dictionary<string, Player> players,
        Dictionary<string, List<WeeklyPlayerStats>> weekly,
        List<StandingsRow> standings,
        bool isFinalWeek)
    {
        if (isFinalWeek || nextWeek.Count == 0)
        {
            return new LookAhead(NextWeek: week + 1, Matchups: [], PivotPlayers: [], OpenQuestions: []);
        }

        var grouped = nextWeek.Where(m => m.MatchupId.HasValue).GroupBy(m => m.MatchupId!.Value);
        var matchups = new List<NextWeekMatchup>();
        foreach (var grp in grouped)
        {
            var pair = grp.ToList();
            if (pair.Count < 2) continue;
            var t1 = pair[0]; var t2 = pair[1];
            var o1 = ownerByRosterId.GetValueOrDefault(t1.RosterId);
            var o2 = ownerByRosterId.GetValueOrDefault(t2.RosterId);

            decimal? proj1 = ProjectLineup(t1, weekly, week + 1);
            decimal? proj2 = ProjectLineup(t2, weekly, week + 1);

            var rank1 = standings.FirstOrDefault(s => s.UserId == o1?.UserId)?.Rank;
            var rank2 = standings.FirstOrDefault(s => s.UserId == o2?.UserId)?.Rank;
            decimal? rankGap = (rank1.HasValue && rank2.HasValue) ? Math.Abs(rank1.Value - rank2.Value) : null;

            (string Type, string Label)? hook = null;
            if (o1 is not null && o2 is not null) hook = _lore.ResolveHook(o1.Username, o2.Username);

            matchups.Add(new NextWeekMatchup(
                MatchupId: grp.Key,
                HomeUserId: o1?.UserId ?? "",
                HomeOwnerDisplay: o1?.DisplayName ?? $"Roster {t1.RosterId}",
                HomeTeamName: o1?.TeamName ?? "",
                AwayUserId: o2?.UserId ?? "",
                AwayOwnerDisplay: o2?.DisplayName ?? $"Roster {t2.RosterId}",
                AwayTeamName: o2?.TeamName ?? "",
                HomeProjection: proj1,
                AwayProjection: proj2,
                PowerRankingGap: rankGap,
                StoryHookType: hook?.Type,
                StoryHookLabel: hook?.Label,
                SeedImplication: null));
        }

        // Pivot players: this-week boom or bust starters with an extreme delta vs projection.
        var pivots = new List<PivotPlayer>();
        var openQuestions = new List<string>();

        return new LookAhead(week + 1, matchups, pivots, openQuestions);
    }

    private static decimal? ProjectLineup(Matchup m, Dictionary<string, List<WeeklyPlayerStats>> weekly, int forWeek)
    {
        if (m.Starters is null || m.Starters.Count == 0) return null;
        decimal sum = 0; int counted = 0;
        foreach (var pid in m.Starters)
        {
            if (string.IsNullOrEmpty(pid) || pid == "0") continue;
            var p = ComputeProjection(pid, forWeek, weekly);
            if (p.HasValue) { sum += p.Value; counted++; }
        }
        return counted == 0 ? null : Math.Round(sum, 2);
    }

    // ---------------- Agent fetch hints ----------------

    private static List<string> BuildAgentFetchHints(List<GameRecap> games, LookAhead lookAhead, Dictionary<string, Player> players)
    {
        var hints = new List<string>();

        // Injury status flags from Sleeper player metadata for next-week starters.
        foreach (var matchup in lookAhead.Matchups)
        {
            // We don't have per-side rosters in the look-ahead at this layer, so just emit a generic hint.
        }

        // Anyone with an injury_status of Questionable / Doubtful / Out who started this week.
        foreach (var g in games)
        {
            foreach (var side in new[] { g.Home, g.Away })
            {
                foreach (var p in side.Starters)
                {
                    if (!players.TryGetValue(p.PlayerId, out var pl)) continue;
                    if (!string.IsNullOrEmpty(pl.InjuryStatus) && pl.InjuryStatus is not "Healthy" and not "Active")
                        hints.Add($"Injury status check: {p.FullName} ({pl.InjuryStatus}) — confirm next-week availability.");
                }
            }
        }

        hints.Add("Search for any breaking NFL injury news from the past 48 hours that affects next-week starters.");
        hints.Add("Search for depth-chart shifts or suspensions announced this week.");
        hints.Add("Search for Sunday weather forecasts for outdoor games involving any next-week starters.");

        return hints.Distinct().Take(20).ToList();
    }

    // ---------------- Prior recaps + name watch ----------------

    private (List<PriorRecap>, List<TeamNameChange>) LoadPriorRecapsAndNameWatch(int season, int week, List<OwnerRef> owners)
    {
        var dir = RecapPaths.RecapDir(season);
        var prior = new List<PriorRecap>();
        if (Directory.Exists(dir))
        {
            var lastWeekPath = Path.Combine(dir, $"week-{week - 1:D2}.md");
            if (week > 1 && File.Exists(lastWeekPath))
            {
                prior.Add(new PriorRecap(week - 1, "verbatim", File.ReadAllText(lastWeekPath)));
            }
            var twoWeekPath = Path.Combine(dir, $"week-{week - 2:D2}.md");
            if (week > 2 && File.Exists(twoWeekPath))
            {
                var summary = SummarizeMarkdown(File.ReadAllText(twoWeekPath), 5);
                prior.Add(new PriorRecap(week - 2, "summary", summary));
            }
        }

        // Team-name watch from the snapshot file.
        var changes = new List<TeamNameChange>();
        var historyPath = RecapPaths.TeamNameHistory(season);
        if (File.Exists(historyPath))
        {
            try
            {
                var json = File.ReadAllText(historyPath);
                var history = JsonSerializer.Deserialize<TeamNameHistoryFile>(json);
                if (history is not null)
                {
                    var lastSnapshot = history.Snapshots
                        .Where(s => s.Week < week)
                        .OrderByDescending(s => s.Week)
                        .FirstOrDefault();
                    if (lastSnapshot is not null)
                    {
                        foreach (var o in owners)
                        {
                            if (lastSnapshot.OwnerNames.TryGetValue(o.UserId, out var prev))
                            {
                                if (!string.Equals(prev.TeamName, o.TeamName, StringComparison.Ordinal))
                                {
                                    changes.Add(new TeamNameChange(
                                        UserId: o.UserId,
                                        OwnerDisplay: o.DisplayName,
                                        PreviousName: prev.TeamName,
                                        NewName: o.TeamName,
                                        WeekChanged: week));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (warning: failed to read team-name history: {ex.Message})");
            }
        }
        return (prior, changes);
    }

    private static string SummarizeMarkdown(string md, int maxBullets)
    {
        // Cheap deterministic 5-bullet summary: take the first markdown headings + first sentence of each.
        var lines = md.Split('\n');
        var headers = lines.Where(l => l.StartsWith("## ", StringComparison.Ordinal)).Take(maxBullets).ToList();
        if (headers.Count == 0) return md.Length > 600 ? md[..600] + "..." : md;
        return string.Join("\n", headers.Select(h => "- " + h.TrimStart('#', ' ').Trim()));
    }

    private void SnapshotTeamNames(int season, int week, List<OwnerRef> owners)
    {
        var path = RecapPaths.TeamNameHistory(season);
        TeamNameHistoryFile history;
        if (File.Exists(path))
        {
            try { history = JsonSerializer.Deserialize<TeamNameHistoryFile>(File.ReadAllText(path)) ?? new(); }
            catch { history = new(); }
        }
        else
        {
            history = new();
        }
        history.Season = season;
        // Replace any existing snapshot for this week.
        history.Snapshots.RemoveAll(s => s.Week == week);
        history.Snapshots.Add(new TeamNameSnapshot
        {
            Week = week,
            GeneratedAt = DateTimeOffset.UtcNow,
            OwnerNames = owners.ToDictionary(
                o => o.UserId,
                o => new TeamNameEntry { DisplayName = o.DisplayName, TeamName = o.TeamName, Username = o.Username })
        });
        history.Snapshots.Sort((a, b) => a.Week.CompareTo(b.Week));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------------- Helpers ----------------

    // ===== Feature: streaks, per-week PF, power rankings, playoff picture, season ledger =====

    private static Dictionary<int, List<decimal>> ComputePerWeekPfByRoster(
        List<Roster> rosters,
        Dictionary<int, List<Matchup>> matchupsByWeek,
        int throughWeek)
    {
        var result = rosters.ToDictionary(r => r.RosterId, _ => new List<decimal>());
        foreach (var w in matchupsByWeek.Keys.OrderBy(k => k))
        {
            if (w > throughWeek) continue;
            foreach (var m in matchupsByWeek[w])
            {
                if (!result.ContainsKey(m.RosterId)) continue;
                if (m.HasScoringData())
                    result[m.RosterId].Add(m.ScoreOrZero());
            }
        }
        return result;
    }

    private static Dictionary<int, string> ComputeStreakByRoster(
        List<Roster> rosters,
        Dictionary<int, List<Matchup>> matchupsByWeek,
        int throughWeek)
    {
        // Walk weeks ascending, record W/L per roster, then collapse the trailing run.
        var perWeekResult = rosters.ToDictionary(r => r.RosterId, _ => new List<char>()); // 'W' / 'L' / 'T'
        foreach (var w in matchupsByWeek.Keys.OrderBy(k => k))
        {
            if (w > throughWeek) continue;
            foreach (var grp in matchupsByWeek[w].GroupBy(m => m.MatchupId))
            {
                var pair = grp.ToList();
                if (pair.Count != 2) continue;
                var a = pair[0]; var b = pair[1];
                if (!perWeekResult.ContainsKey(a.RosterId) || !perWeekResult.ContainsKey(b.RosterId)) continue;
                if (!TryGetPlayedScores(a, b, out var aPts, out var bPts)) continue;
                if (aPts > bPts) { perWeekResult[a.RosterId].Add('W'); perWeekResult[b.RosterId].Add('L'); }
                else if (bPts > aPts) { perWeekResult[b.RosterId].Add('W'); perWeekResult[a.RosterId].Add('L'); }
                else { perWeekResult[a.RosterId].Add('T'); perWeekResult[b.RosterId].Add('T'); }
            }
        }
        var streaks = new Dictionary<int, string>();
        foreach (var (rid, results) in perWeekResult)
        {
            if (results.Count == 0) { streaks[rid] = ""; continue; }
            char last = results[^1];
            int n = 0;
            for (int i = results.Count - 1; i >= 0 && results[i] == last; i--) n++;
            streaks[rid] = $"{last}{n}";
        }
        return streaks;
    }

    private List<PowerRankingRow> BuildPowerRankings(
        int season,
        int week,
        List<StandingsRow> standings,
        Dictionary<int, List<decimal>> perWeekPfByRoster,
        Dictionary<int, OwnerRef> ownerByRosterId,
        bool persistSnapshot)
    {
        if (standings.Count == 0) return [];

        // Score = 0.55 * (PF normalized) + 0.30 * (Last3 normalized) + 0.15 * WinPct
        var rosterIdByUserId = ownerByRosterId.ToDictionary(kv => kv.Value.UserId, kv => kv.Key);
        var enriched = standings.Select(s =>
        {
            int rid = rosterIdByUserId.GetValueOrDefault(s.UserId, -1);
            var pfList = rid >= 0 ? perWeekPfByRoster.GetValueOrDefault(rid) ?? [] : [];
            var last3 = pfList.TakeLast(3).ToList();
            decimal last3Avg = last3.Count == 0 ? 0m : last3.Average();
            int gp = s.Wins + s.Losses + s.Ties;
            decimal winPct = gp == 0 ? 0m : (decimal)(s.Wins + 0.5 * s.Ties) / gp;
            return (Standing: s, Last3Avg: last3Avg, WinPct: winPct);
        }).ToList();

        decimal maxPf = enriched.Max(e => e.Standing.PointsFor);
        decimal maxLast3 = enriched.Max(e => e.Last3Avg);
        if (maxPf <= 0) maxPf = 1m;
        if (maxLast3 <= 0) maxLast3 = 1m;

        var scored = enriched.Select(e =>
        {
            decimal pfNorm = e.Standing.PointsFor / maxPf;
            decimal last3Norm = e.Last3Avg / maxLast3;
            decimal raw = 0.55m * pfNorm + 0.30m * last3Norm + 0.15m * e.WinPct;
            return (e.Standing, e.Last3Avg, Score: Math.Round(raw * 100m, 1));
        }).OrderByDescending(x => x.Score).ToList();

        // Load prior week ranks for delta + persist this week's.
        var priorRanks = LoadPowerHistory(season, week - 1);
        var rows = new List<PowerRankingRow>();
        for (int i = 0; i < scored.Count; i++)
        {
            var (s, last3, score) = scored[i];
            int? delta = priorRanks.TryGetValue(s.UserId, out var prev) ? prev - (i + 1) : null;
            string trend;
            if (delta is null) trend = "—";
            else if (delta >= 3) trend = "▲▲";
            else if (delta >= 1) trend = "▲";
            else if (delta == 0) trend = "—";
            else if (delta >= -2) trend = "▽";
            else trend = "▽▽";
            rows.Add(new PowerRankingRow(
                Rank: i + 1,
                RankDelta: delta,
                UserId: s.UserId,
                OwnerDisplay: s.OwnerDisplay,
                TeamName: s.TeamName,
                PowerScore: score,
                Last3WeekAvgPf: Math.Round(last3, 2),
                Trend: trend));
        }

        if (persistSnapshot)
            SavePowerHistory(season, week, rows);
        return rows;
    }

    private static Dictionary<string, int> LoadPowerHistory(int season, int previousWeek)
    {
        if (previousWeek < 1) return new();
        try
        {
            var path = RecapPaths.PowerHistory(season);
            if (!File.Exists(path)) return new();
            var doc = JsonSerializer.Deserialize<PowerHistoryFile>(File.ReadAllText(path));
            var snap = doc?.Snapshots.FirstOrDefault(s => s.Week == previousWeek);
            return snap?.Ranks ?? new();
        }
        catch { return new(); }
    }

    private static void SavePowerHistory(int season, int week, List<PowerRankingRow> rows)
    {
        try
        {
            var path = RecapPaths.PowerHistory(season);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PowerHistoryFile doc = File.Exists(path)
                ? JsonSerializer.Deserialize<PowerHistoryFile>(File.ReadAllText(path)) ?? new()
                : new() { Season = season };
            doc.Snapshots.RemoveAll(s => s.Week == week);
            doc.Snapshots.Add(new PowerHistorySnapshot
            {
                Week = week,
                GeneratedAt = DateTimeOffset.UtcNow,
                Ranks = rows.ToDictionary(r => r.UserId, r => r.Rank)
            });
            doc.Snapshots = doc.Snapshots.OrderBy(s => s.Week).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }

    private sealed class PowerHistoryFile
    {
        public int Season { get; set; }
        public List<PowerHistorySnapshot> Snapshots { get; set; } = [];
    }
    private sealed class PowerHistorySnapshot
    {
        public int Week { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }
        public Dictionary<string, int> Ranks { get; set; } = new();
    }

    /// <summary>
    /// In playoff weeks, replaces each game's <see cref="GameRecap.PlayoffRound"/> with the
    /// specific bracket role (Championship, 3rd-place game, Consolation final, 7th-place game,
    /// Semifinal, etc.) so the game prompt and the headline label are accurate.
    /// </summary>
    private static List<GameRecap> ApplyBracketLabelsToGames(
        List<GameRecap> games,
        int week,
        List<PlayoffBracketMatch> winners,
        List<PlayoffBracketMatch> losers)
    {
        if (week < Schedule.PlayoffStartWeek) return games;
        if (winners.Count == 0 && losers.Count == 0) return games;

        int currentRound = week - Schedule.PlayoffStartWeek + 1;
        int winnersMaxRound = winners.Count == 0 ? 0 : winners.Max(b => b.Round);
        int losersMaxRound = losers.Count == 0 ? 0 : losers.Max(b => b.Round);

        // Build a lookup: unordered pair of roster ids -> bracket context.
        var labels = new Dictionary<(int, int), (string Label, string SeasonContext, int Importance, string Reason)>();
        void Add(int? a, int? b, string label, string seasonContext)
        {
            if (a is null || b is null) return;
            int lo = Math.Min(a.Value, b.Value);
            int hi = Math.Max(a.Value, b.Value);
            var (importance, reason) = BracketStoryImportance(label, seasonContext);
            labels[(lo, hi)] = (label, seasonContext, importance, reason);
        }

        // Winners bracket: PlacementRank when present (1 = championship, 3 = 3rd-place); semifinals otherwise.
        foreach (var m in winners.Where(b => b.Round == currentRound))
        {
            string label = m.PlacementRank switch
            {
                1 => "Championship",
                3 => "3rd-place game",
                _ when m.Round == winnersMaxRound => "Championship",
                _ when winnersMaxRound > 1 && m.Round == winnersMaxRound - 1 => "Semifinal",
                _ => $"Winners-bracket round {m.Round}"
            };
            Add(m.Team1, m.Team2, label, "playoffs_winners");
        }

        // Losers bracket: prefer PlacementRank (5 = consolation final/1.01, 7 = 7th-place game).
        // If placement isn't set, fall back to bracket sources, then to the prior-round winners/losers
        // (a final-round match whose two teams both won their semis is the consolation final).
        var losersFinalRound = losers.Where(b => b.Round == losersMaxRound && b.Round == currentRound).ToList();
        int priorRound = currentRound - 1;
        var priorLosersWinners = losers.Where(x => x.Round == priorRound && x.Winner.HasValue).Select(x => x.Winner!.Value).ToHashSet();
        var priorLosersLosers = losers.Where(x => x.Round == priorRound && x.Loser.HasValue).Select(x => x.Loser!.Value).ToHashSet();
        foreach (var m in losersFinalRound)
        {
            string? label = m.PlacementRank switch
            {
                // Sleeper losers-bracket 'p' is local: p=1 is the top of the losers bracket
                // (= 5th place overall, winner gets the 1.01); p=3 is the next placement game
                // (= 7th-place game; loser finishes last and forfeits a keeper).
                1 => "Consolation final",
                3 => "7th-place game",
                _ => null
            };
            if (label is null && m.Team1From is not null && m.Team2From is not null)
            {
                bool fromWinners = m.Team1From.Winner.HasValue && m.Team2From.Winner.HasValue;
                bool fromLosers = m.Team1From.Loser.HasValue && m.Team2From.Loser.HasValue;
                if (fromWinners) label = "Consolation final";
                else if (fromLosers) label = "7th-place game";
            }
            if (label is null && m.Team1.HasValue && m.Team2.HasValue)
            {
                bool bothWon = priorLosersWinners.Contains(m.Team1.Value) && priorLosersWinners.Contains(m.Team2.Value);
                bool bothLost = priorLosersLosers.Contains(m.Team1.Value) && priorLosersLosers.Contains(m.Team2.Value);
                if (bothWon) label = "Consolation final";
                else if (bothLost) label = "7th-place game";
            }
            Add(m.Team1, m.Team2, label ?? "Consolation bracket", "consolation");
        }
        // Earlier-round losers matches.
        foreach (var m in losers.Where(b => b.Round == currentRound && b.Round != losersMaxRound))
        {
            Add(m.Team1, m.Team2, "Consolation semifinal", "consolation");
        }

        if (labels.Count == 0) return games;

        var updated = new List<GameRecap>(games.Count);
        foreach (var g in games)
        {
            int lo = Math.Min(g.Home.RosterId, g.Away.RosterId);
            int hi = Math.Max(g.Home.RosterId, g.Away.RosterId);
            if (labels.TryGetValue((lo, hi), out var info))
                updated.Add(g with
                {
                    SeasonContext = info.SeasonContext,
                    PlayoffRound = info.Label,
                    StoryImportance = info.Importance,
                    StoryImportanceReason = info.Reason
                });
            else
                updated.Add(g);
        }
        return updated;

        static (int Importance, string Reason) BracketStoryImportance(string label, string seasonContext)
            => label switch
            {
                "Championship" => (5, "championship game"),
                "Semifinal" => (5, "playoff game"),
                "3rd-place game" => (4, "3rd-place game"),
                "Consolation final" => (4, "consolation final (1.01 on the line)"),
                "7th-place game" => (4, "7th-place game (last-place keeper penalty on the line)"),
                "Consolation semifinal" => (3, "consolation bracket game"),
                _ when seasonContext == "playoffs_winners" => (5, "playoff game"),
                _ => (3, "consolation bracket game")
            };
    }

    private static PlayoffPicture BuildPlayoffPicture(
        League league,
        int week,
        List<StandingsRow> standings,
        Dictionary<int, OwnerRef> ownerByRosterId,
        List<PlayoffBracketMatch> winners,
        List<PlayoffBracketMatch> losers,
        List<Matchup> currentWeekMatchups)
    {
        int playoffWeekStart = Schedule.PlayoffStartWeek;
        int playoffTeams = 4;
        if (league.Settings is not null && league.Settings.TryGetValue("playoff_teams", out var pt) && pt.ValueKind == JsonValueKind.Number)
            playoffTeams = pt.GetInt32();

        // Phase: regular through Schedule.RegularSeasonLastWeek - 1, bubble on the final
        // regular-season week, then playoffs/championship.
        string phase;
        if (week < Schedule.RegularSeasonLastWeek) phase = "regular";
        else if (week == Schedule.RegularSeasonLastWeek) phase = "bubble";
        else if (week >= Schedule.ChampionshipWeek) phase = "championship_week";
        else phase = "playoffs";

        // Bracket projections: top-N by current standings get winners; rest get consolation.
        var ordered = standings.OrderBy(s => s.Rank).ToList();
        var topSpots = new List<BracketSpot>();
        var conSpots = new List<BracketSpot>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            string status = phase switch
            {
                "regular" or "bubble" =>
                    i < playoffTeams - 1 ? "in" :
                    i == playoffTeams - 1 ? "bubble" :
                    i == playoffTeams ? "bubble" : "out",
                _ => i < playoffTeams ? "clinched" : "out"
            };
            var spot = new BracketSpot(
                Seed: i + 1,
                UserId: s.UserId,
                OwnerDisplay: s.OwnerDisplay,
                TeamName: s.TeamName,
                Wins: s.Wins,
                Losses: s.Losses,
                Ties: s.Ties,
                PointsFor: s.PointsFor,
                Status: status);
            if (i < playoffTeams) topSpots.Add(spot);
            else conSpots.Add(spot);
        }

        // Keeper rules — surfaced every week so the agent can riff.
        var keeperRules = new List<string>
        {
            "Standard keepers: each team can keep up to **4 players** for next season.",
            "**Consolation bracket winner gets the 1.01 (first overall pick)** in next season's draft.",
            "**Last-place finisher loses one keeper slot** — they can only keep 3 of the usual 4."
        };

        // Week stakes — tailored to phase.
        var stakes = new List<StakesNote>();
        if (phase is "regular" or "bubble")
        {
            // Bubble watch only makes sense in the closing weeks of the regular season,
            // when standings are stable enough to actually be on a "bubble". One game
            // into the year, every team is theoretically on the bubble — that's noise.
            bool inBubbleWindow = week >= Schedule.RegularSeasonLastWeek - 2;

            if (inBubbleWindow)
            {
                int bubbleIdx = playoffTeams - 1; // last in spot
                foreach (var grp in currentWeekMatchups.GroupBy(m => m.MatchupId))
                {
                    var pair = grp.ToList(); if (pair.Count != 2) continue;
                    var aOwner = ownerByRosterId.GetValueOrDefault(pair[0].RosterId);
                    var bOwner = ownerByRosterId.GetValueOrDefault(pair[1].RosterId);
                    if (aOwner is null || bOwner is null) continue;
                    var aSeed = standings.FirstOrDefault(s => s.UserId == aOwner.UserId)?.Rank ?? 0;
                    var bSeed = standings.FirstOrDefault(s => s.UserId == bOwner.UserId)?.Rank ?? 0;
                    if (aSeed == 0 || bSeed == 0) continue;
                    bool aBubble = aSeed >= bubbleIdx && aSeed <= bubbleIdx + 2;
                    bool bBubble = bSeed >= bubbleIdx && bSeed <= bubbleIdx + 2;
                    if (aBubble || bBubble)
                    {
                        stakes.Add(new StakesNote(
                            MatchupId: pair[0].MatchupId,
                            Headline: $"Bubble watch — {aOwner.TeamName} (#{aSeed}) vs {bOwner.TeamName} (#{bSeed})",
                            Detail: $"A loss here could push the loser into the consolation bracket (top {playoffTeams} make the playoff bracket)."));
                    }
                }
            }
            if (phase == "bubble" && stakes.Count == 0)
            {
                stakes.Add(new StakesNote(null,
                    "Final week before the bracket locks",
                    $"After this week, the top {playoffTeams} go to the playoff bracket; the bottom {ordered.Count - playoffTeams} fight for the consolation crown (and the 1.01)."));
            }
        }
        else // playoffs / championship_week
        {
            int currentRound = week - playoffWeekStart + 1;
            // Losers bracket may run for fewer rounds than the winners bracket (and start later).
            // Map the winners-bracket round to the corresponding losers-bracket round by offsetting
            // off the round-count delta (e.g., 3 winners rounds vs 2 losers rounds → losers R2 = week of winners R3).
            int winnersMaxRound = winners.Count == 0 ? 0 : winners.Max(b => b.Round);
            int losersMaxRound = losers.Count == 0 ? 0 : losers.Max(b => b.Round);
            int losersCurrentRound = currentRound - (winnersMaxRound - losersMaxRound);
            foreach (var bm in winners.Where(b => b.Round == currentRound))
            {
                if (bm.Team1 is null || bm.Team2 is null) continue;
                var t1Owner = ownerByRosterId.GetValueOrDefault(bm.Team1.Value);
                var t2Owner = ownerByRosterId.GetValueOrDefault(bm.Team2.Value);
                if (t1Owner is null || t2Owner is null) continue;
                bool isFinal = currentRound >= winners.Max(x => x.Round);
                // PlacementRank ('p'): 1 = championship, 3 = third-place game (4-team winners bracket).
                int? placement = bm.PlacementRank;
                bool isTitle = isFinal && placement == 1;
                string headline = isTitle
                    ? $"Championship — {t1Owner.TeamName} vs {t2Owner.TeamName}"
                    : (isFinal && placement == 3)
                        ? $"3rd-place game — {t1Owner.TeamName} vs {t2Owner.TeamName}"
                        : $"Winners-bracket {currentRound switch { 1 => "Quarterfinal", 2 => "Semifinal", _ => "round " + currentRound }} — {t1Owner.TeamName} vs {t2Owner.TeamName}";
                string detail = isTitle
                    ? "League title on the line."
                    : (isFinal && placement == 3)
                        ? "Winner finishes 3rd (pick 1.06 next year); loser finishes 4th (pick 1.05)."
                        : "Loser drops to the third-place game; winner advances.";
                stakes.Add(new StakesNote(MatchupId: null, Headline: headline, Detail: detail));
            }
            foreach (var bm in losers.Where(b => b.Round == losersCurrentRound))
            {
                if (bm.Team1 is null || bm.Team2 is null) continue;
                var t1Owner = ownerByRosterId.GetValueOrDefault(bm.Team1.Value);
                var t2Owner = ownerByRosterId.GetValueOrDefault(bm.Team2.Value);
                if (t1Owner is null || t2Owner is null) continue;
                bool isFinal = losers.Count > 0 && losersCurrentRound >= losers.Max(x => x.Round);
                // PlacementRank ('p') in losers bracket is local: p=1 is the consolation final
                // (overall 5th, winner gets the 1.01); p=3 is the 7th-place game (loser finishes last and forfeits a keeper).
                // Some Sleeper payloads omit placement on the losers side. Fall back to bracket sources;
                // if those are also missing, derive from the prior-round outcome: the round-final match
                // whose two teams both *won* their losers-semifinal is the consolation final, and the
                // match whose two teams both *lost* their losers-semifinal is the 7th-place game.
                int? placement = bm.PlacementRank;
                bool isOneOhOne = isFinal && placement == 1;
                bool isLastSpot = isFinal && placement == 3;
                if (isFinal && placement is null)
                {
                    if (bm.Team1From is not null && bm.Team2From is not null)
                    {
                        if (bm.Team1From.Winner.HasValue && bm.Team2From.Winner.HasValue) isOneOhOne = true;
                        else if (bm.Team1From.Loser.HasValue && bm.Team2From.Loser.HasValue) isLastSpot = true;
                    }
                    if (!isOneOhOne && !isLastSpot)
                    {
                        // Look at the prior round (semifinals) for these two roster ids.
                        var priorRound = losersCurrentRound - 1;
                        var priorWinners = losers.Where(x => x.Round == priorRound && x.Winner.HasValue).Select(x => x.Winner!.Value).ToHashSet();
                        var priorLosers = losers.Where(x => x.Round == priorRound && x.Loser.HasValue).Select(x => x.Loser!.Value).ToHashSet();
                        bool bothWon = priorWinners.Contains(bm.Team1.Value) && priorWinners.Contains(bm.Team2.Value);
                        bool bothLost = priorLosers.Contains(bm.Team1.Value) && priorLosers.Contains(bm.Team2.Value);
                        if (bothWon) isOneOhOne = true;
                        else if (bothLost) isLastSpot = true;
                    }
                }
                string headline = isOneOhOne
                    ? $"Consolation final — {t1Owner.TeamName} vs {t2Owner.TeamName} (winner gets the 1.01)"
                    : isLastSpot
                        ? $"7th-place game — {t1Owner.TeamName} vs {t2Owner.TeamName} (loser finishes last and forfeits a keeper)"
                        : $"Consolation bracket — {t1Owner.TeamName} vs {t2Owner.TeamName}";
                string detail = isOneOhOne
                    ? "The 1.01 next year goes to the winner of this game. Loser gets pick 1.02."
                    : isLastSpot
                        ? "Winner gets pick 1.03. Loser finishes last for the season and loses one keeper slot (3 of 4)."
                        : "Win to stay alive for the 1.01; lose and the focus shifts to dodging last place (and the lost keeper).";
                stakes.Add(new StakesNote(MatchupId: null, Headline: headline, Detail: detail));
            }
        }

        // Long-streak callouts. Apply in any phase: a 4+ game streak is a story
        // regardless of bracket position. Longer streaks get more emphatic framing.
        foreach (var s in standings)
        {
            var streak = s.Streak ?? "";
            if (streak.Length < 2) continue;
            char kind = streak[0];
            if (kind != 'W' && kind != 'L') continue;
            if (!int.TryParse(streak[1..], out var n) || n < 4) continue;

            // Resolve the real name so we don't leak Sleeper usernames into the stakes block
            // (the stakes block is rendered verbatim by Compose and never goes through ScrubUsernames).
            var ownerRef = ownerByRosterId.Values.FirstOrDefault(o => o.UserId == s.UserId);
            var realName = ownerRef?.RealName ?? ownerRef?.DisplayName ?? s.OwnerDisplay;
            string article = (kind == 'W' || n == 8 || n == 11) ? "a" : "an"; // "a 4-game", "an 8-game" — only 8 and 11 take "an" for our likely range
            // Simpler: just use length as the article driver — "a {n}-game" is correct for 1,2,3,4,5,6,7,9,10,12... and "an 8-game"/"an 11-game" for 8,11.
            article = (n == 8 || n == 11 || n == 18) ? "an" : "a";

            string headline;
            string detail;
            if (kind == 'W')
            {
                headline = n >= 6
                    ? $"Hottest team in the league — {s.TeamName} has won {n} straight"
                    : $"{s.TeamName} on {article} {n}-game win streak";
                detail = n >= 6
                    ? $"{realName} hasn't lost in over a month and a half. Anyone drawing them is the underdog by default."
                    : $"{realName}'s squad has been rolling — next opponent has to break the streak.";
            }
            else
            {
                headline = n >= 6
                    ? $"Coldest team in the league — {s.TeamName} has dropped {n} straight"
                    : $"{s.TeamName} on {article} {n}-game losing streak";
                detail = n >= 6
                    ? $"{realName} hasn't won in over a month and a half. Every week the season slips a little further."
                    : $"{realName}'s squad needs a win to stop the bleeding.";
            }
            stakes.Add(new StakesNote(MatchupId: null, Headline: headline, Detail: detail));
        }

        return new PlayoffPicture(
            Phase: phase,
            PlayoffWeekStart: playoffWeekStart,
            PlayoffTeams: playoffTeams,
            WinnersBracketProjection: topSpots,
            ConsolationBracketProjection: conSpots,
            KeeperRules: keeperRules,
            WeekStakes: stakes);
    }

    /// <summary>
    /// Resolves the final 1..8 placement and next-year draft pick assignment from the two brackets,
    /// only on the championship week (week 17) and only once both brackets have produced winners.
    /// Returns null otherwise. Draft order: 1.01 to consolation winner, 1.08 to champion (worst record
    /// among playoff teams gets best regular pick? — no: this league rewards winning, so champion picks
    /// last (1.08) and consolation winner picks first (1.01)).
    /// </summary>
    private static SeasonOutcome? BuildSeasonOutcome(
        int week,
        List<StandingsRow> standings,
        List<OwnerRef> owners,
        Dictionary<int, OwnerRef> ownerByRosterId,
        List<PlayoffBracketMatch> winners,
        List<PlayoffBracketMatch> losers)
    {
        if (week < Schedule.ChampionshipWeek) return null;
        if (winners.Count == 0 || losers.Count == 0) return null;

        // Locate the four placement games. Winners bracket: p=1 (championship), p=3 (3rd-place game).
        // Losers bracket: p=5 (consolation final / 1.01), p=7 (7th-place game / loser forfeits a keeper).
        // Some Sleeper bracket payloads omit PlacementRank on the losers side; in that case fall
        // back to the bracket-source links — a round-final losers match whose two teams came from
        // semifinal *winners* is the consolation final; both from semifinal *losers* is the
        // 7th-place game.
        var champGame = winners.FirstOrDefault(b => b.PlacementRank == 1);
        var thirdGame = winners.FirstOrDefault(b => b.PlacementRank == 3);
        if (champGame is null || thirdGame is null)
        {
            // Fallback for winners bracket: if exactly two round-final matches and only one has a
            // settled winner relationship from prior winners, treat ordering from match id.
            var winnersFinal = winners.Where(b => b.Round == winners.Max(x => x.Round)).OrderBy(b => b.MatchId).ToList();
            if (winnersFinal.Count >= 2)
            {
                champGame ??= winnersFinal.FirstOrDefault(b => b.Team1From is null || (b.Team1From.Winner.HasValue && b.Team2From?.Winner.HasValue == true));
                thirdGame ??= winnersFinal.FirstOrDefault(b => b.Team1From is not null && b.Team1From.Loser.HasValue && b.Team2From?.Loser.HasValue == true);
            }
            if (champGame is null || thirdGame is null) return null;
        }

        var losersMaxRound = losers.Max(b => b.Round);
        // Losers-bracket placement codes are local: p=1 is the consolation final (5th place overall,
        // winner gets the 1.01); p=3 is the 7th-place game (loser finishes last).
        var consoFinal = losers.FirstOrDefault(b => b.PlacementRank == 1 && b.Round == losers.Max(x => x.Round));
        var seventhGame = losers.FirstOrDefault(b => b.PlacementRank == 3 && b.Round == losers.Max(x => x.Round));
        if (consoFinal is null || seventhGame is null)
        {
            // Fall back: round-final losers match whose two teams both came from semifinal winners
            // is the consolation final; both from losers is the 7th-place game.
            var losersFinal = losers.Where(b => b.Round == losersMaxRound).ToList();
            consoFinal ??= losersFinal.FirstOrDefault(b => b.Team1From is not null && b.Team2From is not null
                && b.Team1From.Winner.HasValue && b.Team2From.Winner.HasValue);
            seventhGame ??= losersFinal.FirstOrDefault(b => b.Team1From is not null && b.Team2From is not null
                && b.Team1From.Loser.HasValue && b.Team2From.Loser.HasValue);
            // Last-resort fallback: derive from the prior-round (semifinal) outcomes.
            if (consoFinal is null || seventhGame is null)
            {
                int priorRound = losersMaxRound - 1;
                var priorWinners = losers.Where(x => x.Round == priorRound && x.Winner.HasValue).Select(x => x.Winner!.Value).ToHashSet();
                var priorLosersIds = losers.Where(x => x.Round == priorRound && x.Loser.HasValue).Select(x => x.Loser!.Value).ToHashSet();
                consoFinal ??= losersFinal.FirstOrDefault(b => b.Team1.HasValue && b.Team2.HasValue
                    && priorWinners.Contains(b.Team1.Value) && priorWinners.Contains(b.Team2.Value));
                seventhGame ??= losersFinal.FirstOrDefault(b => b.Team1.HasValue && b.Team2.HasValue
                    && priorLosersIds.Contains(b.Team1.Value) && priorLosersIds.Contains(b.Team2.Value));
            }
            if (consoFinal is null || seventhGame is null) return null;
        }

        // All four must have winners decided.
        if (champGame.Winner is null || champGame.Loser is null) return null;
        if (thirdGame.Winner is null || thirdGame.Loser is null) return null;
        if (consoFinal.Winner is null || consoFinal.Loser is null) return null;
        if (seventhGame.Winner is null || seventhGame.Loser is null) return null;

        SeasonPlacement Place(int rosterId, int finalPlace, int draftPick, string path)
        {
            var owner = ownerByRosterId.GetValueOrDefault(rosterId);
            var realName = owner?.RealName ?? owner?.DisplayName ?? "";
            var teamName = owner?.TeamName ?? "";
            var sr = standings.FirstOrDefault(s => owner is not null && s.UserId == owner.UserId);
            return new SeasonPlacement(
                FinalPlace: finalPlace,
                DraftPick: draftPick,
                UserId: owner?.UserId ?? "",
                OwnerRealName: realName,
                TeamName: teamName,
                RegularSeasonWins: sr?.Wins ?? 0,
                RegularSeasonLosses: sr?.Losses ?? 0,
                RegularSeasonTies: sr?.Ties ?? 0,
                RegularSeasonPointsFor: sr?.PointsFor ?? 0m,
                Path: path);
        }

        return new SeasonOutcome(
            Champion:           Place(champGame.Winner.Value,    1, 8, "Won championship"),
            RunnerUp:           Place(champGame.Loser.Value,     2, 7, "Lost in championship"),
            ThirdPlace:         Place(thirdGame.Winner.Value,    3, 6, "Won 3rd-place game"),
            FourthPlace:        Place(thirdGame.Loser.Value,     4, 5, "Lost 3rd-place game"),
            ConsolationFifth:   Place(consoFinal.Winner.Value,   5, 1, "Won consolation final (1.01)"),
            ConsolationSixth:   Place(consoFinal.Loser.Value,    6, 2, "Lost consolation final"),
            ConsolationSeventh: Place(seventhGame.Winner.Value,  7, 3, "Won 7th-place game"),
            ConsolationLast:    Place(seventhGame.Loser.Value,   8, 4, "Lost 7th-place game (finishes last; forfeits a keeper)"),
            LastPlaceKeeperPenalty: "Forfeits one keeper next year (3 of 4).");
    }

    private static SeasonLedger BuildSeasonLedger(
        List<StandingsRow> standings,
        Dictionary<int, List<decimal>> perWeekPfByRoster,
        Dictionary<int, string> streakByRoster,
        Dictionary<int, OwnerRef> ownerByRosterId)
    {
        var winStreaks = new List<StreakNote>();
        var lossStreaks = new List<StreakNote>();
        var hot = new List<TrendNote>();
        var cold = new List<TrendNote>();
        var highlights = new List<string>();

        var rosterIdByUserId = ownerByRosterId.ToDictionary(kv => kv.Value.UserId, kv => kv.Key);

        foreach (var s in standings)
        {
            if (!rosterIdByUserId.TryGetValue(s.UserId, out var rid)) continue;
            var streak = streakByRoster.GetValueOrDefault(rid, "");
            if (streak.Length >= 2 && (streak[0] == 'W' || streak[0] == 'L') && int.TryParse(streak[1..], out var n) && n >= 2)
            {
                var note = new StreakNote(s.UserId, s.OwnerDisplay, s.TeamName, n, streak[0].ToString());
                if (streak[0] == 'W') winStreaks.Add(note); else lossStreaks.Add(note);
            }

            var pfList = perWeekPfByRoster.GetValueOrDefault(rid) ?? [];
            if (pfList.Count >= 4)
            {
                var seasonAvg = pfList.Average();
                var last3Avg = pfList.TakeLast(3).Average();
                var deltaPct = seasonAvg == 0 ? 0m : Math.Round((last3Avg - seasonAvg) / seasonAvg * 100m, 1);
                if (deltaPct >= 12m)
                    hot.Add(new TrendNote(s.UserId, s.OwnerDisplay, s.TeamName, Math.Round(seasonAvg, 2), Math.Round(last3Avg, 2), deltaPct));
                else if (deltaPct <= -12m)
                    cold.Add(new TrendNote(s.UserId, s.OwnerDisplay, s.TeamName, Math.Round(seasonAvg, 2), Math.Round(last3Avg, 2), deltaPct));

                // Notable: longest current run of 150+ scores (uncapped — show the actual length so it escalates week over week).
                int run150 = 0;
                for (int i = pfList.Count - 1; i >= 0; i--)
                {
                    if (pfList[i] >= 150m) run150++;
                    else break;
                }
                if (run150 >= 4)
                    highlights.Add($"{s.TeamName} has scored 150+ in {run150} straight weeks.");
                // Notable: under-100 in two straight
                if (pfList.Count >= 2 && pfList.TakeLast(2).All(p => p < 100m))
                    highlights.Add($"{s.TeamName} has been held under 100 in back-to-back weeks.");
            }
        }

        winStreaks = winStreaks.OrderByDescending(x => x.Length).Take(3).ToList();
        lossStreaks = lossStreaks.OrderByDescending(x => x.Length).Take(3).ToList();
        hot = hot.OrderByDescending(x => x.DeltaPct).Take(2).ToList();
        cold = cold.OrderBy(x => x.DeltaPct).Take(2).ToList();

        return new SeasonLedger(winStreaks, lossStreaks, hot, cold, highlights.Distinct().ToList());
    }

    private static (string SeasonType, string? PlayoffRound, bool IsFinalWeek) ClassifyWeek(
        League league,
        int week,
        List<PlayoffBracketMatch> winners,
        List<PlayoffBracketMatch> losers)
    {
        // Use the league-specific schedule rather than Sleeper's playoff_week_start
        // (which does not match this league's actual played schedule).
        if (week <= Schedule.RegularSeasonLastWeek) return ("regular", null, false);

        // Round 1 = week PlayoffStartWeek; Final = ChampionshipWeek.
        int round = week - Schedule.RegularSeasonLastWeek; // 16 -> 1, 17 -> 2
        bool isFinal = week >= Schedule.ChampionshipWeek;

        int playoffTeams = 4;
        if (league.Settings is not null && league.Settings.TryGetValue("playoff_teams", out var pt))
        {
            if (pt.ValueKind == JsonValueKind.Number) playoffTeams = pt.GetInt32();
        }

        // For a 4-team bracket: round 1 = Semifinal, round 2 = Championship.
        // For a 6-team bracket: round 1 = Quarterfinal, round 2 = Semifinal, round 3 = Championship.
        string label = (playoffTeams, round, isFinal) switch
        {
            (_, _, true)  => "Championship",
            (<= 4, 1, _)  => "Semifinal",
            (> 4, 1, _)   => "Quarterfinal",
            (> 4, 2, _)   => "Semifinal",
            _             => $"Round {round}"
        };

        bool inWinners = winners.Any(b => b.Round == round);
        bool inLosers = losers.Any(b => b.Round == round);
        var seasonType = inWinners ? "playoffs_winners" : (inLosers ? "playoffs_losers" : "consolation");
        return (seasonType, label, isFinal);
    }

    private static string SummariseScoring(League league)
    {
        if (league.ScoringSettings is null) return "(custom scoring)";
        var rec = league.ScoringSettings.GetValueOrDefault("rec");
        var passTd = league.ScoringSettings.GetValueOrDefault("pass_td");
        var pprLabel = rec switch
        {
            >= 1m => "Full PPR",
            >= 0.5m => "Half PPR (0.5)",
            > 0m => $"PPR {rec}",
            _ => "Standard"
        };
        return $"{pprLabel}, {passTd:F0}pt passing TD";
    }

    private static bool TryGetPlayedScores(Matchup a, Matchup b, out decimal aPoints, out decimal bPoints)
    {
        aPoints = a.ScoreOrZero();
        bPoints = b.ScoreOrZero();
        return a.HasScoringData() || b.HasScoringData();
    }

    // ---------------- Weekly theme + previously-on-league ----------------

    private static (WeeklyTheme?, List<string>) LoadWeeklyTheme(int week)
    {
        try
        {
            var path = RecapPaths.WeeklyThemes;
            if (!File.Exists(path)) return (null, new());
            var doc = JsonSerializer.Deserialize<WeeklyThemesFile>(File.ReadAllText(path));
            if (doc is null) return (null, new());
            var entry = doc.Weeks?.FirstOrDefault(w => w.Week == week);
            if (entry is null) return (null, doc.GlobalBannedPhrases ?? new());

            // Lift any phrase the week explicitly allows.
            var banned = (doc.GlobalBannedPhrases ?? new())
                .Where(b => !(entry.BanLifts ?? new()).Any(l => string.Equals(l, b, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var theme = new WeeklyTheme(
                Week: entry.Week,
                Title: entry.Title ?? "",
                Vibe: entry.Vibe ?? "",
                BanLifts: entry.BanLifts ?? new());
            return (theme, banned);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (warning: failed to load weekly themes: {ex.Message})");
            return (null, new());
        }
    }

    private sealed class WeeklyThemesFile
    {
        public List<string>? GlobalBannedPhrases { get; set; }
        public List<WeeklyThemeEntry>? Weeks { get; set; }
    }

    private sealed class WeeklyThemeEntry
    {
        public int Week { get; set; }
        public string? Title { get; set; }
        public string? Vibe { get; set; }
        public List<string>? BanLifts { get; set; }
    }

    private PreviouslyOnLeague? BuildPreviouslyOnLeague(
        int season,
        int week,
        List<StandingsRow> standings,
        List<PowerRankingRow>? powerRankings,
        List<OwnerRef> owners,
        Dictionary<int, List<decimal>> perWeekPfByRoster,
        List<Roster> rosters,
        Dictionary<int, List<Matchup>> allWeekMatchups)
    {
        if (week <= 1) return null;
        int prior = week - 1;

        // Re-derive prior-week standings to compute rank deltas.
        var priorStandings = BuildStandingsAsOfWeek(rosters, owners, allWeekMatchups, prior);
        var priorRankByUser = priorStandings.ToDictionary(s => s.UserId, s => s.Rank);
        var realNameByUser = owners.ToDictionary(o => o.UserId, o => o.RealName ?? o.DisplayName ?? o.TeamName);

        var rankShifts = new List<RankShift>();
        foreach (var s in standings)
        {
            if (priorRankByUser.TryGetValue(s.UserId, out var fromRank))
            {
                int delta = fromRank - s.Rank; // positive = climbed
                if (Math.Abs(delta) >= 1)
                    rankShifts.Add(new RankShift(s.TeamName, realNameByUser.GetValueOrDefault(s.UserId, s.OwnerDisplay), fromRank, s.Rank, delta));
            }
        }
        rankShifts = rankShifts.OrderByDescending(r => Math.Abs(r.Delta)).Take(4).ToList();

        // Power-rank shifts (from the persisted power-history we already ranked this week).
        var priorPower = LoadPowerHistory(season, prior);
        var powerShifts = new List<RankShift>();
        if (powerRankings is not null)
        {
            foreach (var p in powerRankings)
            {
                if (priorPower.TryGetValue(p.UserId, out var fromRank))
                {
                    int delta = fromRank - p.Rank;
                    if (Math.Abs(delta) >= 3)
                        powerShifts.Add(new RankShift(p.TeamName, realNameByUser.GetValueOrDefault(p.UserId, p.OwnerDisplay), fromRank, p.Rank, delta));
                }
            }
        }
        powerShifts = powerShifts.OrderByDescending(r => Math.Abs(r.Delta)).Take(3).ToList();

        // Streak events: started (prior 0/1, now >=2), extended (prior n, now n+1 same kind), ended (prior >=2, now opposite).
        var streakEvents = new List<StreakEvent>();
        var priorStreaks = ComputeStreakByRoster(rosters, allWeekMatchups, prior);
        var thisWeekStreaks = ComputeStreakByRoster(rosters, allWeekMatchups, week);
        var rosterByUser = rosters.Where(r => r.OwnerId is not null).ToDictionary(r => r.OwnerId!, r => r.RosterId);
        foreach (var s in standings)
        {
            if (!rosterByUser.TryGetValue(s.UserId, out var rid)) continue;
            var prevS = priorStreaks.GetValueOrDefault(rid, "");
            var nowS = thisWeekStreaks.GetValueOrDefault(rid, "");
            int prevN = prevS.Length >= 2 && int.TryParse(prevS[1..], out var pn) ? pn : 0;
            int nowN = nowS.Length >= 2 && int.TryParse(nowS[1..], out var nn) ? nn : 0;
            char prevK = prevS.Length > 0 ? prevS[0] : '?';
            char nowK = nowS.Length > 0 ? nowS[0] : '?';
            string realName = realNameByUser.GetValueOrDefault(s.UserId, s.OwnerDisplay);

            if (prevN >= 3 && nowK != prevK && nowN >= 1)
                streakEvents.Add(new StreakEvent(s.TeamName, realName, "ended", $"{prevK}{prevN} -> {nowK}{nowN}", prevN));
            else if (nowN >= 4 && nowK == prevK && nowN > prevN)
                streakEvents.Add(new StreakEvent(s.TeamName, realName, "extended", nowS, nowN));
            else if (nowN == 2 && (prevN < 2 || prevK != nowK))
                streakEvents.Add(new StreakEvent(s.TeamName, realName, "started", nowS, nowN));
        }

        // Notes: top mover one-liner + biggest power swing.
        var notes = new List<string>();
        var biggestClimb = rankShifts.Where(r => r.Delta > 0).OrderByDescending(r => r.Delta).FirstOrDefault();
        var biggestFall = rankShifts.Where(r => r.Delta < 0).OrderBy(r => r.Delta).FirstOrDefault();
        if (biggestClimb is not null)
            notes.Add($"{biggestClimb.TeamName} climbed from #{biggestClimb.FromRank} to #{biggestClimb.ToRank} in the standings.");
        if (biggestFall is not null)
            notes.Add($"{biggestFall.TeamName} slid from #{biggestFall.FromRank} to #{biggestFall.ToRank}.");

        // Try to lift the headline of last week's recap (the first '##' header line).
        string? priorHeadline = null;
        try
        {
            var priorPath = Path.Combine(RecapPaths.RecapDir(season), $"week-{prior:D2}.md");
            if (File.Exists(priorPath))
            {
                var lines = File.ReadAllLines(priorPath);
                // Use the first paragraph after `## Intro` as the headline source.
                int introIdx = Array.FindIndex(lines, l => l.TrimStart().StartsWith("## Intro", StringComparison.OrdinalIgnoreCase));
                if (introIdx >= 0)
                {
                    for (int i = introIdx + 1; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                        priorHeadline = line.Length > 240 ? line[..240] + "..." : line;
                        break;
                    }
                }
            }
        }
        catch { /* non-fatal */ }

        return new PreviouslyOnLeague(prior, priorHeadline, rankShifts, powerShifts, streakEvents, notes);
    }

    // ---------------- Persistence schema ----------------

    private sealed class TeamNameHistoryFile
    {
        public int Season { get; set; }
        public List<TeamNameSnapshot> Snapshots { get; set; } = [];
    }

    private sealed class TeamNameSnapshot
    {
        public int Week { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }
        public Dictionary<string, TeamNameEntry> OwnerNames { get; set; } = new();
    }

    private sealed class TeamNameEntry
    {
        public string DisplayName { get; set; } = "";
        public string TeamName { get; set; } = "";
        public string Username { get; set; } = "";
    }
}

internal static class RecapPaths
{
    /// <summary>Repo-rooted path resolution: walk up from the running exe until we hit the workspace root.</summary>
    public static string WorkspaceRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                if (File.Exists(Path.Combine(dir, "Sleeper.slnx")) ||
                    File.Exists(Path.Combine(dir, "Sleeper.sln")))
                    return dir;
                var parent = Directory.GetParent(dir);
                if (parent is null) break;
                dir = parent.FullName;
            }
            return Environment.CurrentDirectory;
        }
    }

    public static string LorePath => Path.Combine(WorkspaceRoot, "docs", "league-lore.md");
    public static string RecapDir(int season) => Path.Combine(WorkspaceRoot, "recaps", season.ToString());
    public static string RecapFile(int season, int week) => Path.Combine(RecapDir(season), $"week-{week:D2}.md");
    public static string TeamNameHistory(int season) => Path.Combine(RecapDir(season), "team-name-history.json");
    public static string PowerHistory(int season) => Path.Combine(RecapDir(season), "power-history.json");
    public static string DataDir(int season) => Path.Combine(WorkspaceRoot, "datafiles", season.ToString());
    public static string DataFile(int season, int week) => Path.Combine(DataDir(season), $"week-{week:D2}.json");
    public static string TransactionHistory(int season) => Path.Combine(DataDir(season), "transactions.json");
    public static string AssetMovementAudit(int season) => Path.Combine(DataDir(season), "asset-movement-audit.json");
    public static string AssetMovementSummary(int season) => Path.Combine(DataDir(season), "asset-movement-summary.txt");
    public static string WeeklyThemes => Path.Combine(WorkspaceRoot, "datafiles", "weekly-themes.json");

    // Season-recap artifacts. All persisted alongside the weekly recaps so the
    // whole season's machine-readable state lives in one folder.
    public static string SeasonRecap(int season) => Path.Combine(RecapDir(season), "season.md");
    public static string SeasonAggregateJson(int season) => Path.Combine(RecapDir(season), "season-aggregate.json");
    public static string SeasonAwardsJson(int season) => Path.Combine(RecapDir(season), "season-awards.json");
    public static string SeasonOutcomeJson(int season) => Path.Combine(RecapDir(season), "season-outcome.json");
    public static string SeasonManifestJson(int season) => Path.Combine(RecapDir(season), "manifest.json");
    public static string SeasonChartsDir(int season) => Path.Combine(RecapDir(season), "charts");
    public static string SeasonChartFile(int season, string chartName) => Path.Combine(SeasonChartsDir(season), chartName + ".svg");
}
