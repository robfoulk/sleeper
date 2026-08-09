using Sleeper.Api.Models;
using Sleeper.Api.NflData.Analytics;

namespace Sleeper.Api.NflData.Services;

/// <summary>
/// High-level orchestration service that composes all analytics into actionable results.
/// </summary>
public interface IAnalysisService
{
    Task<KeeperReport> GetKeeperAnalysisAsync(string leagueId, string username, int historyYears = 3, CancellationToken ct = default);
    Task<PlayerDeepDive?> GetPlayerDeepDiveAsync(string leagueId, string playerName, int historyYears = 3, CancellationToken ct = default);
    Task<LeagueRankings> GetLeagueRankingsAsync(string leagueId, int season, CancellationToken ct = default);
    Task<RosterEvaluation?> EvaluateRosterAsync(string leagueId, string username, CancellationToken ct = default);
    Task<List<Player>> SearchPlayersAsync(string query, string? position = null, CancellationToken ct = default);
}

public record KeeperReport(
    string LeagueName,
    string Season,
    string OwnerName,
    int Teams,
    int MaxKeepers,
    Dictionary<string, decimal> ReplacementLevels,
    int LastCompletedSeason,
    List<PlayerAnalysis> Analyses,
    LeagueRankings Rankings
);

public record PlayerDeepDive(
    Player Player,
    List<SeasonSummary> SeasonHistory,
    Dictionary<int, List<WeeklyFantasyScore>> WeeklyPointsBySeason,
    PlayerAnalysis Analysis,
    LeagueRankings Rankings
);

public record WeeklyFantasyScore(int Week, decimal Points);

public record RosterEvaluation(
    string OwnerName,
    string LeagueName,
    Dictionary<string, PositionalStrength> Positions,
    List<string> Strengths,
    List<string> Weaknesses,
    List<string> Recommendations
);

public record PositionalStrength(
    string Position,
    int PlayerCount,
    int StarterSlots,
    List<RankedPlayerSummary> Players,
    decimal AveragePpg,
    string Rating
);

public record RankedPlayerSummary(
    string Name,
    string? Position,
    int PositionalRank,
    decimal Ppg
);
