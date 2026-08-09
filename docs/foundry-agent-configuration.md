# Foundry Agent Configuration

The roster report uses managed, server-side Foundry agents for AI prose and player research. The app must know which Foundry project to use, which model deployment backs the agents, and what each managed agent is named.

Configuration is resolved in this order:

1. `src/Sleeper.RosterReport/appsettings.json`
2. `src/Sleeper.RosterReport/appsettings.Development.json`
3. .NET user secrets for `Sleeper.RosterReport`
4. Environment variables

Later sources override earlier sources. In practice, keep safe defaults and agent names in `appsettings.json`, put machine-local developer values in user secrets or `appsettings.Development.json`, and use environment variables for one-off test runs or hosted deployment overrides.

## Current Local Project

The current development Foundry project endpoint is:

```text
https://me-6535-resource.services.ai.azure.com/api/projects/me-6535
```

The current test model deployment is:

```text
gpt-4o-mini-1
```

These are not committed into `appsettings.json`; set them locally with user secrets or `appsettings.Development.json`.

## Recommended Local Setup

Use user secrets for the Foundry endpoint and model deployment. This is the preferred local path because it is explicit, repeatable, and does not risk committing machine-specific configuration.

```powershell
dotnet user-secrets set "Foundry:ProjectEndpoint" "https://me-6535-resource.services.ai.azure.com/api/projects/me-6535" --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj
dotnet user-secrets set "Foundry:ModelDeployment" "gpt-4o-mini-1" --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj
dotnet user-secrets set "Foundry:SeasonModelDeployment" "gpt-4o-mini-1" --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj
```

Check what keys are configured without exposing values:

```powershell
dotnet user-secrets list --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj
```

## Local Config File Option

`appsettings.Development.json` is also supported for local development and is ignored by git. Use it when you want a visible local file instead of user secrets.

Create `src/Sleeper.RosterReport/appsettings.Development.json`:

```json
{
  "Foundry": {
    "ProjectEndpoint": "https://me-6535-resource.services.ai.azure.com/api/projects/me-6535",
    "ModelDeployment": "gpt-4o-mini-1",
    "SeasonModelDeployment": "gpt-4o-mini-1",
    "AutoCreateMissingAgents": true
  }
}
```

Do not put shared agent names here unless you are intentionally overriding them for a local experiment.

## Checked-In Defaults

`src/Sleeper.RosterReport/appsettings.json` is the shared default file. It should contain non-secret defaults and the canonical agent names, but no real project endpoint.

```json
{
  "Foundry": {
    "ProjectEndpoint": "",
    "ModelDeployment": "gpt-4o-mini",
    "SeasonModelDeployment": "",
    "AutoCreateMissingAgents": true,
    "Agents": {
      "KeeperSecondOpinion": { "Name": "sleeper-keeper-second-opinion" },
      "TeamDraftOutlook": { "Name": "sleeper-team-draft-outlook" },
      "WeeklyGameAnalyst": { "Name": "sleeper-weekly-game-analyst" },
      "WeeklyLeagueAnalyst": { "Name": "sleeper-weekly-league-analyst" },
      "SeasonAnalyst": { "Name": "sleeper-season-analyst" }
    }
  }
}
```

## Environment Overrides

For temporary command-line runs, set the .NET hierarchical environment variable names:

```powershell
$env:Foundry__ProjectEndpoint = "https://me-6535-resource.services.ai.azure.com/api/projects/me-6535"
$env:Foundry__ModelDeployment = "gpt-4o-mini-1"
$env:Foundry__SeasonModelDeployment = "gpt-4o-mini-1"
```

The app also accepts older aliases for compatibility:

| Preferred key | Backward-compatible aliases |
| --- | --- |
| `Foundry:ProjectEndpoint` | `AZURE_AI_PROJECT_ENDPOINT`, `AZURE_OPENAI_ENDPOINT`, `FOUNDRY_PROJECT_ENDPOINT` |
| `Foundry:ModelDeployment` | `AZURE_OPENAI_DEPLOYMENT_NAME` |
| `Foundry:SeasonModelDeployment` | `AZURE_OPENAI_SEASON_DEPLOYMENT_NAME`, `Foundry:Agents:SeasonAnalyst:ModelDeployment` |

New setup should use the `Foundry:*` keys so the intent is obvious.

## Agent Names

The app currently expects these managed Foundry agent names:

| Role | Config key | Default managed agent name |
| --- | --- | --- |
| Keeper second opinion | `Foundry:Agents:KeeperSecondOpinion:Name` | `sleeper-keeper-second-opinion` |
| Team draft outlook | `Foundry:Agents:TeamDraftOutlook:Name` | `sleeper-team-draft-outlook` |
| Weekly game analyst | `Foundry:Agents:WeeklyGameAnalyst:Name` | `sleeper-weekly-game-analyst` |
| Weekly league analyst | `Foundry:Agents:WeeklyLeagueAnalyst:Name` | `sleeper-weekly-league-analyst` |
| Season analyst | `Foundry:Agents:SeasonAnalyst:Name` | `sleeper-season-analyst` |

Normal runs first call `GetAgentAsync`. If an agent is missing and `Foundry:AutoCreateMissingAgents` is `true`, the app creates the missing managed agent with the configured model deployment and the role instructions in `FoundryAgentInstructions`.

## Quick Agent Test

The full reporting CLI reference lives in [reporting-cli.md](reporting-cli.md).

Run one week from the repo root:

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- recap --week 1 --league-id 1180276953741729792 --season 2025
```

A real agent-backed run should print `Recap Agents online` and should not print `AI recap disabled`. If it prints `AI recap disabled`, the app did not find `Foundry:ProjectEndpoint` in config, user secrets, or environment variables.
