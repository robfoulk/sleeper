namespace Sleeper.Api.NflData.Scoring;

public interface IFantasyService
{
    /// <summary>
    /// Score a single player's week using the league's scoring settings.
    /// </summary>
    Task<ScoredPlayer?> GetPlayerWeekScoreAsync(string leagueId, string sleeperId, int season, int week, CancellationToken ct = default);

    /// <summary>
    /// Score a single player's full season using the league's scoring settings.
    /// </summary>
    Task<ScoredPlayer?> GetPlayerSeasonScoreAsync(string leagueId, string sleeperId, int season, CancellationToken ct = default);

    /// <summary>
    /// Score all players on a user's roster for a given week.
    /// </summary>
    Task<List<ScoredPlayer>> GetRosterWeekScoresAsync(string leagueId, string username, int season, int week, CancellationToken ct = default);

    /// <summary>
    /// Score all players on a user's roster for the full season.
    /// </summary>
    Task<List<ScoredPlayer>> GetRosterSeasonScoresAsync(string leagueId, string username, int season, CancellationToken ct = default);
}
