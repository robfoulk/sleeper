namespace Sleeper.Api;

public class SleeperClientOptions
{
    public string BaseUrl { get; set; } = "https://api.sleeper.app/v1/";
    public TimeSpan PlayersCacheTtl { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan LeagueCacheTtl { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan MatchupCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan RosterCacheTtl { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan DraftCacheTtl { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan NflStateCacheTtl { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan UserCacheTtl { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan TrendingCacheTtl { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan TransactionCacheTtl { get; set; } = TimeSpan.FromMinutes(15);
}
