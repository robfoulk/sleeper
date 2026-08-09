using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sleeper.Api;
using Sleeper.Api.NflData;
using Sleeper.Api.NflData.Scoring;
using Sleeper.Api.NflData.Services;
using Sleeper.Api.Services;

namespace Sleeper.McpServer.Tools;

[McpServerToolType]
public class LeagueTools
{
    [McpServerTool, Description("Get league information including teams, roster configuration, scoring settings, and keeper rules.")]
    public static async Task<string> GetLeagueInfo(
        ISleeperClient client,
        [Description("Sleeper league ID")] string league_id = ToolSupport.DefaultLeagueId,
        CancellationToken ct = default)
    {
        if (ToolSupport.Required(league_id, "League ID") is { } validation)
            return validation;

        return await ToolSupport.TryAsync(async () =>
        {
            var league = await client.GetLeagueAsync(league_id, ct);
            if (league is null) return "League not found.";

            var config = Api.Models.LeagueRosterConfig.FromLeague(league);
            var sb = new StringBuilder();
            sb.AppendLine($"## {league.Name}");
            sb.AppendLine($"- **Season:** {league.Season} ({league.Status})");
            sb.AppendLine($"- **Teams:** {config.Teams}");
            sb.AppendLine($"- **Max Keepers:** {config.MaxKeepers}");
            sb.AppendLine($"- **Roster Size:** {config.TotalRosterSize} (Starters: {config.StarterSlots.Values.Sum() + config.FlexSlots}, Bench: {config.BenchSlots})");
            sb.AppendLine($"- **Starter Slots:** {string.Join(", ", config.StarterSlots.Select(s => $"{s.Key}:{s.Value}"))}");
            if (config.FlexSlots > 0) sb.AppendLine($"- **Flex Slots:** {config.FlexSlots}");

            if (league.ScoringSettings is not null)
            {
                var key = new[] { "pass_yd", "pass_td", "rush_yd", "rush_td", "rec_yd", "rec_td", "rec", "fum_lost" };
                sb.AppendLine($"- **Key Scoring:** {string.Join(", ", key.Where(k => league.ScoringSettings.ContainsKey(k)).Select(k => $"{k}={league.ScoringSettings[k]}"))}");
            }

            return sb.ToString();
        });
    }

    [McpServerTool, Description("Get weekly matchup scoreboard showing all head-to-head results for a given week.")]
    public static async Task<string> GetMatchupScoreboard(
        ISleeperClient client,
        ISleeperService sleeperService,
        [Description("Week number (1-18)")] int week,
        [Description("Sleeper league ID")] string league_id = ToolSupport.DefaultLeagueId,
        CancellationToken ct = default)
    {
        if (ToolSupport.Between(week, 1, 18, "Week") is { } validation)
            return validation;
        if (ToolSupport.Required(league_id, "League ID") is { } leagueValidation)
            return leagueValidation;

        return await ToolSupport.TryAsync(async () =>
        {
            var league = await client.GetLeagueAsync(league_id, ct);
            if (league is null) return "League not found.";

            var scoreboard = await sleeperService.GetWeekScoreboardAsync(league_id, week, ct);
            var sb = new StringBuilder();
            sb.AppendLine($"## Week {week} Scoreboard -- {league.Name}");
            sb.AppendLine();

            foreach (var m in scoreboard.OrderByDescending(m => (m.Team1Points ?? 0) + (m.Team2Points ?? 0)))
            {
                var t1 = m.Team1TeamName ?? m.Team1DisplayName ?? $"Team {m.Team1RosterId}";
                var t2 = m.Team2TeamName ?? m.Team2DisplayName ?? $"Team {m.Team2RosterId}";
                var team1Points = m.Team1Points ?? 0;
                var team2Points = m.Team2Points ?? 0;
                var result = team1Points == team2Points
                    ? "Tie"
                    : $"Winner: {(team1Points > team2Points ? t1 : t2)}";
                sb.AppendLine($"- **{t1}** {m.Team1Points:F2} vs {m.Team2Points:F2} **{t2}** ({result})");
            }

            return sb.ToString();
        });
    }

    [McpServerTool, Description("Get league-wide positional rankings for a season. Shows QB1-QB32, RB1-RB48, etc. ranked by the league's own scoring settings.")]
    public static async Task<string> GetLeagueRankings(
        IAnalysisService analysis,
        [Description("Season year (e.g. 2025)")] int season,
        [Description("Position to show (QB, RB, WR, TE, K) or 'all'")] string position = "all",
        [Description("How many players to show per position")] int top = 20,
        [Description("Sleeper league ID")] string league_id = ToolSupport.DefaultLeagueId,
        CancellationToken ct = default)
    {
        if (ToolSupport.Between(top, 1, 100, "Top") is { } validation)
            return validation;

        return await ToolSupport.TryAsync(async () =>
        {
        var rankings = await analysis.GetLeagueRankingsAsync(league_id, season, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"## League Rankings ({season})");

        var positions = position.ToUpperInvariant() == "ALL"
            ? new[] { "QB", "RB", "WR", "TE", "K" }
            : new[] { position.ToUpperInvariant() };

        foreach (var pos in positions)
        {
            if (!rankings.ByPosition.TryGetValue(pos, out var players)) continue;

            sb.AppendLine();
            sb.AppendLine($"### {pos} Rankings");
            sb.AppendLine("| Rank | Player | Team | PPG | Total |");
            sb.AppendLine("|------|--------|------|-----|-------|");

            foreach (var p in players.Take(top))
                sb.AppendLine($"| {pos}{p.PositionalRank} | {p.PlayerName} | {p.Team} | {p.Ppg:F1} | {p.TotalPoints:F0} |");
        }

            return sb.ToString();
        });
    }

    [McpServerTool, Description("Get trending players being most added or dropped across all Sleeper leagues. Useful for seeing market activity and identifying breakout candidates.")]
    public static async Task<string> GetTrendingPlayers(
        ISleeperClient client,
        [Description("'add' for most added, 'drop' for most dropped")] string type = "add",
        [Description("Number of players to show")] int limit = 15,
        CancellationToken ct = default)
    {
        if (!string.Equals(type, "add", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "drop", StringComparison.OrdinalIgnoreCase))
            return "Error: Type must be 'add' or 'drop'.";
        if (ToolSupport.Between(limit, 1, 100, "Limit") is { } validation)
            return validation;

        return await ToolSupport.TryAsync(async () =>
        {
        var trending = await client.GetTrendingPlayersAsync("nfl", type, limit: limit, ct: ct);
        var allPlayers = await client.GetAllPlayersAsync(ct: ct);

        var sb = new StringBuilder();
        sb.AppendLine($"## Most {(type == "add" ? "Added" : "Dropped")} Players");

        foreach (var t in trending)
        {
            var name = allPlayers.TryGetValue(t.PlayerId, out var p) ? $"{p.FullName} ({p.Position}, {p.Team})" : t.PlayerId;
            sb.AppendLine($"- **{name}** -- {t.Count} {type}s");
        }

            return sb.ToString();
        });
    }

    [McpServerTool, Description("Score a player's weekly stats using the league's specific scoring settings. Returns fantasy points and a breakdown of how points were earned.")]
    public static async Task<string> ScorePlayerWeek(
        IFantasyService fantasyService,
        [Description("Sleeper player ID (numeric string)")] string player_id,
        [Description("Season year")] int season,
        [Description("Week number")] int week,
        [Description("Sleeper league ID")] string league_id = ToolSupport.DefaultLeagueId,
        CancellationToken ct = default)
    {
        if (ToolSupport.Required(player_id, "Player ID") is { } validation)
            return validation;
        if (ToolSupport.Positive(season, "Season") is { } seasonValidation)
            return seasonValidation;
        if (ToolSupport.Between(week, 1, 18, "Week") is { } weekValidation)
            return weekValidation;

        return await ToolSupport.TryAsync(async () =>
        {
            var score = await fantasyService.GetPlayerWeekScoreAsync(league_id, player_id, season, week, ct);
            if (score is null) return $"No stats found for player {player_id} in {season} week {week}.";

            var sb = new StringBuilder();
            sb.AppendLine($"## {score.PlayerName} -- Week {week}, {season}");
            sb.AppendLine($"**Total: {score.TotalPoints:F2} pts**");
            sb.AppendLine();

            foreach (var b in score.Breakdown)
                sb.AppendLine($"- {b.Label}: {b.StatValue:G} x {b.PointsPerUnit} = **{b.Points:F2}**");

            return sb.ToString();
        });
    }

    [McpServerTool, Description("Get draft history for a league showing all picks, rounds, and keeper designations.")]
    public static async Task<string> GetDraftHistory(
        ISleeperClient client,
        [Description("Sleeper league ID")] string league_id = ToolSupport.DefaultLeagueId,
        [Description("Maximum rounds to show (0 for all)")] int max_rounds = 5,
        CancellationToken ct = default)
    {
        if (max_rounds < 0)
            return "Error: Maximum rounds cannot be negative.";

        return await ToolSupport.TryAsync(async () =>
        {
            var drafts = await client.GetLeagueDraftsAsync(league_id, ct);
            var draft = drafts.FirstOrDefault(d => d.Status == "complete");
            if (draft is null) return "No completed draft found for this league.";

            var picks = await client.GetDraftPicksAsync(draft.DraftId, ct);
            if (picks.Count == 0)
                return $"Draft found for {draft.Season}, but no picks are available.";

            var sb = new StringBuilder();
            sb.AppendLine($"## Draft Results ({draft.Season}, {draft.Type})");
            sb.AppendLine($"**Total Picks:** {picks.Count}");

            var totalRounds = picks.Max(p => p.Round);
            var maxRd = max_rounds > 0 ? max_rounds : totalRounds;
            var byRound = picks.GroupBy(p => p.Round).OrderBy(g => g.Key);

            foreach (var round in byRound.Where(r => r.Key <= maxRd))
            {
                sb.AppendLine();
                sb.AppendLine($"### Round {round.Key}");
                foreach (var pick in round.OrderBy(p => p.PickNo))
                {
                    var name = pick.Metadata is not null ? $"{pick.Metadata.FirstName} {pick.Metadata.LastName}" : pick.PlayerId;
                    var pos = pick.Metadata?.Position ?? "?";
                    var team = pick.Metadata?.Team ?? "?";
                    var keeper = pick.IsKeeper == true ? " 🔒" : "";
                    sb.AppendLine($"- Pick {pick.PickNo}: **{name}** ({pos}, {team}){keeper}");
                }
            }

            if (max_rounds > 0 && maxRd < totalRounds)
                sb.AppendLine($"\n*Showing first {max_rounds} rounds of {totalRounds}*");

            return sb.ToString();
        });
    }
}
