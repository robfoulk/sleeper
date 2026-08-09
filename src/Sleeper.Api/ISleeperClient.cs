using Sleeper.Api.Models;

namespace Sleeper.Api;

public interface ISleeperClient
{
    // User
    Task<User?> GetUserAsync(string usernameOrId, CancellationToken ct = default);

    // Leagues
    Task<List<League>> GetUserLeaguesAsync(string userId, string sport, string season, CancellationToken ct = default);
    Task<League?> GetLeagueAsync(string leagueId, CancellationToken ct = default);
    Task<List<Roster>> GetLeagueRostersAsync(string leagueId, CancellationToken ct = default);
    Task<List<LeagueUser>> GetLeagueUsersAsync(string leagueId, CancellationToken ct = default);
    Task<List<Matchup>> GetLeagueMatchupsAsync(string leagueId, int week, CancellationToken ct = default);
    Task<List<PlayoffBracketMatch>> GetWinnersBracketAsync(string leagueId, CancellationToken ct = default);
    Task<List<PlayoffBracketMatch>> GetLosersBracketAsync(string leagueId, CancellationToken ct = default);
    Task<List<Transaction>> GetTransactionsAsync(string leagueId, int week, CancellationToken ct = default);
    Task<List<TradedPick>> GetTradedPicksAsync(string leagueId, CancellationToken ct = default);
    Task<NflState?> GetNflStateAsync(CancellationToken ct = default);

    // Drafts
    Task<List<Draft>> GetUserDraftsAsync(string userId, string sport, string season, CancellationToken ct = default);
    Task<List<Draft>> GetLeagueDraftsAsync(string leagueId, CancellationToken ct = default);
    Task<Draft?> GetDraftAsync(string draftId, CancellationToken ct = default);
    Task<List<DraftPick>> GetDraftPicksAsync(string draftId, CancellationToken ct = default);
    Task<List<TradedPick>> GetDraftTradedPicksAsync(string draftId, CancellationToken ct = default);
    Task<List<RosterDraftPickOwner>> GetRosterDraftPicksByDraftAsync(string draftId, CancellationToken ct = default);

    // Players
    Task<Dictionary<string, Player>> GetAllPlayersAsync(string sport = "nfl", CancellationToken ct = default);
    Task<List<TrendingPlayer>> GetTrendingPlayersAsync(string sport = "nfl", string type = "add", int? lookbackHours = null, int? limit = null, CancellationToken ct = default);
}
