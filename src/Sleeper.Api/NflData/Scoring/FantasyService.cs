using Sleeper.Api.Models;
using Sleeper.Api.NflData.Models;
using Sleeper.Api.Services;

namespace Sleeper.Api.NflData.Scoring;

public class FantasyService : IFantasyService
{
    private readonly ISleeperClient _sleeper;
    private readonly ISleeperService _sleeperService;
    private readonly INflDataClient _nflData;

    public FantasyService(ISleeperClient sleeper, ISleeperService sleeperService, INflDataClient nflData)
    {
        _sleeper = sleeper;
        _sleeperService = sleeperService;
        _nflData = nflData;
    }

    public async Task<ScoredPlayer?> GetPlayerWeekScoreAsync(string leagueId, string sleeperId, int season, int week, CancellationToken ct = default)
    {
        var scorerTask = GetScorerAsync(leagueId, ct);
        var weeklyTask = _nflData.GetWeeklyStatsBySleeperIdAsync(season, ct);

        await Task.WhenAll(scorerTask, weeklyTask).ConfigureAwait(false);

        var scorer = await scorerTask.ConfigureAwait(false);
        if (scorer is null) return null;

        var weeklyBySleeper = await weeklyTask.ConfigureAwait(false);
        if (!weeklyBySleeper.TryGetValue(sleeperId, out var weeks)) return null;

        var weekStats = weeks.FirstOrDefault(w => w.Week == week);
        if (weekStats is null) return null;

        return scorer.ScoreWeekly(weekStats);
    }

    public async Task<ScoredPlayer?> GetPlayerSeasonScoreAsync(string leagueId, string sleeperId, int season, CancellationToken ct = default)
    {
        var scorerTask = GetScorerAsync(leagueId, ct);
        var seasonTask = _nflData.GetSeasonStatsBySleeperIdAsync(season, "reg", ct);

        await Task.WhenAll(scorerTask, seasonTask).ConfigureAwait(false);

        var scorer = await scorerTask.ConfigureAwait(false);
        if (scorer is null) return null;

        var seasonBySleeper = await seasonTask.ConfigureAwait(false);
        if (!seasonBySleeper.TryGetValue(sleeperId, out var stats)) return null;

        return scorer.ScoreSeason(stats);
    }

    public async Task<List<ScoredPlayer>> GetRosterWeekScoresAsync(string leagueId, string username, int season, int week, CancellationToken ct = default)
    {
        var scorerTask = GetScorerAsync(leagueId, ct);
        var rosterTask = _sleeperService.GetMyRosterAsync(leagueId, username, ct);
        var weeklyTask = _nflData.GetWeeklyStatsBySleeperIdAsync(season, ct);

        await Task.WhenAll(scorerTask, rosterTask, weeklyTask).ConfigureAwait(false);

        var scorer = await scorerTask.ConfigureAwait(false);
        var roster = await rosterTask.ConfigureAwait(false);
        if (scorer is null || roster is null) return [];

        var weeklyBySleeper = await weeklyTask.ConfigureAwait(false);
        var allPlayers = await _sleeper.GetAllPlayersAsync("nfl", ct).ConfigureAwait(false);

        return ScoreRosterWeekly(roster, scorer, weeklyBySleeper, allPlayers, week);
    }

    public async Task<List<ScoredPlayer>> GetRosterSeasonScoresAsync(string leagueId, string username, int season, CancellationToken ct = default)
    {
        var scorerTask = GetScorerAsync(leagueId, ct);
        var rosterTask = _sleeperService.GetMyRosterAsync(leagueId, username, ct);
        var seasonTask = _nflData.GetSeasonStatsBySleeperIdAsync(season, "reg", ct);

        await Task.WhenAll(scorerTask, rosterTask, seasonTask).ConfigureAwait(false);

        var scorer = await scorerTask.ConfigureAwait(false);
        var roster = await rosterTask.ConfigureAwait(false);
        if (scorer is null || roster is null) return [];

        var seasonBySleeper = await seasonTask.ConfigureAwait(false);
        var allPlayers = await _sleeper.GetAllPlayersAsync("nfl", ct).ConfigureAwait(false);

        return ScoreRosterSeason(roster, scorer, seasonBySleeper, allPlayers);
    }

    // -- Helpers --

    private async Task<FantasyScorer?> GetScorerAsync(string leagueId, CancellationToken ct)
    {
        var league = await _sleeper.GetLeagueAsync(leagueId, ct).ConfigureAwait(false);
        if (league?.ScoringSettings is null) return null;
        return new FantasyScorer(league.ScoringSettings);
    }

    private static List<ScoredPlayer> ScoreRosterWeekly(
        Roster roster,
        FantasyScorer scorer,
        Dictionary<string, List<WeeklyPlayerStats>> weeklyBySleeper,
        Dictionary<string, Player> allPlayers,
        int week)
    {
        var results = new List<ScoredPlayer>();

        foreach (var playerId in roster.Players ?? [])
        {
            if (weeklyBySleeper.TryGetValue(playerId, out var weeks))
            {
                var weekStats = weeks.FirstOrDefault(w => w.Week == week);
                if (weekStats is not null)
                {
                    results.Add(scorer.ScoreWeekly(weekStats));
                    continue;
                }
            }

            // Player had no stats this week — return a zero-point entry with name
            var name = allPlayers.TryGetValue(playerId, out var p) ? p.FullName : playerId;
            var pos = p?.Position;
            results.Add(new ScoredPlayer(playerId, name, pos, 0m, []));
        }

        return results.OrderByDescending(s => s.TotalPoints).ToList();
    }

    private static List<ScoredPlayer> ScoreRosterSeason(
        Roster roster,
        FantasyScorer scorer,
        Dictionary<string, SeasonPlayerStats> seasonBySleeper,
        Dictionary<string, Player> allPlayers)
    {
        var results = new List<ScoredPlayer>();

        foreach (var playerId in roster.Players ?? [])
        {
            if (seasonBySleeper.TryGetValue(playerId, out var stats))
            {
                results.Add(scorer.ScoreSeason(stats));
                continue;
            }

            var name = allPlayers.TryGetValue(playerId, out var p) ? p.FullName : playerId;
            var pos = p?.Position;
            results.Add(new ScoredPlayer(playerId, name, pos, 0m, []));
        }

        return results.OrderByDescending(s => s.TotalPoints).ToList();
    }
}
