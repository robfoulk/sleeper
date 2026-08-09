using Sleeper.Api.NflData.Models;

namespace Sleeper.Api.NflData;

public interface INflDataClient
{
    /// <summary>
    /// Get weekly player stats for a season. Includes per-game stats and fantasy points.
    /// </summary>
    Task<List<WeeklyPlayerStats>> GetWeeklyStatsAsync(int season, CancellationToken ct = default);

    /// <summary>
    /// Get season-aggregated player stats. Regular season only by default.
    /// </summary>
    Task<List<SeasonPlayerStats>> GetSeasonStatsAsync(int season, string seasonType = "reg", CancellationToken ct = default);

    /// <summary>
    /// Get player ID cross-reference mappings (Sleeper, ESPN, Yahoo, GSIS, etc.)
    /// </summary>
    Task<List<PlayerIdMapping>> GetPlayerIdMappingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Build a lookup from Sleeper player ID to nflverse GSIS ID.
    /// </summary>
    Task<Dictionary<string, string>> GetSleeperToGsisMapAsync(CancellationToken ct = default);

    /// <summary>
    /// Get season stats keyed by Sleeper player ID for easy joining with Sleeper rosters.
    /// </summary>
    Task<Dictionary<string, SeasonPlayerStats>> GetSeasonStatsBySleeperIdAsync(int season, string seasonType = "reg", CancellationToken ct = default);

    /// <summary>
    /// Get weekly stats keyed by Sleeper player ID for easy joining with Sleeper rosters.
    /// </summary>
    Task<Dictionary<string, List<WeeklyPlayerStats>>> GetWeeklyStatsBySleeperIdAsync(int season, CancellationToken ct = default);
}
