namespace Sleeper.Api.NflData;

public class NflDataOptions
{
    public string StatsBaseUrl { get; set; } = "https://github.com/nflverse/nflverse-data/releases/download/stats_player/";
    public string PlayerIdsUrl { get; set; } = "https://raw.githubusercontent.com/dynastyprocess/data/master/files/db_playerids.csv";
    public TimeSpan CurrentSeasonCacheTtl { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan HistoricalCacheTtl { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan PlayerIdsCacheTtl { get; set; } = TimeSpan.FromDays(7);
}
