using Sleeper.Api.NflData.Models;
using Sleeper.Api.NflData.Scoring;

namespace Sleeper.Api.NflData.Analytics;

/// <summary>
/// Scores all NFL players for a season using league scoring settings and produces
/// positional rankings (QB1, WR14, RB38, etc.) and data-driven replacement levels.
/// </summary>
public class LeagueRanker
{
    private readonly FantasyScorer _scorer;

    public LeagueRanker(FantasyScorer scorer)
    {
        _scorer = scorer;
    }

    /// <summary>
    /// Score all players and produce positional rankings.
    /// Returns a dictionary keyed by nflverse player_id (GSIS ID).
    /// </summary>
    public LeagueRankings Rank(List<SeasonPlayerStats> allSeasonStats, int minGames = 4)
    {
        var scored = new List<RankedPlayer>();

        foreach (var stats in allSeasonStats)
        {
            if (stats.Position is null or "DEF") continue;
            var games = stats.Games ?? 0;
            if (games < minGames) continue;

            var result = _scorer.ScoreSeason(stats);
            var ppg = games > 0 ? Math.Round(result.TotalPoints / games, 2) : 0m;

            scored.Add(new RankedPlayer(
                GsisId: stats.PlayerId,
                PlayerName: stats.PlayerDisplayName ?? stats.PlayerName ?? "",
                Position: stats.Position,
                Team: stats.RecentTeam,
                Games: games,
                TotalPoints: result.TotalPoints,
                Ppg: ppg,
                PositionalRank: 0, // filled below
                OverallRank: 0     // filled below
            ));
        }

        // Assign positional ranks
        var byPosition = scored
            .GroupBy(p => p.Position!.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Ppg).ToList());

        foreach (var (pos, players) in byPosition)
        {
            for (int i = 0; i < players.Count; i++)
                players[i] = players[i] with { PositionalRank = i + 1 };

            byPosition[pos] = players;
        }

        // Assign overall rank (all positions by PPG)
        var allRanked = scored.OrderByDescending(p => p.Ppg).ToList();
        for (int i = 0; i < allRanked.Count; i++)
        {
            var idx = allRanked[i];
            // Find the updated record in byPosition and update overall rank
            if (byPosition.TryGetValue(idx.Position!.ToUpperInvariant(), out var posList))
            {
                var posIdx = posList.FindIndex(p => p.GsisId == idx.GsisId);
                if (posIdx >= 0)
                    posList[posIdx] = posList[posIdx] with { OverallRank = i + 1 };
            }
        }

        // Build GSIS lookup
        var byGsisId = new Dictionary<string, RankedPlayer>();
        foreach (var players in byPosition.Values)
            foreach (var p in players)
                byGsisId.TryAdd(p.GsisId, p);

        return new LeagueRankings(byPosition, byGsisId);
    }

    /// <summary>
    /// Score all players and produce rankings keyed by Sleeper player ID.
    /// </summary>
    public LeagueRankings RankBySleeperId(
        List<SeasonPlayerStats> allSeasonStats,
        Dictionary<string, string> sleeperToGsisMap,
        int minGames = 4)
    {
        var rankings = Rank(allSeasonStats, minGames);

        // Build reverse map: GSIS -> Sleeper
        var gsisToSleeper = new Dictionary<string, string>();
        foreach (var (sleeperId, gsisId) in sleeperToGsisMap)
            gsisToSleeper.TryAdd(gsisId, sleeperId);

        var bySleeperId = new Dictionary<string, RankedPlayer>();
        foreach (var (gsisId, player) in rankings.ByGsisId)
        {
            if (gsisToSleeper.TryGetValue(gsisId, out var sleeperId))
                bySleeperId.TryAdd(sleeperId, player);
        }

        return new LeagueRankings(rankings.ByPosition, rankings.ByGsisId, bySleeperId);
    }
}

/// <summary>
/// A player with their positional and overall rank.
/// </summary>
public record RankedPlayer(
    string GsisId,
    string PlayerName,
    string? Position,
    string? Team,
    int Games,
    decimal TotalPoints,
    decimal Ppg,
    int PositionalRank,
    int OverallRank
);

/// <summary>
/// Complete league rankings with positional groupings and lookup dictionaries.
/// </summary>
public class LeagueRankings
{
    public Dictionary<string, List<RankedPlayer>> ByPosition { get; }
    public Dictionary<string, RankedPlayer> ByGsisId { get; }
    public Dictionary<string, RankedPlayer> BySleeperId { get; }

    public LeagueRankings(
        Dictionary<string, List<RankedPlayer>> byPosition,
        Dictionary<string, RankedPlayer> byGsisId,
        Dictionary<string, RankedPlayer>? bySleeperId = null)
    {
        ByPosition = byPosition;
        ByGsisId = byGsisId;
        BySleeperId = bySleeperId ?? new();
    }

    /// <summary>
    /// Get a player's rank string like "QB4", "WR22", "RB38".
    /// </summary>
    public string? GetRankLabel(string sleeperId)
    {
        if (BySleeperId.TryGetValue(sleeperId, out var p))
            return $"{p.Position}{p.PositionalRank}";
        return null;
    }

    /// <summary>
    /// Calculate real replacement-level PPG from actual data for each position.
    /// Replacement = the Nth-ranked player where N = teams × effective starters.
    /// </summary>
    public Dictionary<string, decimal> CalculateReplacementLevels(
        int teams,
        Dictionary<string, int> starterSlots,
        int flexSlots = 0,
        IReadOnlySet<string>? flexEligiblePositions = null)
    {
        var result = new Dictionary<string, decimal>();
        flexEligiblePositions ??= new HashSet<string>(["RB", "WR", "TE"], StringComparer.OrdinalIgnoreCase);

        foreach (var (pos, players) in ByPosition)
        {
            var directSlots = starterSlots.GetValueOrDefault(pos, 0);
            var effectiveSlots = directSlots;

            if (flexEligiblePositions.Contains(pos))
                effectiveSlots += flexSlots;

            var replacementIdx = teams * effectiveSlots;

            result[pos] = replacementIdx < players.Count
                ? players[replacementIdx].Ppg
                : players.Count > 0 ? players[^1].Ppg : 0m;
        }

        return result;
    }
}
