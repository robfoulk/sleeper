using System.Text.Json;
using Sleeper.Api;
using Sleeper.Api.Models;
using Sleeper.Api.NflData;
using Sleeper.Api.Services;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Builds the deterministic <see cref="SeasonAggregate"/> at season's end
/// from data already on disk + per-week matchups via the Sleeper API. No AI
/// calls. Persists the aggregate, the awards, and the season outcome as JSON
/// sidecars before any prose pass runs.
/// </summary>
internal sealed class SeasonAggregateBuilder
{
    private readonly ISleeperClient _sleeper;
    private readonly ISleeperService _sleeperService;
    private readonly INflDataClient _nfl;
    private readonly LeagueLore _lore;

    public SeasonAggregateBuilder(
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

    public async Task<(SeasonAggregate Aggregate, SeasonAwards Awards)> BuildAsync(
        string leagueId,
        int? overrideSeason,
        CancellationToken ct = default)
    {
        // 1. Build the championship-week envelope. This gives us Owners, final Standings,
        //    Schedule, and the resolved SeasonOutcome (placements + next-year draft picks).
        var envBuilder = new RecapEnvelopeBuilder(_sleeper, _sleeperService, _nfl, _lore);
        var envelope = await envBuilder.BuildAsync(
            leagueId,
            RecapEnvelopeBuilder.Schedule.ChampionshipWeek,
            overrideSeason,
            ct,
            new RecapEnvelopeBuildOptions(PersistSnapshots: false)).ConfigureAwait(false);

        var schedule = envelope.Schedule ?? RecapEnvelopeBuilder.Schedule;
        int season = envelope.Meta.Season;

        // 2. Pull every week's matchups (1..ChampionshipWeek). We need the raw scores
        //    for the per-team weekly series and the score-rank chart.
        var perWeekMatchups = new Dictionary<int, List<Matchup>>();
        for (int w = 1; w <= schedule.ChampionshipWeek; w++)
        {
            try { perWeekMatchups[w] = await _sleeper.GetLeagueMatchupsAsync(leagueId, w, ct).ConfigureAwait(false); }
            catch { perWeekMatchups[w] = []; }
        }

        // 3. Read power-history.json (written by RecapEnvelopeBuilder during each weekly recap).
        var powerByWeek = LoadAllPowerSnapshots(season);

        // 4. Build each owner's per-week series.
        var rosters = await _sleeper.GetLeagueRostersAsync(leagueId, ct).ConfigureAwait(false);
        var ownerByRoster = envelope.Owners.ToDictionary(o => o.RosterId, o => o);
        var ownerByUser = envelope.Owners.ToDictionary(o => o.UserId, o => o);

        var teams = new List<SeasonTeamSeries>();
        foreach (var owner in envelope.Owners.OrderBy(o => o.RosterId))
        {
            var weekly = new List<WeeklyEntry>();
            int cw = 0, cl = 0, ct2 = 0;
            decimal seasonHigh = decimal.MinValue;
            int seasonHighWeek = 0;
            decimal seasonLow = decimal.MaxValue;
            int seasonLowWeek = 0;
            int curWinStreak = 0, curLossStreak = 0, longestWin = 0, longestLoss = 0;
            decimal pfTotal = 0, paTotal = 0;
            decimal cumPf = 0, cumPa = 0;       // Running totals; only advance during the regular season so the chart line goes flat in the playoffs (cleaner read than a sudden bump from a playoff blowout for one team).
            int totalWins = 0, totalLosses = 0, totalTies = 0;
            decimal totalPf = 0, totalPa = 0;
            string? lastTeamName = null;

            for (int w = 1; w <= schedule.ChampionshipWeek; w++)
            {
                var weekMatchups = perWeekMatchups.GetValueOrDefault(w) ?? [];
                var mine = weekMatchups.FirstOrDefault(m => m.RosterId == owner.RosterId);
                if (mine is null) { weekly.Add(new WeeklyEntry(w, 0, 0, null, null, null, cw, cl, ct2, lastTeamName, cumPf, cumPa, cumPf - cumPa)); continue; }
                var opp = weekMatchups.FirstOrDefault(m => m.MatchupId == mine.MatchupId && m.RosterId != owner.RosterId);

                decimal myPts = mine.ScoreOrZero();
                decimal oppPts = opp?.ScoreOrZero() ?? 0m;
                int? result = opp is null || !PairHasPlayedScore(mine, opp) ? null : (myPts > oppPts ? 1 : myPts < oppPts ? -1 : 0);

                // Regular-season-only roll-up for cumulative record (matches Sleeper's standings).
                if (w <= schedule.RegularSeasonLastWeek && result is not null)
                {
                    if (result == 1) { cw++; curWinStreak++; curLossStreak = 0; }
                    else if (result == -1) { cl++; curLossStreak++; curWinStreak = 0; }
                    else { ct2++; curWinStreak = 0; curLossStreak = 0; }
                    if (curWinStreak > longestWin) longestWin = curWinStreak;
                    if (curLossStreak > longestLoss) longestLoss = curLossStreak;
                    pfTotal += myPts;
                    paTotal += oppPts;
                    cumPf += myPts;
                    cumPa += oppPts;
                }

                // Total (full-season) record + PF/PA — includes playoffs and consolation games.
                if (result is not null)
                {
                    if (result == 1) totalWins++;
                    else if (result == -1) totalLosses++;
                    else totalTies++;
                    totalPf += myPts;
                    totalPa += oppPts;
                }

                if (myPts > seasonHigh) { seasonHigh = myPts; seasonHighWeek = w; }
                if (myPts < seasonLow && myPts > 0) { seasonLow = myPts; seasonLowWeek = w; }

                int? powerRank = powerByWeek.TryGetValue(w, out var ranks) && ranks.TryGetValue(owner.UserId, out var pr) ? pr : null;
                int? scoreRank = ComputeScoreRank(weekMatchups, owner.RosterId);

                weekly.Add(new WeeklyEntry(w, myPts, oppPts, result, powerRank, scoreRank, cw, cl, ct2, owner.TeamName, Math.Round(cumPf, 2), Math.Round(cumPa, 2), Math.Round(cumPf - cumPa, 2)));
                lastTeamName = owner.TeamName;
            }

            // Final placement / draft pick from SeasonOutcome (when the season is finished).
            int? finalPlace = null;
            int? draftPick = null;
            if (envelope.SeasonOutcome is not null)
            {
                var so = envelope.SeasonOutcome;
                var placement = new[] { so.Champion, so.RunnerUp, so.ThirdPlace, so.FourthPlace, so.ConsolationFifth, so.ConsolationSixth, so.ConsolationSeventh, so.ConsolationLast }
                    .FirstOrDefault(p => p.UserId == owner.UserId);
                if (placement is not null)
                {
                    finalPlace = placement.FinalPlace;
                    draftPick = placement.DraftPick;
                }
            }

            teams.Add(new SeasonTeamSeries(
                RosterId: owner.RosterId,
                UserId: owner.UserId,
                OwnerRealName: owner.RealName ?? owner.DisplayName ?? owner.TeamName,
                FinalTeamName: owner.TeamName,
                Generation: owner.Generation,
                Weekly: weekly,
                RegularSeasonWins: cw,
                RegularSeasonLosses: cl,
                RegularSeasonTies: ct2,
                RegularSeasonPointsFor: Math.Round(pfTotal, 2),
                RegularSeasonPointsAgainst: Math.Round(paTotal, 2),
                TotalWins: totalWins,
                TotalLosses: totalLosses,
                TotalTies: totalTies,
                TotalPointsFor: Math.Round(totalPf, 2),
                TotalPointsAgainst: Math.Round(totalPa, 2),
                SeasonHighScore: seasonHigh == decimal.MinValue ? 0 : Math.Round(seasonHigh, 2),
                SeasonHighScoreWeek: seasonHighWeek,
                SeasonLowScore: seasonLow == decimal.MaxValue ? 0 : Math.Round(seasonLow, 2),
                SeasonLowScoreWeek: seasonLowWeek,
                LongestWinStreak: longestWin,
                LongestLossStreak: longestLoss,
                FinalPlace: finalPlace,
                NextYearDraftPick: draftPick));
        }

        // 5. Weekly champions (highest scorer of each played week).
        var weeklyChamps = new List<WeeklyChampion>();
        for (int w = 1; w <= schedule.ChampionshipWeek; w++)
        {
            var weekMatchups = perWeekMatchups.GetValueOrDefault(w) ?? [];
            if (weekMatchups.Count == 0) continue;
            var top = weekMatchups
                .Where(m => m.HasScoringData())
                .OrderByDescending(m => m.ScoreOrZero())
                .FirstOrDefault();
            if (top is null || !ownerByRoster.TryGetValue(top.RosterId, out var topOwner)) continue;
            weeklyChamps.Add(new WeeklyChampion(
                Week: w,
                UserId: topOwner.UserId,
                OwnerRealName: topOwner.RealName ?? topOwner.DisplayName ?? topOwner.TeamName,
                TeamName: topOwner.TeamName,
                PointsFor: Math.Round(top.ScoreOrZero(), 2)));
        }

        // 6. Highlights (single-week highs/lows, blowouts, upsets).
        var highlights = BuildHighlights(perWeekMatchups, ownerByRoster, powerByWeek, schedule);

        // 7. Compose aggregate.
        var aggregate = new SeasonAggregate(
            Season: season,
            LeagueId: leagueId,
            LeagueName: envelope.Meta.LeagueName,
            GeneratedAt: DateTimeOffset.UtcNow,
            Schedule: schedule,
            Owners: envelope.Owners,
            Teams: teams,
            Highlights: highlights,
            WeeklyChampions: weeklyChamps,
            Outcome: envelope.SeasonOutcome);

        // 8. Awards (deterministic — agent narrates, doesn't pick).
        var awards = BuildAwards(aggregate, season, leagueId);

        return (aggregate, awards);
    }

    // ---------------- Helpers ----------------

    private static int? ComputeScoreRank(List<Matchup> weekMatchups, int rosterId)
    {
        var ordered = weekMatchups
            .Where(m => m.HasScoringData())
            .OrderByDescending(m => m.ScoreOrZero())
            .ToList();
        var idx = ordered.FindIndex(m => m.RosterId == rosterId);
        return idx < 0 ? null : idx + 1;
    }

    private static Dictionary<int, Dictionary<string, int>> LoadAllPowerSnapshots(int season)
    {
        var result = new Dictionary<int, Dictionary<string, int>>();
        try
        {
            var path = RecapPaths.PowerHistory(season);
            if (!File.Exists(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Snapshots", out var snaps)) return result;
            foreach (var snap in snaps.EnumerateArray())
            {
                if (!snap.TryGetProperty("Week", out var wEl) || wEl.ValueKind != JsonValueKind.Number) continue;
                int w = wEl.GetInt32();
                var ranks = new Dictionary<string, int>();
                if (snap.TryGetProperty("Ranks", out var ranksEl) && ranksEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in ranksEl.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number)
                            ranks[prop.Name] = prop.Value.GetInt32();
                    }
                }
                result[w] = ranks;
            }
        }
        catch { }
        return result;
    }

    private static SeasonHighlights BuildHighlights(
        Dictionary<int, List<Matchup>> perWeekMatchups,
        Dictionary<int, OwnerRef> ownerByRoster,
        Dictionary<int, Dictionary<string, int>> powerByWeek,
        LeagueSchedule schedule)
    {
        SingleScoreNote? hi = null, lo = null;
        GameNote? blowout = null, narrow = null, upset = null;

        for (int w = 1; w <= schedule.ChampionshipWeek; w++)
        {
            var weekMatchups = perWeekMatchups.GetValueOrDefault(w) ?? [];
            foreach (var m in weekMatchups)
            {
                if (!m.HasScoringData()) continue;
                if (!ownerByRoster.TryGetValue(m.RosterId, out var owner)) continue;
                decimal pts = m.ScoreOrZero();
                if (hi is null || pts > hi.Value) hi = new SingleScoreNote(w, owner.UserId, owner.RealName ?? owner.DisplayName ?? owner.TeamName, owner.TeamName, Math.Round(pts, 2), null);
                if (pts > 0 && (lo is null || pts < lo.Value)) lo = new SingleScoreNote(w, owner.UserId, owner.RealName ?? owner.DisplayName ?? owner.TeamName, owner.TeamName, Math.Round(pts, 2), null);
            }

            // Game-pair iteration for blowout / narrow / upset.
            foreach (var grp in weekMatchups.GroupBy(m => m.MatchupId))
            {
                var pair = grp.ToList();
                if (pair.Count != 2) continue;
                var a = pair[0]; var b = pair[1];
                if (!PairHasPlayedScore(a, b)) continue;
                decimal aPts = a.ScoreOrZero();
                decimal bPts = b.ScoreOrZero();
                if (aPts == bPts) continue;
                var (winner, loser, wPts, lPts) = aPts > bPts ? (a, b, aPts, bPts) : (b, a, bPts, aPts);
                if (!ownerByRoster.TryGetValue(winner.RosterId, out var wOwner)) continue;
                if (!ownerByRoster.TryGetValue(loser.RosterId, out var lOwner)) continue;
                decimal margin = wPts - lPts;

                int? wPowerAtTime = powerByWeek.TryGetValue(w - 1, out var prevRanks) && prevRanks.TryGetValue(wOwner.UserId, out var wp) ? wp : null;
                int? lPowerAtTime = powerByWeek.TryGetValue(w - 1, out var prevRanks2) && prevRanks2.TryGetValue(lOwner.UserId, out var lp) ? lp : null;

                var note = new GameNote(
                    Week: w,
                    WinnerUserId: wOwner.UserId,
                    WinnerTeamName: wOwner.TeamName,
                    LoserUserId: lOwner.UserId,
                    LoserTeamName: lOwner.TeamName,
                    WinnerScore: Math.Round(wPts, 2),
                    LoserScore: Math.Round(lPts, 2),
                    Margin: Math.Round(margin, 2),
                    WinnerPowerRankAtTime: wPowerAtTime,
                    LoserPowerRankAtTime: lPowerAtTime);

                if (blowout is null || margin > blowout.Margin) blowout = note;
                if (narrow is null || margin < narrow.Margin) narrow = note;
                // Upset: the loser was ranked better than the winner going into the week.
                if (wPowerAtTime is not null && lPowerAtTime is not null && lPowerAtTime < wPowerAtTime)
                {
                    if (upset is null || margin > upset.Margin) upset = note;
                }
            }
        }

        // Best/worst regular-season PF (totals).
        SingleScoreNote? bestPf = null, worstPf = null;
        var pfByOwner = new Dictionary<string, (decimal Pf, OwnerRef Owner)>();
        for (int w = 1; w <= schedule.RegularSeasonLastWeek; w++)
        {
            foreach (var m in perWeekMatchups.GetValueOrDefault(w) ?? [])
            {
                if (!m.HasScoringData()) continue;
                if (!ownerByRoster.TryGetValue(m.RosterId, out var owner)) continue;
                if (!pfByOwner.TryGetValue(owner.UserId, out var cur)) cur = (0m, owner);
                cur.Pf += m.ScoreOrZero();
                pfByOwner[owner.UserId] = cur;
            }
        }
        foreach (var (uid, (pf, o)) in pfByOwner)
        {
            var note = new SingleScoreNote(0, uid, o.RealName ?? o.DisplayName ?? o.TeamName, o.TeamName, Math.Round(pf, 2), $"Total regular-season points for ({schedule.RegularSeasonLastWeek} games).");
            if (bestPf is null || pf > bestPf.Value) bestPf = note;
            if (worstPf is null || pf < worstPf.Value) worstPf = note;
        }

        return new SeasonHighlights(
            HighestSingleWeek: hi,
            LowestSingleWeek: lo,
            BiggestBlowout: blowout,
            NarrowestWin: narrow,
            BiggestUpset: upset,
            BestRegularSeasonPF: bestPf,
            WorstRegularSeasonPF: worstPf);
    }

    private static SeasonAwards BuildAwards(SeasonAggregate agg, int season, string leagueId)
    {
        var list = new List<SeasonAward>();
        var h = agg.Highlights;

        if (h.HighestSingleWeek is not null)
            list.Add(new SeasonAward(
                Name: "Single-Week Showstopper",
                Description: "Most points scored by any team in a single week of the season.",
                UserId: h.HighestSingleWeek.UserId, OwnerRealName: h.HighestSingleWeek.OwnerRealName, TeamName: h.HighestSingleWeek.TeamName,
                PlayerId: null, PlayerName: null,
                Metric: "single-week points for", Value: h.HighestSingleWeek.Value,
                Week: h.HighestSingleWeek.Week, Citation: $"Scored {h.HighestSingleWeek.Value:F2} in week {h.HighestSingleWeek.Week}."));

        if (h.LowestSingleWeek is not null)
            list.Add(new SeasonAward(
                Name: "The Cold Front",
                Description: "Fewest points scored by any team in a single non-zero week.",
                UserId: h.LowestSingleWeek.UserId, OwnerRealName: h.LowestSingleWeek.OwnerRealName, TeamName: h.LowestSingleWeek.TeamName,
                PlayerId: null, PlayerName: null,
                Metric: "single-week points for", Value: h.LowestSingleWeek.Value,
                Week: h.LowestSingleWeek.Week, Citation: $"Scored {h.LowestSingleWeek.Value:F2} in week {h.LowestSingleWeek.Week}."));

        if (h.BiggestBlowout is not null)
            list.Add(new SeasonAward(
                Name: "Biggest Blowout",
                Description: "Largest single-game margin of victory.",
                UserId: h.BiggestBlowout.WinnerUserId, OwnerRealName: null, TeamName: h.BiggestBlowout.WinnerTeamName,
                PlayerId: null, PlayerName: null,
                Metric: "margin of victory", Value: h.BiggestBlowout.Margin,
                Week: h.BiggestBlowout.Week,
                Citation: $"{h.BiggestBlowout.WinnerTeamName} {h.BiggestBlowout.WinnerScore:F2} def. {h.BiggestBlowout.LoserTeamName} {h.BiggestBlowout.LoserScore:F2} (week {h.BiggestBlowout.Week})."));

        if (h.NarrowestWin is not null)
            list.Add(new SeasonAward(
                Name: "Razor's Edge",
                Description: "Narrowest single-game margin of victory.",
                UserId: h.NarrowestWin.WinnerUserId, OwnerRealName: null, TeamName: h.NarrowestWin.WinnerTeamName,
                PlayerId: null, PlayerName: null,
                Metric: "margin of victory", Value: h.NarrowestWin.Margin,
                Week: h.NarrowestWin.Week,
                Citation: $"{h.NarrowestWin.WinnerTeamName} {h.NarrowestWin.WinnerScore:F2} def. {h.NarrowestWin.LoserTeamName} {h.NarrowestWin.LoserScore:F2} (week {h.NarrowestWin.Week})."));

        if (h.BiggestUpset is not null)
            list.Add(new SeasonAward(
                Name: "Upset of the Year",
                Description: "Biggest victory by a team whose power-rank entering the week was worse than its opponent's.",
                UserId: h.BiggestUpset.WinnerUserId, OwnerRealName: null, TeamName: h.BiggestUpset.WinnerTeamName,
                PlayerId: null, PlayerName: null,
                Metric: "margin of victory (lower-power-ranked team)", Value: h.BiggestUpset.Margin,
                Week: h.BiggestUpset.Week,
                Citation: $"{h.BiggestUpset.WinnerTeamName} (power #{h.BiggestUpset.WinnerPowerRankAtTime}) over {h.BiggestUpset.LoserTeamName} (power #{h.BiggestUpset.LoserPowerRankAtTime}) by {h.BiggestUpset.Margin:F2} (week {h.BiggestUpset.Week})."));

        if (h.BestRegularSeasonPF is not null)
            list.Add(new SeasonAward(
                Name: "Points Crown",
                Description: "Most regular-season points scored.",
                UserId: h.BestRegularSeasonPF.UserId, OwnerRealName: h.BestRegularSeasonPF.OwnerRealName, TeamName: h.BestRegularSeasonPF.TeamName,
                PlayerId: null, PlayerName: null,
                Metric: "regular-season points for", Value: h.BestRegularSeasonPF.Value,
                Week: null, Citation: $"Scored {h.BestRegularSeasonPF.Value:F2} across the regular season."));

        // Iron Team — longest win streak in the regular season.
        var ironTeam = agg.Teams.OrderByDescending(t => t.LongestWinStreak).ThenByDescending(t => t.RegularSeasonWins).FirstOrDefault();
        if (ironTeam is not null && ironTeam.LongestWinStreak >= 3)
            list.Add(new SeasonAward(
                Name: "Iron Team",
                Description: "Longest regular-season winning streak.",
                UserId: ironTeam.UserId, OwnerRealName: ironTeam.OwnerRealName, TeamName: ironTeam.FinalTeamName,
                PlayerId: null, PlayerName: null,
                Metric: "consecutive regular-season wins", Value: ironTeam.LongestWinStreak,
                Week: null, Citation: $"Won {ironTeam.LongestWinStreak} regular-season games in a row."));

        // The Cooler — longest loss streak.
        var cooler = agg.Teams.OrderByDescending(t => t.LongestLossStreak).ThenBy(t => t.RegularSeasonWins).FirstOrDefault();
        if (cooler is not null && cooler.LongestLossStreak >= 3)
            list.Add(new SeasonAward(
                Name: "The Cooler",
                Description: "Longest regular-season losing streak.",
                UserId: cooler.UserId, OwnerRealName: cooler.OwnerRealName, TeamName: cooler.FinalTeamName,
                PlayerId: null, PlayerName: null,
                Metric: "consecutive regular-season losses", Value: cooler.LongestLossStreak,
                Week: null, Citation: $"Lost {cooler.LongestLossStreak} regular-season games in a row."));

        // Comeback Team — biggest power-rank improvement from week 1 to the final regular-season week.
        SeasonTeamSeries? comeback = null;
        int comebackDelta = 0;
        foreach (var t in agg.Teams)
        {
            int? w1 = t.Weekly.FirstOrDefault(e => e.Week == 1)?.PowerRank;
            int? w15 = t.Weekly.FirstOrDefault(e => e.Week == agg.Schedule.RegularSeasonLastWeek)?.PowerRank;
            if (w1 is null || w15 is null) continue;
            int delta = w1.Value - w15.Value;
            if (delta > comebackDelta) { comebackDelta = delta; comeback = t; }
        }
        if (comeback is not null && comebackDelta >= 1)
            list.Add(new SeasonAward(
                Name: "Comeback Team",
                Description: $"Biggest power-rank improvement from week 1 to week {agg.Schedule.RegularSeasonLastWeek}.",
                UserId: comeback.UserId, OwnerRealName: comeback.OwnerRealName, TeamName: comeback.FinalTeamName,
                PlayerId: null, PlayerName: null,
                Metric: "power-rank delta (W1 → final regular-season week, lower is better)", Value: comebackDelta,
                Week: null, Citation: $"Climbed {comebackDelta} power-rank spots between week 1 and week {agg.Schedule.RegularSeasonLastWeek}."));

        // Heartbreak — most close losses (margin <= 5).
        SeasonTeamSeries? heartbreak = null;
        int closeLosses = 0;
        foreach (var t in agg.Teams)
        {
            int n = t.Weekly.Count(e => e.Week <= agg.Schedule.RegularSeasonLastWeek
                && e.Result == -1
                && Math.Abs(e.PointsFor - e.PointsAgainst) <= 5m);
            if (n > closeLosses) { closeLosses = n; heartbreak = t; }
        }
        if (heartbreak is not null && closeLosses >= 2)
            list.Add(new SeasonAward(
                Name: "Heartbreak of the Year",
                Description: "Most regular-season losses by 5 points or fewer.",
                UserId: heartbreak.UserId, OwnerRealName: heartbreak.OwnerRealName, TeamName: heartbreak.FinalTeamName,
                PlayerId: null, PlayerName: null,
                Metric: "close losses (margin ≤ 5)", Value: closeLosses,
                Week: null, Citation: $"Suffered {closeLosses} regular-season losses by 5 points or fewer."));

        return new SeasonAwards(season, leagueId, DateTimeOffset.UtcNow, list);
    }

    private static bool PairHasPlayedScore(Matchup a, Matchup b)
        => a.HasScoringData() || b.HasScoringData();
}
