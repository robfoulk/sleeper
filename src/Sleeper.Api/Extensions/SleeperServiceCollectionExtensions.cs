using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Sleeper.Api.NflData;
using Sleeper.Api.NflData.Scoring;
using Sleeper.Api.NflData.Services;
using Sleeper.Api.Services;
using Sleeper.Api.Injuries;

namespace Sleeper.Api.Extensions;

public static class SleeperServiceCollectionExtensions
{
    public static IServiceCollection AddSleeperApi(
        this IServiceCollection services,
        Action<SleeperClientOptions>? configure = null) =>
        AddSleeperApi(services, configure, configureInjuryStore: null, configureInjuryImport: null);

    public static IServiceCollection AddSleeperApi(
        this IServiceCollection services,
        Action<SleeperClientOptions>? configure,
        Action<InjuryStoreOptions>? configureInjuryStore,
        Action<InjuryImportOptions>? configureInjuryImport)
    {
        var options = new SleeperClientOptions();
        configure?.Invoke(options);

        services.Configure<SleeperClientOptions>(opt =>
        {
            opt.BaseUrl = options.BaseUrl;
            opt.PlayersCacheTtl = options.PlayersCacheTtl;
            opt.LeagueCacheTtl = options.LeagueCacheTtl;
            opt.MatchupCacheTtl = options.MatchupCacheTtl;
            opt.RosterCacheTtl = options.RosterCacheTtl;
            opt.DraftCacheTtl = options.DraftCacheTtl;
            opt.NflStateCacheTtl = options.NflStateCacheTtl;
            opt.UserCacheTtl = options.UserCacheTtl;
            opt.TrendingCacheTtl = options.TrendingCacheTtl;
            opt.TransactionCacheTtl = options.TransactionCacheTtl;
        });

        services.AddHttpClient<SleeperClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
        });

        services.TryAddSingleton<IMemoryCache, MemoryCache>();

        services.AddSingleton<ISleeperClient>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(nameof(SleeperClient));
            httpClient.BaseAddress = new Uri(options.BaseUrl);
            var inner = new SleeperClient(httpClient);
            var cache = sp.GetRequiredService<IMemoryCache>();
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SleeperClientOptions>>();
            return new CachedSleeperClient(inner, cache, opts);
        });

        services.TryAddSingleton<ISleeperService, SleeperService>();
        services.Configure<InjuryStoreOptions>(options => configureInjuryStore?.Invoke(options));
        services.TryAddSingleton<IInjuryStore, SqliteInjuryStore>();
        services.Configure<InjuryImportOptions>(options => configureInjuryImport?.Invoke(options));
        services.AddHttpClient<NflverseInjuryImporter>();
        services.TryAddSingleton<IInjuryImporter>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new NflverseInjuryImporter(
                factory.CreateClient(nameof(NflverseInjuryImporter)),
                sp.GetRequiredService<ISleeperClient>(),
                sp.GetRequiredService<INflDataClient>(),
                sp.GetRequiredService<IInjuryStore>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<InjuryImportOptions>>());
        });

        return services;
    }

    /// <summary>
    /// Adds the nflverse NFL data client for player stats, fantasy points, and ID mappings.
    /// </summary>
    public static IServiceCollection AddNflData(
        this IServiceCollection services,
        Action<NflDataOptions>? configure = null)
    {
        var options = new NflDataOptions();
        configure?.Invoke(options);

        services.Configure<NflDataOptions>(opt =>
        {
            opt.StatsBaseUrl = options.StatsBaseUrl;
            opt.PlayerIdsUrl = options.PlayerIdsUrl;
            opt.CurrentSeasonCacheTtl = options.CurrentSeasonCacheTtl;
            opt.HistoricalCacheTtl = options.HistoricalCacheTtl;
            opt.PlayerIdsCacheTtl = options.PlayerIdsCacheTtl;
        });

        services.AddHttpClient<NflDataClient>();
        services.TryAddSingleton<IMemoryCache, MemoryCache>();

        services.TryAddSingleton<INflDataClient>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(nameof(NflDataClient));
            var cache = sp.GetRequiredService<IMemoryCache>();
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NflDataOptions>>();
            return new NflDataClient(httpClient, cache, opts);
        });

        services.TryAddSingleton<IFantasyService, FantasyService>();
        services.TryAddSingleton<IAnalysisService, AnalysisService>();

        return services;
    }
}
