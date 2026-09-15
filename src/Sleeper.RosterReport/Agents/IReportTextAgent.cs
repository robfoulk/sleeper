using Microsoft.Agents.AI;

namespace Sleeper.RosterReport.Agents;

internal interface IReportTextAgent
{
    Task<string> GenerateAsync(
        string prompt,
        AgentCallContext context,
        CancellationToken cancellationToken = default);
}

internal sealed record AgentCallContext(
    int? Week,
    string Role,
    string? Subject = null);

internal sealed class FoundryReportTextAgent(AIAgent agent) : IReportTextAgent
{
    public async Task<string> GenerateAsync(
        string prompt,
        AgentCallContext context,
        CancellationToken cancellationToken = default)
    {
        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Text?.Trim() ?? "";
    }
}
