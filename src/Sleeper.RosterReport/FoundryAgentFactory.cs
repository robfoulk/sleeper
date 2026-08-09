using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;

namespace Sleeper.RosterReport;

internal static class FoundryAgentFactory
{
    public static async Task<FoundryAgentRuntime?> TryCreateAsync(
        FoundryAgentSettings settings,
        FoundryAgentRole role,
        string instructions,
        bool enableWebSearch,
        CancellationToken ct = default)
    {
        var label = FoundryAgentSettings.LabelFor(role);
        if (!settings.IsConfigured)
            return null;

        var agentName = settings.AgentNameFor(role);
        var modelDeployment = settings.ModelDeploymentFor(role);

        try
        {
            var credential = new DefaultAzureCredential();
            var projectClient = new AIProjectClient(new Uri(settings.ProjectEndpoint!), credential);
            var agent = await FoundryAgentProvisioner.GetOrCreateAgentAsync(
                projectClient,
                agentName,
                modelDeployment,
                instructions,
                FoundryAgentSettings.DescriptionFor(role),
                enableWebSearch,
                settings.AutoCreateMissingAgents,
                role,
                ct).ConfigureAwait(false);

            Console.WriteLine($"  ({label}: Foundry agent '{agentName}', model '{modelDeployment}')");
            return new FoundryAgentRuntime(agent, agentName, modelDeployment);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ({label} init failed: {ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }
}

internal sealed record FoundryAgentRuntime(AIAgent Agent, string AgentName, string ModelDeployment);
