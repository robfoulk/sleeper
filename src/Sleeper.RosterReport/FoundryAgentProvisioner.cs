using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Microsoft.Agents.AI;
using OpenAI.Responses;
using System.ClientModel;

#pragma warning disable OPENAI001 // Responses API is in preview

namespace Sleeper.RosterReport;

/// <summary>
/// Ensures the Foundry prompt agent exists in the project, creating it with
/// web-search capability when not found.
/// </summary>
internal static class FoundryAgentProvisioner
{
    public static string DefaultInstructions => GetDefaultInstructions(DateTime.UtcNow.Year);

    public static string GetDefaultInstructions(int upcomingSeason)
        => FoundryAgentInstructions.PlayerResearch(upcomingSeason);

    /// <summary>
    /// Checks whether the named agent exists in the Foundry project.
    /// If missing, creates a new prompt agent with web search tool enabled.
    /// Uses TokenCredential (e.g. DefaultAzureCredential) — API key auth
    /// is not supported by AIProjectClient.
    /// </summary>
    public static async Task<AIAgent> GetOrCreateAgentAsync(
        AIProjectClient projectClient,
        string agentName,
        string modelDeployment,
        string instructions,
        string description,
        bool enableWebSearch,
        bool allowCreate,
        FoundryAgentRole role,
        CancellationToken ct = default)
    {
        var admin = projectClient.AgentAdministrationClient;

        try
        {
            var existing = await admin.GetAgentAsync(agentName, ct).ConfigureAwait(false);
            Console.WriteLine($"  (Foundry agent '{agentName}' found)");
            return projectClient.AsAIAgent(existing.Value);
        }
        catch (ClientResultException ex) when (ex.Status == 404)
        {
            if (!allowCreate)
                throw new InvalidOperationException($"Foundry agent '{agentName}' was not found and automatic creation is disabled.", ex);
        }

        Console.WriteLine($"  (Foundry agent '{agentName}' not found; creating server-side agent...)");

        var definition = new DeclarativeAgentDefinition(model: modelDeployment)
        {
            Instructions = instructions
        };
        if (enableWebSearch)
            definition.Tools.Add(ResponseTool.CreateWebSearchTool());

        var options = new ProjectsAgentVersionCreationOptions(definition)
        {
            Description = description
        };
        options.Metadata.Add("app", "Sleeper.RosterReport");
        options.Metadata.Add("role", role.ToString());

        var created = await admin.CreateAgentVersionAsync(
            agentName: agentName,
            options: options,
            cancellationToken: ct);

        Console.WriteLine($"  (Created Foundry agent '{agentName}' v{created.Value.Version})");
        return projectClient.AsAIAgent(created.Value);
    }
}
