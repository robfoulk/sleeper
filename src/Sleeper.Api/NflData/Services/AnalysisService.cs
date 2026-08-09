using Sleeper.Api.Models;
using Sleeper.Api.NflData.Analytics;
using Sleeper.Api.NflData.Models;
using Sleeper.Api.NflData.Scoring;
using Sleeper.Api.Services;

namespace Sleeper.Api.NflData.Services;

public class AnalysisService : IAnalysisService
{
    private readonly ISleeperClient _sleeper;
    private readonly ISleeperService _sleeperService;
    private readonly INflDataClient _nflData;

    public AnalysisService(ISleeperClient sleeper, ISleeperService sleeperService, INflDataClient nflData)
    {
        _sleeper = sleeper;
        _sleeperService = sleeperService;
        _nflData = nflData;
    }

    public async Task<KeeperReport> GetKeeperAnalysisAsync(string leagueId, string username, int historyYears = 3, CancellationToken ct = default)
    {
        var user = await _sleeper.GetUserAsync(username, ct);
        var league = await _sleeper.GetLeagueAsync(leagueId, ct);
        if (user is null || league is null)
            return new KeeperReport("Unknown", "?", username, 0, 0, new(), 0, [], new(new(), new()));

        var config = LeagueRosterConfig.FromLeague(league);
        var scorer = new FantasyScorer(league.ScoringSettings ?? new());
        var currentSeason = int.TryParse(league.Season, out var s) ? s : DateTime.UtcNow.Year;

        // Determine last completed season
        var nflState = await _sleeper.GetNflStateAsync(ct);
        var lastCompleted = GetLastCompletedSeason(nflState, currentSeason);

        // Fetch multi-year stats in parallel
        var seasonStatsTasks = new Dictionary<int, Task<Dictionary<string, SeasonPlayerStats>>>();
        var weeklyStatsTasks = new Dictionary<int, Task<Dictionary<string, List<WeeklyPlayerStats>>>>();
        for (int y = lastCompleted; y > lastCompleted - historyYears; y--)
        {
            seasonStatsTasks[y] = _nflData.GetSeasonStatsBySleeperIdAsync(y, "reg", ct);
            weeklyStatsTasks[y] = _nflData.GetWeeklyStatsBySleeperIdAsync(y, ct);
        }
        await Task.WhenAll(Task.WhenAll(seasonStatsTasks.Values), Task.WhenAll(weeklyStatsTasks.Values));

        // League-wide rankings and replacement levels
        var lastSeasonAllStats = await _nflData.GetSeasonStatsAsync(lastCompleted, "reg", ct);
        var sleeperToGsis = await _nflData.GetSleeperToGsisMapAsync(ct);
        var ranker = new LeagueRanker(scorer);
        var rankings = ranker.RankBySleeperId(lastSeasonAllStats, sleeperToGsis);
        var replacementLevels = rankings.CalculateReplacementLevels(
            config.Teams,
            config.StarterSlots,
            config.FlexSlots,
            config.FlexEligiblePositions);

        var gamesNextSeason = DurabilityCalculator.GamesInSeason(currentSeason);
        var keeperValues = await _sleeperService.GetRosterKeeperValuesAsync(leagueId, username, ct: ct);

        var analyzer = new KeeperAnalyzer(replacementLevels);
        var analyses = new List<PlayerAnalysis>();

        foreach (var kv in keeperValues)
        {
            var p = kv.Player;
            if (p.Position is "DEF") continue;

            var seasonHistory = new List<SeasonSummary>();
            var recentWeekly = new List<decimal>();

            for (int y = lastCompleted; y > lastCompleted - historyYears; y--)
            {
                if (seasonStatsTasks[y].Result.TryGetValue(p.PlayerId, out var ss))
                {
                    var scored = scorer.ScoreSeason(ss);
                    var games = ss.Games ?? 0;
                    var ppg = games > 0 ? Math.Round(scored.TotalPoints / games, 2) : 0m;

                    var weeklyPts = new List<decimal>();
                    if (weeklyStatsTasks[y].Result.TryGetValue(p.PlayerId, out var weeks))
                    {
                        weeklyPts = weeks.Where(w => w.SeasonType == "REG")
                            .Select(w => scorer.ScoreWeekly(w).TotalPoints).ToList();
                        if (y == lastCompleted) recentWeekly = weeklyPts;
                    }

                    var stdDev = weeklyPts.Count >= 2 ? ConsistencyCalculator.CalculateStdDev(weeklyPts) : 0m;
                    seasonHistory.Add(new SeasonSummary(y, games, scored.TotalPoints, ppg, Math.Round(stdDev, 2)));
                }
            }

            analyses.Add(analyzer.Analyze(p.PlayerId, p.FullName, p.Position, p.Age,
                kv.KeeperCostRound, kv.CanBeKept, seasonHistory, recentWeekly, gamesNextSeason));
        }

        analyses = analyses.OrderByDescending(a => a.KeeperScore).ToList();

        return new KeeperReport(
            LeagueName: league.Name ?? "Unknown",
            Season: league.Season ?? "?",
            OwnerName: user.DisplayName ?? username,
            Teams: config.Teams,
            MaxKeepers: config.MaxKeepers,
            ReplacementLevels: replacementLevels,
            LastCompletedSeason: lastCompleted,
            Analyses: analyses,
            Rankings: rankings
        );
    }

    public async Task<PlayerDeepDive?> GetPlayerDeepDiveAsync(string leagueId, string playerName, int historyYears = 3, CancellationToken ct = default)
    {
        var league = await _sleeper.GetLeagueAsync(leagueId, ct);
        if (league is null) return null;

        var scorer = new FantasyScorer(league.ScoringSettings ?? new());
        var currentSeason = int.TryParse(league.Season, out var s) ? s : DateTime.UtcNow.Year;
        var nflState = await _sleeper.GetNflStateAsync(ct);
        var lastCompleted = GetLastCompletedSeason(nflState, currentSeason);

        // Find player
        var allPlayers = await _sleeper.GetAllPlayersAsync("nfl", ct);
        var searchName = playerName.ToLowerInvariant();
        var match = allPlayers.Values.FirstOrDefault(p =>
            p.FullName.ToLowerInvariant().Contains(searchName) ||
            (p.SearchFullName?.Contains(searchName.Replace(" ", "")) ?? false));
        if (match is null) return null;

        var seasonHistory = new List<SeasonSummary>();
        var allWeeklyPoints = new Dictionary<int, List<WeeklyFantasyScore>>();

        for (int y = lastCompleted; y > lastCompleted - historyYears; y--)
        {
            var seasonStats = await _nflData.GetSeasonStatsBySleeperIdAsync(y, "reg", ct);
            var weeklyStats = await _nflData.GetWeeklyStatsBySleeperIdAsync(y, ct);

            if (seasonStats.TryGetValue(match.PlayerId, out var ss))
            {
                var scored = scorer.ScoreSeason(ss);
                var games = ss.Games ?? 0;
                var ppg = games > 0 ? Math.Round(scored.TotalPoints / games, 2) : 0m;

                var weeklyPts = new List<decimal>();
                if (weeklyStats.TryGetValue(match.PlayerId, out var weeks))
                {
                    weeklyPts = weeks.Where(w => w.SeasonType == "REG")
                        .OrderBy(w => w.Week)
                        .Select(w => scorer.ScoreWeekly(w).TotalPoints).ToList();
                    allWeeklyPoints[y] = weeks
                        .Where(w => w.SeasonType == "REG")
                        .OrderBy(w => w.Week)
                        .Select(w => new WeeklyFantasyScore(w.Week, scorer.ScoreWeekly(w).TotalPoints))
                        .ToList();
                }

                var stdDev = weeklyPts.Count >= 2 ? ConsistencyCalculator.CalculateStdDev(weeklyPts) : 0m;
                seasonHistory.Add(new SeasonSummary(y, games, scored.TotalPoints, ppg, Math.Round(stdDev, 2)));
            }
        }

        // Rankings
        var lastSeasonAllStats = await _nflData.GetSeasonStatsAsync(lastCompleted, "reg", ct);
        var sleeperToGsis = await _nflData.GetSleeperToGsisMapAsync(ct);
        var ranker = new LeagueRanker(scorer);
        var rankings = ranker.RankBySleeperId(lastSeasonAllStats, sleeperToGsis);

        var recentYear = seasonHistory.Count > 0 ? seasonHistory.OrderByDescending(sh => sh.Season).First().Season : lastCompleted;
        var recentWkPts = allWeeklyPoints
            .GetValueOrDefault(recentYear, [])
            .Select(score => score.Points)
            .ToList();
        var analyzer = new KeeperAnalyzer();
        var analysis = analyzer.Analyze(match.PlayerId, match.FullName, match.Position, match.Age,
            null, false, seasonHistory, recentWkPts);

        return new PlayerDeepDive(match, seasonHistory, allWeeklyPoints, analysis, rankings);
    }

    public async Task<LeagueRankings> GetLeagueRankingsAsync(string leagueId, int season, CancellationToken ct = default)
    {
        var league = await _sleeper.GetLeagueAsync(leagueId, ct);
        if (league is null) return new LeagueRankings(new(), new());

        var scorer = new FantasyScorer(league.ScoringSettings ?? new());
        var allStats = await _nflData.GetSeasonStatsAsync(season, "reg", ct);
        var sleeperToGsis = await _nflData.GetSleeperToGsisMapAsync(ct);
        var ranker = new LeagueRanker(scorer);
        return ranker.RankBySleeperId(allStats, sleeperToGsis);
    }

    public async Task<RosterEvaluation?> EvaluateRosterAsync(string leagueId, string username, CancellationToken ct = default)
    {
        var user = await _sleeper.GetUserAsync(username, ct);
        var league = await _sleeper.GetLeagueAsync(leagueId, ct);
        if (user is null || league is null) return null;

        var config = LeagueRosterConfig.FromLeague(league);
        var currentSeason = int.TryParse(league.Season, out var s) ? s : DateTime.UtcNow.Year;
        var nflState = await _sleeper.GetNflStateAsync(ct);
        var lastCompleted = GetLastCompletedSeason(nflState, currentSeason);

        var rankings = await GetLeagueRankingsAsync(leagueId, lastCompleted, ct);
        var roster = await _sleeperService.GetMyRosterAsync(leagueId, username, ct);
        if (roster is null) return null;

        var fantasyPositions = new[] { "QB", "RB", "WR", "TE", "K" };
        var positions = new Dictionary<string, PositionalStrength>();
        var strengths = new List<string>();
        var weaknesses = new List<string>();
        var recommendations = new List<string>();

        foreach (var pos in fantasyPositions)
        {
            var starterSlots = config.StarterSlots.GetValueOrDefault(pos, 0);
            var playersAtPos = new List<RankedPlayerSummary>();

            foreach (var playerId in roster.Players ?? [])
            {
                if (rankings.BySleeperId.TryGetValue(playerId, out var ranked) &&
                    string.Equals(ranked.Position, pos, StringComparison.OrdinalIgnoreCase))
                {
                    playersAtPos.Add(new RankedPlayerSummary(ranked.PlayerName, ranked.Position, ranked.PositionalRank, ranked.Ppg));
                }
            }

            playersAtPos = playersAtPos.OrderBy(p => p.PositionalRank).ToList();
            var avgPpg = playersAtPos.Count > 0 ? Math.Round(playersAtPos.Average(p => p.Ppg), 1) : 0m;

            var bestRank = playersAtPos.Count > 0 ? playersAtPos.Min(p => p.PositionalRank) : 999;
            var rating = bestRank switch
            {
                <= 5 => "Elite",
                <= 15 => "Strong",
                <= 30 => "Average",
                <= 50 => "Weak",
                _ => "Very Weak"
            };

            positions[pos] = new PositionalStrength(pos, playersAtPos.Count, starterSlots, playersAtPos, avgPpg, rating);

            if (bestRank <= 10) strengths.Add($"{pos}: {playersAtPos[0].Name} ({pos}{bestRank}) is an elite starter");
            if (playersAtPos.Count < starterSlots) weaknesses.Add($"{pos}: Only {playersAtPos.Count} players for {starterSlots} starter slots");
            else if (bestRank > 30) weaknesses.Add($"{pos}: No top-30 {pos} on roster (best: {pos}{bestRank})");

            if (playersAtPos.Count < starterSlots)
                recommendations.Add($"Priority: Acquire {starterSlots - playersAtPos.Count} more {pos}(s)");
            else if (bestRank > 40)
                recommendations.Add($"Consider upgrading {pos} -- best player is {pos}{bestRank}");
        }

        return new RosterEvaluation(
            OwnerName: user.DisplayName ?? username,
            LeagueName: league.Name ?? "Unknown",
            Positions: positions,
            Strengths: strengths,
            Weaknesses: weaknesses,
            Recommendations: recommendations
        );
    }

    public async Task<List<Sleeper.Api.Models.Player>> SearchPlayersAsync(string query, string? position = null, CancellationToken ct = default)
    {
        var allPlayers = await _sleeper.GetAllPlayersAsync("nfl", ct);
        var search = query.ToLowerInvariant().Replace(" ", "");

        return allPlayers.Values
            .Where(p =>
                (p.SearchFullName?.Contains(search) ?? false) ||
                p.FullName.ToLowerInvariant().Replace(" ", "").Contains(search))
            .Where(p => position is null || string.Equals(p.Position, position, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.SearchRank ?? 999999)
            .Take(20)
            .ToList();
    }

    internal static int GetLastCompletedSeason(NflState? nflState, int currentSeason)
    {
        if (nflState is null) return currentSeason - 1;
        return int.TryParse(nflState.PreviousSeason, out var previousSeason)
            ? previousSeason
            : currentSeason - 1;
    }
}
