# Foundry Agent Best Practices

This app uses server-side, versioned Microsoft Foundry agents. Keep that line bright: agent definitions live in Foundry, and the console app wraps those managed agents at runtime. The multi-library naming is genuinely easy to mix up, so use this document before adding the next agent.

## Library Roles

| Library or type | Use it for | Do not use it for |
| --- | --- | --- |
| `Azure.AI.Projects` / `AIProjectClient` | Connect to the Foundry project endpoint. | Defining per-call prompt behavior by itself. |
| `AIProjectClient.AgentAdministrationClient` | Retrieve server-side agents with `GetAgentAsync`; create a missing managed agent with `CreateAgentVersionAsync`. | Routine runtime prompting logic. |
| `Azure.AI.Projects.Agents` models | Build managed agent definitions, for example `DeclarativeAgentDefinition` and `ProjectsAgentVersionCreationOptions`. | In-process Agent Framework orchestration. |
| `Microsoft.Agents.AI.Foundry` | Wrap an existing Foundry agent record/version as an `AIAgent` with `projectClient.AsAIAgent(recordOrVersion)`. | Creating the server-side agent resource. |
| `Microsoft.Agents.AI` | Run the wrapped `AIAgent` with `RunAsync`. | Foundry project administration. |
| `AIProjectClient.AsAIAgent(model, instructions, ...)` | Direct Responses-agent inference only. Useful for experiments. | New app agents in this repo. It does not create a server-side Foundry agent. |
| `ProjectResponsesClient` / `CreateResponseOptions` | Low-level direct Responses API calls. | New managed agents in this repo. |

Current package pins are intentional:

- `Azure.AI.Projects` `2.0.1` for the project client. It brings the native agent administration models through `Azure.AI.Projects.Agents`.
- `Microsoft.Agents.AI.Foundry` `1.3.0` for Agent Framework wrapping.
- `Azure.Identity` `1.21.0` for local developer auth through `DefaultAzureCredential`.

## Runtime Pattern

The app should follow this order for every managed agent:

1. Resolve `FoundryAgentSettings` from `appsettings.json`, `appsettings.Development.json`, user secrets, and environment variables.
2. Build an `AIProjectClient` with `Foundry:ProjectEndpoint` and `DefaultAzureCredential`.
3. Call `AgentAdministrationClient.GetAgentAsync(agentName)`.
4. If the agent exists, wrap the returned record with `projectClient.AsAIAgent(record)`.
5. If the agent is missing and `Foundry:AutoCreateMissingAgents` is true, create a server-side agent with `CreateAgentVersionAsync`, then wrap the returned version with `projectClient.AsAIAgent(version)`.
6. Run prompts through `AIAgent.RunAsync(prompt)`.

Existing agents are treated as the source of truth. The app creates missing agents as a development convenience, but it should not silently rewrite an existing Foundry agent every time it starts. If an instruction change needs to become authoritative for an existing agent, make that a deliberate versioning step.

## Configuration

Use structured config first, user secrets for machine-local values, and environment variables only as deployment overrides. The full setup guide lives in [foundry-agent-configuration.md](foundry-agent-configuration.md).

The short version:

- `appsettings.json` keeps checked-in defaults and canonical agent names.
- `appsettings.Development.json` is local-only and ignored by git.
- User secrets are the preferred local place for `Foundry:ProjectEndpoint`, `Foundry:ModelDeployment`, and `Foundry:SeasonModelDeployment`.
- Environment variables are best for temporary runs and hosted deployment overrides.
- New setup should use `Foundry:*` keys. Backward-compatible aliases exist only inside `FoundryAgentSettings`.

## Adding A New Agent

1. Add a new `FoundryAgentRole` value and default name in `FoundryAgentSettings`.
2. Add the instruction text in `FoundryAgentInstructions`. Keep global behavior there; put task-specific data in the runtime prompt.
3. Create the runtime wrapper through `FoundryAgentFactory.TryCreateAsync(settings, role, instructions, enableWebSearch)`.
4. Use `enableWebSearch: true` only when the server-side agent needs current external data. Recap prose agents should usually stay tool-free and grounded in the prompt JSON.
5. Call `AIAgent.RunAsync(prompt)` from the feature class. Do not pass new instructions or tools per request for a managed Foundry agent.
6. Add the agent name to `appsettings.json` and update [foundry-agent-configuration.md](foundry-agent-configuration.md) with any required keys.
7. Run `dotnet build src/Sleeper.RosterReport/Sleeper.RosterReport.csproj` and the non-integration test suite.

## Instruction Guidance

Keep instructions stable, short, and role-level. Good instructions say who the agent is, what evidence it can use, what it must never invent, and what tone to use. They should not include weekly scores, roster JSON, or command-specific details; those belong in the prompt built at call time.

For this league, the voice should be competitive and specific, with room for a little family-league bite. It should not be solemn. A useful anchor: this is a boys' game played by grown men, so the writing can jab, but it should not sneer.

Use these rules for new instructions:

- State the data contract: use only prompt JSON for scores, records, player stats, standings, and awards.
- State tool rules: web search is for current ADP/news only; if current market data is thin, say so.
- State name rules: use owner real names or team names, never Sleeper usernames.
- State invention rules: no fake scores, injuries, transactions, coaches, locker rooms, betting lines, or relationships.
- Keep reusable tone in instructions and put per-command constraints in the runtime prompt.

## Red Flags

- A new app agent calls `ProjectResponsesClient.CreateResponseAsync` directly.
- A new app agent calls `AIProjectClient.AsAIAgent(model, instructions, ...)` and assumes a Foundry agent was created.
- Config is read directly with `Environment.GetEnvironmentVariable` outside `FoundryAgentSettings`.
- Existing server-side agents are overwritten automatically during normal report generation.
- Runtime prompts include conflicting instructions that fight the server-side agent definition.
