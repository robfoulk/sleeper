using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sleeper.Api.Exceptions;
using Sleeper.Api.Models;

namespace Sleeper.Api;

public class SleeperClient : ISleeperClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SleeperClient(HttpClient http)
    {
        _http = http;
    }

    // User

    public Task<User?> GetUserAsync(string usernameOrId, CancellationToken ct = default)
        => GetAsync<User>($"user/{Uri.EscapeDataString(usernameOrId)}", ct);

    // Leagues

    public Task<List<League>> GetUserLeaguesAsync(string userId, string sport, string season, CancellationToken ct = default)
        => GetListAsync<League>($"user/{Uri.EscapeDataString(userId)}/leagues/{Uri.EscapeDataString(sport)}/{Uri.EscapeDataString(season)}", ct);

    public Task<League?> GetLeagueAsync(string leagueId, CancellationToken ct = default)
        => GetAsync<League>($"league/{Uri.EscapeDataString(leagueId)}", ct);

    public Task<List<Roster>> GetLeagueRostersAsync(string leagueId, CancellationToken ct = default)
        => GetListAsync<Roster>($"league/{Uri.EscapeDataString(leagueId)}/rosters", ct);

    public Task<List<LeagueUser>> GetLeagueUsersAsync(string leagueId, CancellationToken ct = default)
        => GetListAsync<LeagueUser>($"league/{Uri.EscapeDataString(leagueId)}/users", ct);

    public Task<List<Matchup>> GetLeagueMatchupsAsync(string leagueId, int week, CancellationToken ct = default)
        => GetListAsync<Matchup>($"league/{Uri.EscapeDataString(leagueId)}/matchups/{week}", ct);

    public Task<List<PlayoffBracketMatch>> GetWinnersBracketAsync(string leagueId, CancellationToken ct = default)
        => GetListAsync<PlayoffBracketMatch>($"league/{Uri.EscapeDataString(leagueId)}/winners_bracket", ct);

    public Task<List<PlayoffBracketMatch>> GetLosersBracketAsync(string leagueId, CancellationToken ct = default)
        => GetListAsync<PlayoffBracketMatch>($"league/{Uri.EscapeDataString(leagueId)}/losers_bracket", ct);

    public Task<List<Transaction>> GetTransactionsAsync(string leagueId, int week, CancellationToken ct = default)
        => GetListAsync<Transaction>($"league/{Uri.EscapeDataString(leagueId)}/transactions/{week}", ct);

    public Task<List<TradedPick>> GetTradedPicksAsync(string leagueId, CancellationToken ct = default)
        => GetListAsync<TradedPick>($"league/{Uri.EscapeDataString(leagueId)}/traded_picks", ct);

    public Task<NflState?> GetNflStateAsync(CancellationToken ct = default)
        => GetAsync<NflState>("state/nfl", ct);

    // Drafts

    public Task<List<Draft>> GetUserDraftsAsync(string userId, string sport, string season, CancellationToken ct = default)
        => GetListAsync<Draft>($"user/{Uri.EscapeDataString(userId)}/drafts/{Uri.EscapeDataString(sport)}/{Uri.EscapeDataString(season)}", ct);

    public Task<List<Draft>> GetLeagueDraftsAsync(string leagueId, CancellationToken ct = default)
        => GetListAsync<Draft>($"league/{Uri.EscapeDataString(leagueId)}/drafts", ct);

    public Task<Draft?> GetDraftAsync(string draftId, CancellationToken ct = default)
        => GetAsync<Draft>($"draft/{Uri.EscapeDataString(draftId)}", ct);

    public Task<List<DraftPick>> GetDraftPicksAsync(string draftId, CancellationToken ct = default)
        => GetListAsync<DraftPick>($"draft/{Uri.EscapeDataString(draftId)}/picks", ct);

    public Task<List<TradedPick>> GetDraftTradedPicksAsync(string draftId, CancellationToken ct = default)
        => GetListAsync<TradedPick>($"draft/{Uri.EscapeDataString(draftId)}/traded_picks", ct);

    public async Task<List<RosterDraftPickOwner>> GetRosterDraftPicksByDraftAsync(string draftId, CancellationToken ct = default)
    {
        const string query = """
            query roster_draft_picks_by_draft($draftId: String!) {
              roster_draft_picks_by_draft(draft_id: $draftId){
                roster_id
                season
                round
                owner_id
              }
            }
            """;
        using var response = await _http.PostAsJsonAsync(
            "https://sleeper.com/graphql",
            new
            {
                operationName = "roster_draft_picks_by_draft",
                variables = new { draftId },
                query
            },
            JsonOptions,
            ct).ConfigureAwait(false);
        EnsureSuccess(response);
        var payload = await response.Content
            .ReadFromJsonAsync<GraphQlResponse<RosterDraftPicksByDraftData>>(JsonOptions, ct)
            .ConfigureAwait(false);

        if (payload?.Errors is { Count: > 0 })
            throw new InvalidOperationException(
                $"Sleeper GraphQL request failed: {string.Join("; ", payload.Errors.Select(error => error.Message))}");

        return payload?.Data?.RosterDraftPicksByDraft ?? [];
    }

    // Players

    public Task<Dictionary<string, Player>> GetAllPlayersAsync(string sport = "nfl", CancellationToken ct = default)
        => GetDictionaryAsync<Player>($"players/{Uri.EscapeDataString(sport)}", ct);

    public Task<List<TrendingPlayer>> GetTrendingPlayersAsync(string sport = "nfl", string type = "add", int? lookbackHours = null, int? limit = null, CancellationToken ct = default)
    {
        var url = $"players/{Uri.EscapeDataString(sport)}/trending/{Uri.EscapeDataString(type)}";
        var queryParams = new List<string>();
        if (lookbackHours.HasValue) queryParams.Add($"lookback_hours={lookbackHours.Value}");
        if (limit.HasValue) queryParams.Add($"limit={limit.Value}");
        if (queryParams.Count > 0) url += "?" + string.Join("&", queryParams);
        return GetListAsync<TrendingPlayer>(url, ct);
    }

    // Helpers

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        EnsureSuccess(response);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
    }

    private async Task<List<T>> GetListAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        EnsureSuccess(response);

        return await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, ct).ConfigureAwait(false) ?? [];
    }

    private async Task<Dictionary<string, T>> GetDictionaryAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new Dictionary<string, T>();

        EnsureSuccess(response);

        return await response.Content.ReadFromJsonAsync<Dictionary<string, T>>(JsonOptions, ct).ConfigureAwait(false)
               ?? new Dictionary<string, T>();
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new SleeperRateLimitException();

        throw new SleeperApiException(
            response.StatusCode,
            $"Sleeper API returned {(int)response.StatusCode} {response.ReasonPhrase}");
    }
}
