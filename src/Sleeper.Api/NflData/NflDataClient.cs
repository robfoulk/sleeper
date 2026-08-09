using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sleeper.Api.Caching;
using Sleeper.Api.NflData.Models;

namespace Sleeper.Api.NflData;

public class NflDataClient : INflDataClient
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly NflDataOptions _options;

    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = null,
    };

    public NflDataClient(HttpClient http, IMemoryCache cache, IOptions<NflDataOptions> options)
    {
        _http = http;
        _cache = cache;
        _options = options.Value;
    }

    public Task<List<WeeklyPlayerStats>> GetWeeklyStatsAsync(int season, CancellationToken ct = default)
    {
        var url = $"{_options.StatsBaseUrl}stats_player_week_{season}.csv";
        var ttl = IsCurrentOrFutureSeason(season) ? _options.CurrentSeasonCacheTtl : _options.HistoricalCacheTtl;
        return GetOrCreateAsync($"nfl-weekly:{season}", ttl, () => DownloadCsvAsync<WeeklyPlayerStats>(url, ct), ct);
    }

    public Task<List<SeasonPlayerStats>> GetSeasonStatsAsync(int season, string seasonType = "reg", CancellationToken ct = default)
    {
        var url = $"{_options.StatsBaseUrl}stats_player_{seasonType}_{season}.csv";
        var ttl = IsCurrentOrFutureSeason(season) ? _options.CurrentSeasonCacheTtl : _options.HistoricalCacheTtl;
        return GetOrCreateAsync($"nfl-season:{seasonType}:{season}", ttl, () => DownloadCsvAsync<SeasonPlayerStats>(url, ct), ct);
    }

    public Task<List<PlayerIdMapping>> GetPlayerIdMappingsAsync(CancellationToken ct = default)
    {
        return GetOrCreateAsync("nfl-playerids", _options.PlayerIdsCacheTtl, () => DownloadCsvAsync<PlayerIdMapping>(_options.PlayerIdsUrl, ct), ct);
    }

    public async Task<Dictionary<string, string>> GetSleeperToGsisMapAsync(CancellationToken ct = default)
    {
        return await GetOrCreateAsync("nfl-sleeper-gsis-map", _options.PlayerIdsCacheTtl, async () =>
        {
            var mappings = await GetPlayerIdMappingsAsync(ct).ConfigureAwait(false);
            var map = new Dictionary<string, string>();
            foreach (var m in mappings)
            {
                if (!string.IsNullOrEmpty(m.SleeperId) && !string.IsNullOrEmpty(m.GsisId) && m.SleeperId != "NA" && m.GsisId != "NA")
                    map.TryAdd(m.SleeperId, m.GsisId);
            }
            return map;
        }, ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, SeasonPlayerStats>> GetSeasonStatsBySleeperIdAsync(int season, string seasonType = "reg", CancellationToken ct = default)
    {
        var ttl = IsCurrentOrFutureSeason(season) ? _options.CurrentSeasonCacheTtl : _options.HistoricalCacheTtl;
        return await GetOrCreateAsync($"nfl-season-sleeper:{seasonType}:{season}", ttl, async () =>
        {
            var statsTask = GetSeasonStatsAsync(season, seasonType, ct);
            var mapTask = GetSleeperToGsisMapAsync(ct);
            await Task.WhenAll(statsTask, mapTask).ConfigureAwait(false);

            var stats = await statsTask.ConfigureAwait(false);
            var sleeperToGsis = await mapTask.ConfigureAwait(false);

            // Reverse: GSIS -> Sleeper
            var gsisToSleeper = new Dictionary<string, string>();
            foreach (var (sleeperId, gsisId) in sleeperToGsis)
                gsisToSleeper.TryAdd(gsisId, sleeperId);

            var result = new Dictionary<string, SeasonPlayerStats>();
            foreach (var stat in stats)
            {
                if (gsisToSleeper.TryGetValue(stat.PlayerId, out var sleeperId))
                    result.TryAdd(sleeperId, stat);
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, List<WeeklyPlayerStats>>> GetWeeklyStatsBySleeperIdAsync(int season, CancellationToken ct = default)
    {
        var ttl = IsCurrentOrFutureSeason(season) ? _options.CurrentSeasonCacheTtl : _options.HistoricalCacheTtl;
        return await GetOrCreateAsync($"nfl-weekly-sleeper:{season}", ttl, async () =>
        {
            var statsTask = GetWeeklyStatsAsync(season, ct);
            var mapTask = GetSleeperToGsisMapAsync(ct);
            await Task.WhenAll(statsTask, mapTask).ConfigureAwait(false);

            var stats = await statsTask.ConfigureAwait(false);
            var sleeperToGsis = await mapTask.ConfigureAwait(false);

            var gsisToSleeper = new Dictionary<string, string>();
            foreach (var (sleeperId, gsisId) in sleeperToGsis)
                gsisToSleeper.TryAdd(gsisId, sleeperId);

            var result = new Dictionary<string, List<WeeklyPlayerStats>>();
            foreach (var stat in stats)
            {
                if (gsisToSleeper.TryGetValue(stat.PlayerId, out var sleeperId))
                {
                    if (!result.TryGetValue(sleeperId, out var list))
                    {
                        list = [];
                        result[sleeperId] = list;
                    }
                    list.Add(stat);
                }
            }
            return result;
        }, ct).ConfigureAwait(false);
    }

    // Helpers

    private async Task<List<T>> DownloadCsvAsync<T>(string url, CancellationToken ct)
    {
        using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CsvConfig);
        var records = new List<T>();
        await foreach (var record in csv.GetRecordsAsync<T>(ct).ConfigureAwait(false))
            records.Add(record);
        return records;
    }

    private Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory, CancellationToken ct)
        => _cache.GetOrCreateIfNotNullAsync(key, ttl, factory, ct);

    private static bool IsCurrentOrFutureSeason(int season) => season >= DateTime.UtcNow.Year - 1;
}
