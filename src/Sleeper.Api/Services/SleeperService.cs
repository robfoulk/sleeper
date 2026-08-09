using Sleeper.Api.Models;

namespace Sleeper.Api.Services;

public class SleeperService : ISleeperService
{
    private readonly ISleeperClient _client;

    public SleeperService(ISleeperClient client)
    {
        _client = client;
    }

    public async Task<Roster?> GetMyRosterAsync(string leagueId, string username, CancellationToken ct = default)
    {
        var user = await _client.GetUserAsync(username, ct).ConfigureAwait(false);
        if (user is null) return null;

        var rosters = await _client.GetLeagueRostersAsync(leagueId, ct).ConfigureAwait(false);
        return rosters.FirstOrDefault(r => r.OwnerId == user.UserId);
    }

    public async Task<List<MatchupWithNames>> GetWeekScoreboardAsync(string leagueId, int week, CancellationToken ct = default)
    {
        var matchupsTask = _client.GetLeagueMatchupsAsync(leagueId, week, ct);
        var rostersTask = _client.GetLeagueRostersAsync(leagueId, ct);
        var usersTask = _client.GetLeagueUsersAsync(leagueId, ct);

        await Task.WhenAll(matchupsTask, rostersTask, usersTask).ConfigureAwait(false);

        var matchups = await matchupsTask.ConfigureAwait(false);
        var rosters = await rostersTask.ConfigureAwait(false);
        var users = await usersTask.ConfigureAwait(false);

        var ownerMap = BuildOwnerMap(rosters, users);

        var grouped = matchups
            .Where(m => m.MatchupId.HasValue)
            .GroupBy(m => m.MatchupId!.Value);

        var results = new List<MatchupWithNames>();
        foreach (var group in grouped)
        {
            var teams = group.ToList();
            var t1 = teams.ElementAtOrDefault(0);
            var t2 = teams.ElementAtOrDefault(1);

            if (t1 is null) continue;

            var (t1Display, t1Team) = GetOwnerInfo(ownerMap, t1.RosterId);
            var (t2Display, t2Team) = t2 is not null ? GetOwnerInfo(ownerMap, t2.RosterId) : (null, null);

            results.Add(new MatchupWithNames(
                MatchupId: group.Key,
                Team1DisplayName: t1Display,
                Team1TeamName: t1Team,
                Team1RosterId: t1.RosterId,
                Team1Points: t1.Points,
                Team2DisplayName: t2Display,
                Team2TeamName: t2Team,
                Team2RosterId: t2?.RosterId,
                Team2Points: t2?.Points
            ));
        }

        return results;
    }

    public async Task<DraftPick?> FindPlayerDraftPickAsync(string leagueId, string playerName, CancellationToken ct = default)
    {
        var drafts = await _client.GetLeagueDraftsAsync(leagueId, ct).ConfigureAwait(false);
        var draft = drafts.FirstOrDefault();
        if (draft is null) return null;

        var picks = await _client.GetDraftPicksAsync(draft.DraftId, ct).ConfigureAwait(false);

        var searchName = playerName.ToLowerInvariant();
        return picks.FirstOrDefault(p =>
            p.Metadata is not null &&
            $"{p.Metadata.FirstName} {p.Metadata.LastName}".ToLowerInvariant().Contains(searchName));
    }

    public async Task<List<RosterWithOwner>> GetRostersWithOwnersAsync(string leagueId, CancellationToken ct = default)
    {
        var rostersTask = _client.GetLeagueRostersAsync(leagueId, ct);
        var usersTask = _client.GetLeagueUsersAsync(leagueId, ct);

        await Task.WhenAll(rostersTask, usersTask).ConfigureAwait(false);

        var rosters = await rostersTask.ConfigureAwait(false);
        var users = await usersTask.ConfigureAwait(false);
        var userMap = users.ToDictionary(u => u.UserId, u => u);

        return rosters.Select(r =>
        {
            LeagueUser? owner = r.OwnerId is not null && userMap.TryGetValue(r.OwnerId, out var u) ? u : null;
            return new RosterWithOwner(
                Roster: r,
                OwnerDisplayName: owner?.DisplayName,
                OwnerUsername: owner?.Username,
                TeamName: owner?.Metadata?.GetValueOrDefault("team_name")
            );
        }).ToList();
    }

    public async Task<List<PlayerInfo>> GetRosterPlayersAsync(string leagueId, string username, CancellationToken ct = default)
    {
        var roster = await GetMyRosterAsync(leagueId, username, ct).ConfigureAwait(false);
        if (roster is null) return [];

        var allPlayers = await _client.GetAllPlayersAsync("nfl", ct).ConfigureAwait(false);

        var starters = new HashSet<string>(roster.Starters ?? []);
        var reserves = new HashSet<string>(roster.Reserve ?? []);

        var result = new List<PlayerInfo>();
        foreach (var playerId in roster.Players ?? [])
        {
            if (allPlayers.TryGetValue(playerId, out var player))
            {
                result.Add(new PlayerInfo(
                    Player: player,
                    IsStarter: starters.Contains(playerId),
                    IsReserve: reserves.Contains(playerId)
                ));
            }
            else if (IsTeamDefense(playerId))
            {
                // Team defenses use abbreviations (e.g. "SEA", "DET") as player IDs.
                // They may not appear in the /players endpoint, so synthesize a record.
                var defensePlayer = new Player(
                    PlayerId: playerId,
                    FirstName: playerId,
                    LastName: "DEF",
                    Position: "DEF",
                    Team: playerId,
                    Age: null, Status: "Active", Number: null,
                    College: null, YearsExp: null,
                    FantasyPositions: ["DEF"],
                    InjuryStatus: null, Weight: null, Height: null,
                    SearchFullName: $"{playerId.ToLowerInvariant()}def",
                    SearchFirstName: playerId.ToLowerInvariant(),
                    SearchLastName: "def",
                    SearchRank: null, DepthChartPosition: null, DepthChartOrder: null,
                    Sport: "nfl", Hashtag: null, FantasyDataId: null,
                    BirthCountry: null, EspnId: null, YahooId: null,
                    RotowireId: null, RotoworldId: null, SportradarId: null,
                    PracticeParticipation: null, InjuryStartDate: null
                );
                result.Add(new PlayerInfo(
                    Player: defensePlayer,
                    IsStarter: starters.Contains(playerId),
                    IsReserve: reserves.Contains(playerId)
                ));
            }
        }

        return result;
    }

    public async Task<string?> GetLeagueIdForSeasonAsync(string currentLeagueId, string targetSeason, CancellationToken ct = default)
    {
        var leagueId = currentLeagueId;
        var visited = new HashSet<string>();

        while (leagueId is not null && visited.Add(leagueId))
        {
            var league = await _client.GetLeagueAsync(leagueId, ct).ConfigureAwait(false);
            if (league is null) return null;

            if (string.Equals(league.Season, targetSeason, StringComparison.OrdinalIgnoreCase))
                return league.LeagueId;

            leagueId = league.PreviousLeagueId;
        }

        return null;
    }

    public async Task<List<KeeperValue>> GetRosterKeeperValuesAsync(string leagueId, string username, int undraftedCost = 10, CancellationToken ct = default)
    {
        // Get the league to determine draft source
        var league = await _client.GetLeagueAsync(leagueId, ct).ConfigureAwait(false);
        if (league is null) return [];

        // Find the most recent completed draft: current league first, then previous season
        var drafts = await _client.GetLeagueDraftsAsync(leagueId, ct).ConfigureAwait(false);
        var completedDraft = drafts.FirstOrDefault(d => d.Status == "complete");

        if (completedDraft is null && league.PreviousLeagueId is not null)
        {
            drafts = await _client.GetLeagueDraftsAsync(league.PreviousLeagueId, ct).ConfigureAwait(false);
            completedDraft = drafts.FirstOrDefault(d => d.Status == "complete");
        }

        // Build player_id -> draft pick lookup
        var draftPicksByPlayerId = new Dictionary<string, DraftPick>();
        if (completedDraft is not null)
        {
            var picks = await _client.GetDraftPicksAsync(completedDraft.DraftId, ct).ConfigureAwait(false);
            foreach (var pick in picks)
            {
                if (pick.PlayerId is not null)
                    draftPicksByPlayerId.TryAdd(pick.PlayerId, pick);
            }
        }

        // Get roster with resolved player objects
        var rosterPlayers = await GetRosterPlayersAsync(leagueId, username, ct).ConfigureAwait(false);

        return rosterPlayers.Select(pi =>
        {
            var draftPick = draftPicksByPlayerId.GetValueOrDefault(pi.Player.PlayerId);
            var (costRound, canBeKept) = KeeperValue.Calculate(draftPick?.Round, undraftedCost);

            return new KeeperValue(
                Player: pi.Player,
                IsStarter: pi.IsStarter,
                IsReserve: pi.IsReserve,
                DraftRound: draftPick?.Round,
                DraftPickNumber: draftPick?.PickNo,
                WasKeptLastYear: draftPick?.IsKeeper == true,
                KeeperCostRound: costRound,
                CanBeKept: canBeKept
            );
        }).ToList();
    }

    public async Task<List<DeclaredKeeperTeam>> GetDeclaredKeepersAsync(string leagueId, CancellationToken ct = default)
    {
        var rostersTask = _client.GetLeagueRostersAsync(leagueId, ct);
        var usersTask = _client.GetLeagueUsersAsync(leagueId, ct);

        await Task.WhenAll(rostersTask, usersTask).ConfigureAwait(false);

        var rosters = await rostersTask.ConfigureAwait(false);
        var users = await usersTask.ConfigureAwait(false);
        var userMap = users.ToDictionary(u => u.UserId, u => u);

        // Only fetch the player catalogue when at least one team has declared keepers.
        // For most of the offseason every team's "keepers" is null/empty, so this avoids
        // a multi-MB download when there's nothing to resolve.
        var anyKeepers = rosters.Any(r => r.Keepers is { Count: > 0 });
        Dictionary<string, Player>? allPlayers = anyKeepers
            ? await _client.GetAllPlayersAsync("nfl", ct).ConfigureAwait(false)
            : null;

        return rosters.Select(r => BuildDeclaredKeeperTeam(r, userMap, allPlayers)).ToList();
    }

    public async Task<DeclaredKeeperTeam?> GetDeclaredKeepersForUserAsync(string leagueId, string username, CancellationToken ct = default)
    {
        var user = await _client.GetUserAsync(username, ct).ConfigureAwait(false);
        if (user is null) return null;

        var rosters = await _client.GetLeagueRostersAsync(leagueId, ct).ConfigureAwait(false);
        var roster = rosters.FirstOrDefault(r => r.OwnerId == user.UserId);
        if (roster is null) return null;

        var users = await _client.GetLeagueUsersAsync(leagueId, ct).ConfigureAwait(false);
        var userMap = users.ToDictionary(u => u.UserId, u => u);

        Dictionary<string, Player>? allPlayers = roster.Keepers is { Count: > 0 }
            ? await _client.GetAllPlayersAsync("nfl", ct).ConfigureAwait(false)
            : null;

        return BuildDeclaredKeeperTeam(roster, userMap, allPlayers);
    }

    private static DeclaredKeeperTeam BuildDeclaredKeeperTeam(
        Roster roster,
        Dictionary<string, LeagueUser> userMap,
        Dictionary<string, Player>? allPlayers)
    {
        LeagueUser? owner = roster.OwnerId is not null && userMap.TryGetValue(roster.OwnerId, out var u) ? u : null;

        var keepers = new List<Player>();
        if (allPlayers is not null && roster.Keepers is { Count: > 0 })
        {
            foreach (var playerId in roster.Keepers)
            {
                if (string.IsNullOrWhiteSpace(playerId)) continue;
                if (allPlayers.TryGetValue(playerId, out var p))
                {
                    keepers.Add(p);
                }
                else if (IsTeamDefense(playerId))
                {
                    keepers.Add(SynthesizeDefense(playerId));
                }
            }
        }

        return new DeclaredKeeperTeam(
            RosterId: roster.RosterId,
            OwnerId: roster.OwnerId,
            Username: owner?.Username,
            DisplayName: owner?.DisplayName ?? owner?.Username,
            TeamName: owner?.Metadata?.GetValueOrDefault("team_name"),
            Keepers: keepers
        );
    }

    private static Player SynthesizeDefense(string teamAbbr) =>
        new(
            PlayerId: teamAbbr,
            FirstName: teamAbbr,
            LastName: "DEF",
            Position: "DEF",
            Team: teamAbbr,
            Age: null, Status: "Active", Number: null,
            College: null, YearsExp: null,
            FantasyPositions: ["DEF"],
            InjuryStatus: null, Weight: null, Height: null,
            SearchFullName: $"{teamAbbr.ToLowerInvariant()}def",
            SearchFirstName: teamAbbr.ToLowerInvariant(),
            SearchLastName: "def",
            SearchRank: null, DepthChartPosition: null, DepthChartOrder: null,
            Sport: "nfl", Hashtag: null, FantasyDataId: null,
            BirthCountry: null, EspnId: null, YahooId: null,
            RotowireId: null, RotoworldId: null, SportradarId: null,
            PracticeParticipation: null, InjuryStartDate: null
        );

    // Helpers

    private static readonly HashSet<string> NflTeamAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "ARI", "ATL", "BAL", "BUF", "CAR", "CHI", "CIN", "CLE",
        "DAL", "DEN", "DET", "GB", "HOU", "IND", "JAX", "KC",
        "LAC", "LAR", "LV", "MIA", "MIN", "NE", "NO", "NYG",
        "NYJ", "PHI", "PIT", "SEA", "SF", "TB", "TEN", "WAS"
    };

    public static bool IsTeamDefense(string playerId)
        => NflTeamAbbreviations.Contains(playerId);

    private static Dictionary<int, (string? DisplayName, string? TeamName)> BuildOwnerMap(
        List<Roster> rosters, List<LeagueUser> users)
    {
        var userMap = users.ToDictionary(u => u.UserId, u => u);
        var map = new Dictionary<int, (string?, string?)>();

        foreach (var roster in rosters)
        {
            if (roster.OwnerId is not null && userMap.TryGetValue(roster.OwnerId, out var user))
            {
                map[roster.RosterId] = (
                    user.DisplayName ?? user.Username,
                    user.Metadata?.GetValueOrDefault("team_name")
                );
            }
            else
            {
                map[roster.RosterId] = (null, null);
            }
        }

        return map;
    }

    private static (string? DisplayName, string? TeamName) GetOwnerInfo(
        Dictionary<int, (string? DisplayName, string? TeamName)> ownerMap, int rosterId)
    {
        return ownerMap.TryGetValue(rosterId, out var info) ? info : (null, null);
    }
}
