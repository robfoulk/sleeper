using FluentAssertions;
using Sleeper.Api.Services;

namespace Sleeper.Api.IntegrationTests;

[Trait("Category", "Integration")]
public class SleeperClientIntegrationTests : IDisposable
{
    private readonly HttpClient _http;
    private readonly SleeperClient _client;

    private const string Username = "robfoulk";
    private const string LeagueId = "1312539280601522176";
    private const string PreviousLeagueId = "1180276953741729792";

    public SleeperClientIntegrationTests()
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.sleeper.app/v1/") };
        _client = new SleeperClient(_http);
    }

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task GetUserAsync_ReturnsValidUser()
    {
        var user = await _client.GetUserAsync(Username);

        user.Should().NotBeNull();
        user!.Username.Should().Be(Username);
        user.UserId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetUserAsync_ReturnsNull_ForBogusUser()
    {
        var user = await _client.GetUserAsync("this_user_definitely_does_not_exist_zzz999");

        user.Should().BeNull();
    }

    [Fact]
    public async Task GetLeagueAsync_ReturnsLeague()
    {
        var league = await _client.GetLeagueAsync(LeagueId);

        league.Should().NotBeNull();
        league!.LeagueId.Should().Be(LeagueId);
        league.Sport.Should().Be("nfl");
        league.TotalRosters.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetLeagueRostersAsync_ReturnsRosters_WithTeamDefenses()
    {
        var rosters = await _client.GetLeagueRostersAsync(LeagueId);

        rosters.Should().NotBeEmpty();

        // At least some rosters should have team defense abbreviations in their players
        var hasDefense = rosters.Any(r =>
            r.Players is not null &&
            r.Players.Any(SleeperService.IsTeamDefense));

        hasDefense.Should().BeTrue("rosters should contain team defense abbreviations like SEA, DET, PHI");
    }

    [Fact]
    public async Task GetLeagueUsersAsync_ReturnsUsers()
    {
        var users = await _client.GetLeagueUsersAsync(LeagueId);

        users.Should().NotBeEmpty();
        users.Should().Contain(u => u.DisplayName == Username || u.Username == Username);
    }

    [Fact]
    public async Task GetNflStateAsync_ReturnsState()
    {
        var state = await _client.GetNflStateAsync();

        state.Should().NotBeNull();
        state!.Season.Should().NotBeNullOrEmpty();
        state.Week.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetLeagueDraftsAsync_ReturnsDrafts()
    {
        // Use previous year league which should have a completed draft
        var drafts = await _client.GetLeagueDraftsAsync(PreviousLeagueId);

        drafts.Should().NotBeEmpty();
        var draft = drafts[0];
        draft.DraftId.Should().NotBeNullOrEmpty();
        draft.Status.Should().Be("complete");
    }

    [Fact]
    public async Task GetDraftPicksAsync_ReturnsPicksWithMetadata()
    {
        var drafts = await _client.GetLeagueDraftsAsync(PreviousLeagueId);
        drafts.Should().NotBeEmpty();

        var picks = await _client.GetDraftPicksAsync(drafts[0].DraftId);

        picks.Should().NotBeEmpty();
        var firstPick = picks[0];
        firstPick.PlayerId.Should().NotBeNullOrEmpty();
        firstPick.Round.Should().BeGreaterThan(0);
        firstPick.Metadata.Should().NotBeNull();
        firstPick.Metadata!.FirstName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetUserLeaguesAsync_ReturnsLeagues()
    {
        var user = await _client.GetUserAsync(Username);
        user.Should().NotBeNull();

        var leagues = await _client.GetUserLeaguesAsync(user!.UserId, "nfl", "2025");

        leagues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetTrendingPlayersAsync_ReturnsTrending()
    {
        var trending = await _client.GetTrendingPlayersAsync("nfl", "add", limit: 5);

        trending.Should().NotBeEmpty();
        trending.Count.Should().BeLessThanOrEqualTo(5);
        trending[0].PlayerId.Should().NotBeNullOrEmpty();
        trending[0].Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetAllPlayersAsync_ContainsBothPlayersAndTeamDefenses()
    {
        // WARNING: This is a ~5MB response. Only run in integration tests.
        var players = await _client.GetAllPlayersAsync();

        players.Should().NotBeEmpty();
        players.Count.Should().BeGreaterThan(1000);

        // Verify a normal player exists
        players.Values.Should().Contain(p => p.Position == "QB");

        // Check if team defenses are in the players endpoint
        // They use abbreviations like "SEA", "DET" as the key
        var defenseKeys = players.Keys.Where(SleeperService.IsTeamDefense).ToList();
        defenseKeys.Should().NotBeEmpty("team defenses should be present in /players/nfl");

        // Verify a defense entry
        if (players.TryGetValue("SEA", out var sea))
        {
            sea.Team.Should().Be("SEA");
            sea.Position.Should().Be("DEF");
        }
    }
}
