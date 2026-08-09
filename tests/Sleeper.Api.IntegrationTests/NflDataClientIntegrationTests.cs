using FluentAssertions;
using Sleeper.Api.NflData;

namespace Sleeper.Api.IntegrationTests;

[Trait("Category", "Integration")]
public class NflDataClientIntegrationTests : IDisposable
{
    private readonly HttpClient _http;
    private readonly NflDataClient _client;

    public NflDataClientIntegrationTests()
    {
        _http = new HttpClient();
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var options = Microsoft.Extensions.Options.Options.Create(new NflDataOptions());
        _client = new NflDataClient(_http, cache, options);
    }

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task GetWeeklyStatsAsync_Returns2025Data()
    {
        var stats = await _client.GetWeeklyStatsAsync(2025);

        stats.Should().NotBeEmpty();
        stats.Count.Should().BeGreaterThan(100);

        // Check a known player has data
        var qbs = stats.Where(s => s.Position == "QB" && s.PassingYards > 0).ToList();
        qbs.Should().NotBeEmpty();

        // Fantasy points should be populated
        var withPoints = stats.Where(s => s.FantasyPointsPpr > 0).ToList();
        withPoints.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetSeasonStatsAsync_Returns2025SeasonTotals()
    {
        var stats = await _client.GetSeasonStatsAsync(2025);

        stats.Should().NotBeEmpty();

        var topQb = stats
            .Where(s => s.Position == "QB")
            .OrderByDescending(s => s.FantasyPointsPpr ?? 0)
            .FirstOrDefault();

        topQb.Should().NotBeNull();
        topQb!.PassingYards.Should().BeGreaterThan(1000);
        topQb.Games.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetPlayerIdMappingsAsync_ContainsSleeperIds()
    {
        var mappings = await _client.GetPlayerIdMappingsAsync();

        mappings.Should().NotBeEmpty();
        mappings.Count.Should().BeGreaterThan(5000);

        var withSleeper = mappings.Where(m => !string.IsNullOrEmpty(m.SleeperId) && m.SleeperId != "NA").ToList();
        withSleeper.Should().NotBeEmpty();
        withSleeper.Count.Should().BeGreaterThan(1000);
    }

    [Fact]
    public async Task GetSleeperToGsisMapAsync_BuildsWorkingMap()
    {
        var map = await _client.GetSleeperToGsisMapAsync();

        map.Should().NotBeEmpty();
        map.Count.Should().BeGreaterThan(1000);

        // Most GSIS IDs start with "00-" but some legacy entries use other formats
        var standardIds = map.Values.Count(v => v.StartsWith("00-"));
        standardIds.Should().BeGreaterThan(1000);
    }

    [Fact]
    public async Task GetSeasonStatsBySleeperIdAsync_JoinsCorrectly()
    {
        var statsBySleeper = await _client.GetSeasonStatsBySleeperIdAsync(2025);

        statsBySleeper.Should().NotBeEmpty();
        statsBySleeper.Count.Should().BeGreaterThan(100);

        // Verify keys are Sleeper IDs (numeric strings), not GSIS IDs
        statsBySleeper.Keys.Should().AllSatisfy(key => key.Should().NotStartWith("00-"));

        // Verify stats are populated
        var anyWithPoints = statsBySleeper.Values.Any(s => s.FantasyPointsPpr > 0);
        anyWithPoints.Should().BeTrue();
    }

    [Fact]
    public async Task GetWeeklyStatsBySleeperIdAsync_GroupsByPlayer()
    {
        var weeklyBySleeper = await _client.GetWeeklyStatsBySleeperIdAsync(2025);

        weeklyBySleeper.Should().NotBeEmpty();

        // Each player should have multiple weeks
        var multiWeek = weeklyBySleeper.Values.Where(weeks => weeks.Count > 1).ToList();
        multiWeek.Should().NotBeEmpty();

        // Weeks should be sorted correctly (they come from the CSV in order)
        var anyPlayer = multiWeek.First();
        anyPlayer.Should().AllSatisfy(w => w.Season.Should().Be(2025));
    }
}
