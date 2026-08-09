using FluentAssertions;
using Sleeper.Api.Services;

namespace Sleeper.Api.IntegrationTests;

[Trait("Category", "Integration")]
public class SleeperServiceIntegrationTests : IDisposable
{
    private readonly HttpClient _http;
    private readonly SleeperClient _client;
    private readonly SleeperService _service;

    private const string Username = "robfoulk";
    private const string LeagueId = "1312539280601522176";
    private const string PreviousLeagueId = "1180276953741729792";

    public SleeperServiceIntegrationTests()
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.sleeper.app/v1/") };
        _client = new SleeperClient(_http);
        _service = new SleeperService(_client);
    }

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task GetMyRosterAsync_FindsRobsRoster()
    {
        var roster = await _service.GetMyRosterAsync(LeagueId, Username);

        roster.Should().NotBeNull();
        roster!.Players.Should().NotBeEmpty();
        roster.OwnerId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetRostersWithOwnersAsync_ReturnsEnrichedRosters()
    {
        var rosters = await _service.GetRostersWithOwnersAsync(LeagueId);

        rosters.Should().NotBeEmpty();
        rosters.Should().Contain(r => r.OwnerUsername == Username || r.OwnerDisplayName == Username);
    }

    [Fact]
    public async Task GetLeagueIdForSeasonAsync_FindsPreviousYear()
    {
        var result = await _service.GetLeagueIdForSeasonAsync(LeagueId, "2024");

        // The previous year league should be found by walking the chain
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRosterKeeperValuesAsync_ReturnsKeeperValuesForRob()
    {
        var results = await _service.GetRosterKeeperValuesAsync(LeagueId, Username);

        results.Should().NotBeEmpty();

        // Every result should have a player
        results.Should().AllSatisfy(r =>
        {
            r.Player.Should().NotBeNull();
            r.Player.PlayerId.Should().NotBeNullOrEmpty();
        });

        // Keepable players should have a cost round
        var keepable = results.Where(r => r.CanBeKept).ToList();
        keepable.Should().NotBeEmpty();
        keepable.Should().AllSatisfy(r => r.KeeperCostRound.Should().NotBeNull());

        // Non-keepable (rounds 1-3) should have null cost
        var nonKeepable = results.Where(r => !r.CanBeKept).ToList();
        nonKeepable.Should().AllSatisfy(r =>
        {
            r.KeeperCostRound.Should().BeNull();
            r.DraftRound.Should().NotBeNull();
            r.DraftRound.Should().BeLessThanOrEqualTo(3);
        });
    }
}
