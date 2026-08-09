using Sleeper.Api.Models;

namespace Sleeper.Api.Services;

public interface ISleeperService
{
    Task<Roster?> GetMyRosterAsync(string leagueId, string username, CancellationToken ct = default);
    Task<List<MatchupWithNames>> GetWeekScoreboardAsync(string leagueId, int week, CancellationToken ct = default);
    Task<DraftPick?> FindPlayerDraftPickAsync(string leagueId, string playerName, CancellationToken ct = default);
    Task<List<RosterWithOwner>> GetRostersWithOwnersAsync(string leagueId, CancellationToken ct = default);
    Task<List<PlayerInfo>> GetRosterPlayersAsync(string leagueId, string username, CancellationToken ct = default);
    Task<string?> GetLeagueIdForSeasonAsync(string currentLeagueId, string targetSeason, CancellationToken ct = default);
    Task<List<KeeperValue>> GetRosterKeeperValuesAsync(string leagueId, string username, int undraftedCost = 10, CancellationToken ct = default);

    /// <summary>
    /// Returns the declared keepers (player selections) for every team in the league.
    /// The <c>Keepers</c> list on each entry is empty for owners who have not declared keepers,
    /// which is normal for most of the offseason until the keeper deadline.
    /// </summary>
    Task<List<DeclaredKeeperTeam>> GetDeclaredKeepersAsync(string leagueId, CancellationToken ct = default);

    /// <summary>
    /// Returns the declared keepers for a single team identified by username.
    /// Returns <c>null</c> when the user has no roster in the league.
    /// </summary>
    Task<DeclaredKeeperTeam?> GetDeclaredKeepersForUserAsync(string leagueId, string username, CancellationToken ct = default);
}
