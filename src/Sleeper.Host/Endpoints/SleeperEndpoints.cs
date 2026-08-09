using Microsoft.AspNetCore.Mvc;
using Sleeper.Api;
using Sleeper.Api.NflData.Scoring;
using Sleeper.Api.NflData.Services;
using Sleeper.Api.Services;
using Sleeper.McpServer.Tools;

namespace Sleeper.Host.Endpoints;

internal static class SleeperEndpoints
{
    private const string Markdown = "text/markdown; charset=utf-8";

    public static IEndpointRouteBuilder MapSleeperApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Sleeper");

        // League
        api.MapGet("/league/{leagueId}", async (
                string leagueId,
            [FromServices] ISleeperClient client,
            CancellationToken ct) =>
            Md(await LeagueTools.GetLeagueInfo(client, leagueId, ct)))
            .WithName("GetLeagueInfo")
            .WithSummary("League info, roster config, scoring, keeper rules.");

        api.MapGet("/league/{leagueId}/scoreboard/{week:int}", async (
                string leagueId, int week,
                [FromServices] ISleeperClient client,
                [FromServices] ISleeperService sleeperService,
                CancellationToken ct) =>
                Md(await LeagueTools.GetMatchupScoreboard(client, sleeperService, week, leagueId, ct)))
            .WithName("GetMatchupScoreboard");

        api.MapGet("/league/{leagueId}/rankings/{season:int}", async (
                string leagueId, int season,
                [FromServices] IAnalysisService analysis,
                [FromQuery] string? position,
                [FromQuery] int? top,
                CancellationToken ct) =>
                Md(await LeagueTools.GetLeagueRankings(analysis, season, position ?? "all", top ?? 20, leagueId, ct)))
            .WithName("GetLeagueRankings");

        api.MapGet("/league/{leagueId}/draft", async (
                string leagueId,
                [FromServices] ISleeperClient client,
                [FromQuery] int? maxRounds,
                CancellationToken ct) =>
                Md(await LeagueTools.GetDraftHistory(client, leagueId, maxRounds ?? 5, ct)))
            .WithName("GetDraftHistory");

        api.MapGet("/league/{leagueId}/score/{playerId}/{season:int}/{week:int}", async (
                string leagueId, string playerId, int season, int week,
                [FromServices] IFantasyService fantasy,
                CancellationToken ct) =>
                Md(await LeagueTools.ScorePlayerWeek(fantasy, playerId, season, week, leagueId, ct)))
            .WithName("ScorePlayerWeek");

        api.MapGet("/trending", async (
                [FromServices] ISleeperClient client,
                [FromQuery] string? type,
                [FromQuery] int? limit,
                CancellationToken ct) =>
                Md(await LeagueTools.GetTrendingPlayers(client, type ?? "add", limit ?? 15, ct)))
            .WithName("GetTrendingPlayers");

        // Keepers / Roster / Player
        api.MapGet("/league/{leagueId}/keepers/{username}", async (
                string leagueId, string username,
                [FromServices] IAnalysisService analysis,
                CancellationToken ct) =>
                Md(await KeeperTools.AnalyzeKeepers(analysis, username, leagueId, ct)))
            .WithName("AnalyzeKeepers");

        api.MapGet("/league/{leagueId}/keepers-declared", async (
                string leagueId,
                [FromServices] ISleeperService sleeperService,
                CancellationToken ct) =>
                Md(await KeeperTools.GetDeclaredKeepers(sleeperService, leagueId, ct: ct)))
            .WithName("GetDeclaredKeepers")
            .WithSummary("Actual declared keepers for every team. Empty for most of the offseason.");

        api.MapGet("/league/{leagueId}/keepers-declared/{username}", async (
                string leagueId, string username,
                [FromServices] ISleeperService sleeperService,
                CancellationToken ct) =>
                Md(await KeeperTools.GetDeclaredKeepers(sleeperService, leagueId, username, ct)))
            .WithName("GetDeclaredKeepersForUser");

        api.MapGet("/league/{leagueId}/roster/{username}", async (
                string leagueId, string username,
                [FromServices] IAnalysisService analysis,
                CancellationToken ct) =>
                Md(await RosterTools.EvaluateRoster(analysis, username, leagueId, ct)))
            .WithName("EvaluateRoster");

        api.MapGet("/league/{leagueId}/player/{playerName}", async (
                string leagueId, string playerName,
                [FromServices] IAnalysisService analysis,
                CancellationToken ct) =>
                Md(await PlayerTools.PlayerDeepDive(analysis, playerName, leagueId, ct)))
            .WithName("PlayerDeepDive");

        api.MapGet("/players/search", async (
                [FromServices] IAnalysisService analysis,
                [FromQuery] string q,
                [FromQuery] string? position,
                CancellationToken ct) =>
                Md(await PlayerTools.SearchPlayers(analysis, q, position, ct)))
            .WithName("SearchPlayers");

        return app;
    }

    private static IResult Md(string content) => Results.Text(content, Markdown);
}
