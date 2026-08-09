namespace Sleeper.Api.NflData.Analytics;

/// <summary>
/// Calculates player durability as a percentage of possible games played.
/// Injuries are the #1 risk in keeper leagues — a player who misses half the season
/// destroys your keeper value regardless of their talent.
/// </summary>
public static class DurabilityCalculator
{
    /// <summary>
    /// Get the number of regular season games for a given NFL season.
    /// 17 games from 2021 onwards, 16 games before that.
    /// </summary>
    public static int GamesInSeason(int season) => season >= 2021 ? 17 : 16;

    /// <summary>
    /// Calculate durability percentage (0-100) over multiple seasons.
    /// </summary>
    public static decimal Calculate(List<SeasonSummary> seasons, int maxSeasons = 3)
    {
        var recent = seasons
            .OrderByDescending(s => s.Season)
            .Take(maxSeasons)
            .ToList();

        if (recent.Count == 0) return 0m;

        var totalGames = recent.Sum(s => s.GamesPlayed);
        var maxGames = recent.Sum(s => GamesInSeason(s.Season));

        if (maxGames == 0) return 0m;

        return Math.Round((decimal)totalGames / maxGames * 100m, 1);
    }

    /// <summary>
    /// Get a durability multiplier (0.0-1.0) for projections.
    /// Players under 70% availability get discounted.
    /// </summary>
    public static decimal GetMultiplier(decimal durabilityPct)
    {
        // 85%+ = no discount (full value)
        if (durabilityPct >= 85m) return 1.0m;

        // 70-85% = mild discount
        if (durabilityPct >= 70m) return 0.85m + (durabilityPct - 70m) / 100m;

        // 50-70% = moderate discount
        if (durabilityPct >= 50m) return 0.65m + (durabilityPct - 50m) / 100m;

        // Under 50% = severe discount
        return Math.Max(0.40m, durabilityPct / 100m);
    }
}
