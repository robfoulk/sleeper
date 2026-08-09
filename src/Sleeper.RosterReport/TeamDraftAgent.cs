using Microsoft.Agents.AI;

#pragma warning disable OPENAI001 // Responses API is in preview

namespace Sleeper.RosterReport;

/// <summary>
/// Uses Azure AI Foundry Responses API to provide draft-aware opinions on
/// each player in a team deep dive, incorporating current ADP, news, and
/// draft stock information via web search.
/// </summary>
internal sealed class TeamDraftAgent
{
    private readonly AIAgent _agent;
    private readonly string _agentName;
    private readonly int _upcomingSeason;

    private TeamDraftAgent(AIAgent agent, string agentName, int upcomingSeason)
    {
        _agent = agent;
        _agentName = agentName;
        _upcomingSeason = upcomingSeason;
    }

    /// <summary>
    /// Try to build the agent from Foundry configuration.
    /// Uses a server-side Foundry agent with web search enabled.
    /// Returns null if required configuration is not set or connection fails.
    /// </summary>
    public static async Task<TeamDraftAgent?> TryCreateAsync(FoundryAgentSettings settings, int upcomingSeason)
    {
        var runtime = await FoundryAgentFactory.TryCreateAsync(
            settings,
            FoundryAgentRole.TeamDraftOutlook,
            FoundryAgentInstructions.PlayerResearch(upcomingSeason),
            enableWebSearch: true).ConfigureAwait(false);
        return runtime is null
            ? null
            : new TeamDraftAgent(runtime.Agent, runtime.AgentName, upcomingSeason);
    }

    /// <summary>
    /// Gets a draft-aware opinion for a player, considering their stats profile,
    /// current ADP, news, and trade/draft stock.
    /// </summary>
    public async Task<string> GetDraftOpinionAsync(
        string playerName, string? position, int? age,
        decimal weightedPpg, decimal projectedPoints, decimal vorp,
        string trendDirection, decimal trendPerYear,
        decimal durabilityPct, decimal consistencyScore,
        CancellationToken ct = default)
    {
        var prompt =
            $"Player: {playerName} ({position ?? "?"}, age {age?.ToString() ?? "?"})\n" +
            $"Upcoming {_upcomingSeason} fantasy football season.\n" +
            $"Statistical profile:\n" +
            $"- Weighted PPG: {weightedPpg:F1}\n" +
            $"- Projected season points: {projectedPoints:F0}\n" +
            $"- VORP: {vorp:F1}\n" +
            $"- Trend: {trendDirection} ({trendPerYear:+0.0;-0.0} PPG/yr)\n" +
            $"- Durability: {durabilityPct:F0}%\n" +
            $"- Consistency: {consistencyScore:F0}/100\n\n" +
            $"Search the web for this player's {_upcomingSeason} fantasy football ADP (average draft position). " +
            $"Only use {_upcomingSeason} ADP data — ignore any prior-year ADP as those seasons are completed. " +
            $"Also search for {_upcomingSeason} NFL news, injury updates, depth chart changes, and offseason moves. " +
            $"Then give a 3-5 sentence draft outlook: Is this player being drafted too high, too low, or about right? " +
            $"What's the key upside and risk? Any news that changes the outlook?";

        var delays = new[] { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var response = await _agent.RunAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
                return response.Text?.Trim() ?? "(no response)";
            }
            catch (Exception ex) when (attempt < delays.Length && IsRateLimit(ex))
            {
                await Task.Delay(delays[attempt], ct);
            }
        }
    }

    private static bool IsRateLimit(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("429") || msg.Contains("rate_limit", StringComparison.OrdinalIgnoreCase);
    }
}
