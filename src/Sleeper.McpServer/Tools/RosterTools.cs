using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Sleeper.Api.NflData.Services;

namespace Sleeper.McpServer.Tools;

[McpServerToolType]
public class RosterTools
{
    [McpServerTool, Description("Evaluate a roster's positional strengths and weaknesses. Shows which positions are strong, weak, and where upgrades are needed. Better than just listing players -- this uses league-wide rankings to give real context.")]
    public static async Task<string> EvaluateRoster(
        IAnalysisService analysis,
        [Description("Sleeper username")] string username,
        [Description("Sleeper league ID")] string league_id = ToolSupport.DefaultLeagueId,
        CancellationToken ct = default)
    {
        if (ToolSupport.Required(username, "Username") is { } validation)
            return validation;

        return await ToolSupport.TryAsync(async () =>
        {
            var eval = await analysis.EvaluateRosterAsync(league_id, username, ct);
            if (eval is null) return $"Could not evaluate roster for '{username}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"## Roster Evaluation -- {eval.OwnerName}");
            sb.AppendLine($"**League:** {eval.LeagueName}");
            sb.AppendLine();

            sb.AppendLine("### Positional Breakdown");
            sb.AppendLine("| Position | Rating | Players | Starters | Best Player | Avg PPG |");
            sb.AppendLine("|----------|--------|---------|----------|-------------|---------|");

            foreach (var (pos, strength) in eval.Positions.OrderBy(p => p.Key))
            {
                var best = strength.Players.Count > 0 ? $"{strength.Players[0].Name} ({pos}{strength.Players[0].PositionalRank})" : "None";
                sb.AppendLine($"| {pos} | {strength.Rating} | {strength.PlayerCount} | {strength.StarterSlots} | {best} | {strength.AveragePpg:F1} |");
            }

            if (eval.Strengths.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Strengths");
                foreach (var s in eval.Strengths)
                    sb.AppendLine($"- {s}");
            }

            if (eval.Weaknesses.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Weaknesses");
                foreach (var w in eval.Weaknesses)
                    sb.AppendLine($"- {w}");
            }

            if (eval.Recommendations.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Recommendations");
                foreach (var r in eval.Recommendations)
                    sb.AppendLine($"- {r}");
            }

            return sb.ToString();
        });
    }
}
