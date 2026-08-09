using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sleeper.Api.NflData.Analytics;
using Sleeper.Api.NflData.Services;

namespace Sleeper.McpServer.Tools;

[McpServerToolType]
public class PlayerTools
{
    [McpServerTool, Description("Deep dive analysis on a specific player. Returns 3-year stats history, weekly scoring trends, consistency metrics, aging curve, VORP, and projections. Use when asked about a specific player's performance, history, or value.")]
    public static async Task<string> PlayerDeepDive(
        IAnalysisService analysis,
        [Description("Player name or partial name to search for")] string player_name,
        [Description("Sleeper league ID (needed for league-specific scoring)")] string league_id = ToolSupport.DefaultLeagueId,
        CancellationToken ct = default)
    {
        if (ToolSupport.Required(player_name, "Player name") is { } validation)
            return validation;

        return await ToolSupport.TryAsync(async () =>
        {
            var dive = await analysis.GetPlayerDeepDiveAsync(league_id, player_name, ct: ct);
            if (dive is null) return $"Player '{player_name}' not found.";

            var p = dive.Player;
            var a = dive.Analysis;
            var sb = new StringBuilder();
            sb.AppendLine($"## {p.FullName}");
            sb.AppendLine($"**{p.Position}** | {p.Team ?? "FA"} | Age {p.Age} | {p.YearsExp ?? 0} yrs exp");
            var rank = dive.Rankings.GetRankLabel(p.PlayerId);
            if (rank is not null) sb.AppendLine($"**League Rank:** {rank}");
            sb.AppendLine();

            if (dive.SeasonHistory.Count > 0)
            {
                sb.AppendLine("### Season-by-Season");
                sb.AppendLine("| Season | Games | Total | PPG | StdDev |");
                sb.AppendLine("|--------|-------|-------|-----|--------|");
                foreach (var sh in dive.SeasonHistory.OrderBy(s => s.Season))
                    sb.AppendLine($"| {sh.Season} | {sh.GamesPlayed} | {sh.TotalPoints:F1} | {sh.Ppg:F1} | {sh.StdDev:F1} |");
            }

            var recentYear = dive.SeasonHistory.Count > 0 ? dive.SeasonHistory.Max(s => s.Season) : 0;
            if (dive.WeeklyPointsBySeason.TryGetValue(recentYear, out var weekly) && weekly.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"### {recentYear} Weekly Scores");
                foreach (var score in weekly)
                    sb.AppendLine($"- Week {score.Week}: {score.Points:F1} pts");
            }

            sb.AppendLine();
            sb.AppendLine("### Projections & Analysis");
            sb.AppendLine($"- **Weighted PPG:** {a.WeightedPpg:F1}");
            sb.AppendLine($"- **Age-Adjusted PPG:** {a.AgeAdjustedPpg:F1} (factor: {AgingCurve.GetFactor(p.Position, p.Age):F2})");
            sb.AppendLine($"- **Projected Season:** {a.ProjectedSeasonPoints:F0} pts");
            sb.AppendLine($"- **VORP:** {a.Vorp:F1}");
            sb.AppendLine($"- **Trend:** {a.TrendDirection} ({a.TrendPerYear:+0.0;-0.0} PPG/year)");
            sb.AppendLine($"- **Durability:** {a.DurabilityPct:F0}%");
            sb.AppendLine($"- **Consistency:** {a.ConsistencyScore:F0}/100 (Boom: {a.BoomRate:F0}% / Bust: {a.BustRate:F0}%)");

            return sb.ToString();
        });
    }

    [McpServerTool, Description("Search for NFL players by name, optionally filtered by position. Returns matching players with their team, position, and Sleeper player ID.")]
    public static async Task<string> SearchPlayers(
        IAnalysisService analysis,
        [Description("Player name or partial name")] string query,
        [Description("Position filter (QB, RB, WR, TE, K, DEF)")] string? position = null,
        CancellationToken ct = default)
    {
        if (ToolSupport.Required(query, "Query") is { } validation)
            return validation;

        return await ToolSupport.TryAsync(async () =>
        {
        var results = await analysis.SearchPlayersAsync(query, position, ct);
        if (results.Count == 0) return $"No players found matching '{query}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"### Search Results for '{query}'");
        foreach (var p in results)
            sb.AppendLine($"- **{p.FullName}** ({p.Position}, {p.Team ?? "FA"}) -- ID: {p.PlayerId}");

            return sb.ToString();
        });
    }
}
