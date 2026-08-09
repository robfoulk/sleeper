using System.Net;
using System.Text.Json;
using FluentAssertions;
using Sleeper.Api.Exceptions;
using Sleeper.Api.Models;

namespace Sleeper.Api.Tests;

public class SleeperClientTests
{
    private static (SleeperClient client, MockHttpMessageHandler handler) CreateClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.sleeper.app/v1/") };
        return (new SleeperClient(httpClient), handler);
    }

    // -- User --

    [Fact]
    public async Task GetUserAsync_ReturnsUser_WhenFound()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("""{"user_id":"12345","username":"testuser","display_name":"Test","avatar":"abc"}""");

        var user = await client.GetUserAsync("testuser");

        user.Should().NotBeNull();
        user!.UserId.Should().Be("12345");
        user.Username.Should().Be("testuser");
        user.DisplayName.Should().Be("Test");
        handler.LastRequestUri!.PathAndQuery.Should().Be("/v1/user/testuser");
    }

    [Fact]
    public async Task GetUserAsync_ReturnsNull_When404()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("", HttpStatusCode.NotFound);

        var user = await client.GetUserAsync("nobody");

        user.Should().BeNull();
    }

    [Fact]
    public async Task GetUserAsync_ThrowsRateLimitException_When429()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("", HttpStatusCode.TooManyRequests);

        var act = () => client.GetUserAsync("testuser");

        await act.Should().ThrowAsync<SleeperRateLimitException>();
    }

    [Fact]
    public async Task GetUserAsync_ThrowsApiException_WhenServerError()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("", HttpStatusCode.InternalServerError);

        var act = () => client.GetUserAsync("testuser");

        await act.Should().ThrowAsync<SleeperApiException>()
            .Where(e => e.StatusCode == HttpStatusCode.InternalServerError);
    }

    // -- Leagues --

    [Fact]
    public async Task GetLeagueAsync_DeserializesLeague()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("""
        {
            "league_id": "999",
            "name": "Test League",
            "status": "in_season",
            "sport": "nfl",
            "season": "2025",
            "total_rosters": 8,
            "draft_id": "d1",
            "previous_league_id": "888",
            "roster_positions": ["QB","RB","WR","TE","FLEX","DEF","BN"],
            "scoring_settings": {"pass_td": 4.0, "rush_td": 6.0}
        }
        """);

        var league = await client.GetLeagueAsync("999");

        league.Should().NotBeNull();
        league!.LeagueId.Should().Be("999");
        league.Name.Should().Be("Test League");
        league.TotalRosters.Should().Be(8);
        league.PreviousLeagueId.Should().Be("888");
        league.ScoringSettings.Should().ContainKey("pass_td");
        league.RosterPositions.Should().Contain("DEF");
    }

    [Fact]
    public async Task GetUserLeaguesAsync_ReturnsEmptyList_When404()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("", HttpStatusCode.NotFound);

        var leagues = await client.GetUserLeaguesAsync("123", "nfl", "2025");

        leagues.Should().BeEmpty();
    }

    // -- Rosters with team defenses --

    [Fact]
    public async Task GetLeagueRostersAsync_ParsesRostersWithTeamDefenses()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("""
        [
            {
                "roster_id": 1,
                "owner_id": "user1",
                "league_id": "lg1",
                "players": ["7523", "6904", "SEA", "HOU"],
                "starters": ["7523", "SEA"],
                "reserve": [],
                "settings": {"wins":5,"losses":3,"ties":0,"fpts":1200,"fpts_decimal":50,
                             "fpts_against":1100,"fpts_against_decimal":25,
                             "total_moves":3,"waiver_position":4,"waiver_budget_used":10}
            }
        ]
        """);

        var rosters = await client.GetLeagueRostersAsync("lg1");

        rosters.Should().HaveCount(1);
        var roster = rosters[0];
        roster.Players.Should().Contain("SEA");
        roster.Players.Should().Contain("HOU");
        roster.Players.Should().Contain("7523");
        roster.Starters.Should().Contain("SEA");
    }

    // -- Matchups --

    [Fact]
    public async Task GetLeagueMatchupsAsync_ConstructsCorrectUrl()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("[]");

        await client.GetLeagueMatchupsAsync("lg1", 5);

        handler.LastRequestUri!.PathAndQuery.Should().Be("/v1/league/lg1/matchups/5");
    }

    // -- NFL State --

    [Fact]
    public async Task GetNflStateAsync_DeserializesState()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("""
        {
            "week": 8,
            "season": "2025",
            "season_type": "regular",
            "season_start_date": "2025-09-04",
            "previous_season": "2024",
            "leg": 8,
            "league_season": "2025",
            "league_create_season": "2026",
            "display_week": 9
        }
        """);

        var state = await client.GetNflStateAsync();

        state.Should().NotBeNull();
        state!.Week.Should().Be(8);
        state.Season.Should().Be("2025");
        state.DisplayWeek.Should().Be(9);
    }

    // -- Draft Picks --

    [Fact]
    public async Task GetDraftPicksAsync_DeserializesPicks()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("""
        [
            {
                "player_id": "2391",
                "picked_by": "user1",
                "roster_id": 1,
                "round": 1,
                "draft_slot": 3,
                "pick_no": 3,
                "is_keeper": null,
                "draft_id": "d1",
                "metadata": {
                    "player_id": "2391",
                    "first_name": "David",
                    "last_name": "Johnson",
                    "position": "RB",
                    "team": "ARI",
                    "status": "Active",
                    "sport": "nfl",
                    "number": "31",
                    "injury_status": ""
                }
            }
        ]
        """);

        var picks = await client.GetDraftPicksAsync("d1");

        picks.Should().HaveCount(1);
        picks[0].Metadata!.FirstName.Should().Be("David");
        picks[0].Metadata!.LastName.Should().Be("Johnson");
        picks[0].Round.Should().Be(1);
    }

    [Fact]
    public async Task GetRosterDraftPicksByDraftAsync_UsesVariablesAndDeserializesResponse()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("""
            {
              "data": {
                "roster_draft_picks_by_draft": [
                  { "season": "2026", "round": 2, "roster_id": 3, "owner_id": 4 }
                ]
              }
            }
            """);

        var picks = await client.GetRosterDraftPicksByDraftAsync("""draft"with-quote""");

        picks.Should().ContainSingle();
        picks[0].OwnerId.Should().Be(4);
        handler.LastRequestContent.Should().Contain("\"variables\":{\"draftId\":\"draft\\u0022with-quote\"}");
        handler.LastRequestContent.Should().Contain("$draftId");
        handler.LastRequestContent.Should().NotContain("""draft_id: "draft""");
    }

    [Fact]
    public async Task GetRosterDraftPicksByDraftAsync_ThrowsWhenGraphQlReturnsErrors()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("""{"data":null,"errors":[{"message":"draft not found"}]}""");

        Func<Task> act = async () => await client.GetRosterDraftPicksByDraftAsync("missing");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*draft not found*");
    }

    // -- Trending Players --

    [Fact]
    public async Task GetTrendingPlayersAsync_ConstructsUrlWithQueryParams()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("""[{"player_id":"111","count":45}]""");

        var trending = await client.GetTrendingPlayersAsync("nfl", "add", lookbackHours: 48, limit: 10);

        trending.Should().HaveCount(1);
        trending[0].PlayerId.Should().Be("111");
        handler.LastRequestUri!.PathAndQuery.Should().Contain("lookback_hours=48");
        handler.LastRequestUri.PathAndQuery.Should().Contain("limit=10");
    }

    [Fact]
    public async Task GetTrendingPlayersAsync_OmitsNullQueryParams()
    {
        var (client, handler) = CreateClient();
        handler.SetResponse("[]");

        await client.GetTrendingPlayersAsync();

        handler.LastRequestUri!.PathAndQuery.Should().NotContain("?");
    }
}

// Simple mock handler for unit tests
public class MockHttpMessageHandler : HttpMessageHandler
{
    private string _responseContent = "";
    private HttpStatusCode _statusCode = HttpStatusCode.OK;
    public Uri? LastRequestUri { get; private set; }
    public string? LastRequestContent { get; private set; }

    public void SetResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseContent = content;
        _statusCode = statusCode;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastRequestContent = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
