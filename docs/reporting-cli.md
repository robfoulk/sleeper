# Reporting CLI

`Sleeper.RosterReport` is the local reporting application for league keeper analysis, matchup scoreboards, weekly recaps, season recaps, and roster-history snapshots.

Run commands from the repository root:

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- <command> [options]
```

After publishing or building, the same command surface is available through the compiled app:

```powershell
dotnet .\src\Sleeper.RosterReport\bin\Debug\net10.0\Sleeper.RosterReport.dll <command> [options]
```

## Help Contract

Help is part of the supported interface and should be safe for people and agentic systems to call.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- --help
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- help recap
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- recap --help
```

Expected behavior:

- `--help`, `-h`, and `help <command>` exit with code `0`.
- Running with no arguments prints root help and exits with code `1`.
- Invalid commands or options print an error plus relevant help and exit with code `1`.
- Legacy positional forms remain accepted, but named options are the preferred interface for new automation.

## Defaults

Default league ID:

```text
1312539280601522176
```

Use `--league-id <id>` on any report command to override it.

Foundry-backed reports degrade when Foundry is not configured. Setup lives in [foundry-agent-configuration.md](foundry-agent-configuration.md). The deterministic data work still runs where possible.

## Commands

### `keepers`

Analyze one team's keeper values and recommendations.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- keepers --username robfoulk
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--username`, `-u` | Yes | Sleeper username to analyze. |
| `--league-id`, `-l` | No | Sleeper league ID. Defaults to the current league. |

Output: console report only.

AI behavior: uses the keeper second-opinion Foundry agent when configured; otherwise the deterministic keeper analysis still runs.

Legacy form:

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- keepers robfoulk
```

### `board`

Show league-wide keeper candidates by team.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- board
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--league-id`, `-l` | No | Sleeper league ID. Defaults to the current league. |

Output: console report only.

### `player`

Run a player deep dive with multi-year trend and projection data.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- player --name "Justin Jefferson"
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--name`, `-n` | Yes | Player name or partial name. Quote multi-word names in shells. |
| `--league-id`, `-l` | No | Sleeper league ID. Defaults to the current league. |

Output: console report only.

Legacy form is still accepted and now joins multi-token names unless the final token looks like a league ID:

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- player Justin Jefferson
```

### `team`

Run a full roster deep dive with keeper context and draft outlook.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- team --username robfoulk
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--username`, `-u` | Yes | Sleeper username to analyze. |
| `--league-id`, `-l` | No | Sleeper league ID. Defaults to the current league. |

Output: console report only.

AI behavior: uses the team draft-outlook Foundry agent when configured; otherwise deterministic player sections still run.

### `matchup`

Show the weekly matchup scoreboard.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- matchup --week 10
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--week`, `-w` | No | Week number. Defaults to the current NFL week when omitted. |
| `--league-id`, `-l` | No | Sleeper league ID. Defaults to the current league. |

Output: console report only.

### `recap`

Build an AI-authored weekly league recap.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- recap --week 10 --season 2025
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--week`, `-w` | Yes | League week, currently validated as `1` through `17`. |
| `--season`, `-s` | No | Override season year. |
| `--league-id`, `-l` | No | Sleeper league ID. Defaults to the current league. |

Output:

```text
recaps/{season}/week-NN.md
```

AI behavior: uses weekly recap Foundry agents when configured. If Foundry is not configured, the app writes a data-only markdown envelope dump for debugging.

### `season`

Build the season-in-review recap and sidecar artifacts.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- season --season 2025
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--season`, `-s` | No | Override season year. |
| `--league-id`, `-l` | No | Sleeper league ID. Defaults to the current league. |

Output:

```text
recaps/{season}/season.md
recaps/{season}/manifest.json
recaps/{season}/season-aggregate.json
recaps/{season}/season-awards.json
recaps/{season}/season-outcome.json
recaps/{season}/charts/*.svg
```

The positional form `season 2025` now means season `2025` for the default league.

### `rosters-history`

Capture kickoff-locked weekly roster snapshots from matchup data.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- rosters-history --season 2025
```

Options:

| Option | Required | Description |
| --- | --- | --- |
| `--season`, `-s` | Yes | Season year. |
| `--league-id`, `-l` | No | Sleeper league ID. Defaults to the current league. |

Output:

```text
datafiles/{season}/week-NN.json
```

### `asset-history`

Fetch completed weekly transactions and audit movement of Week 1 roster assets.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- asset-history --season 2025 --league-id 1180276953741729792
```

Run `rosters-history` first for the same season.

Output:

```text
datafiles/{season}/transactions.json
datafiles/{season}/asset-movement-audit.json
datafiles/{season}/asset-movement-summary.txt
```

The audit distinguishes retained, traded, dropped, and waiver/free-agent-moved assets; counts starts on original and later rosters; and records complete player/pick/FAAB trade packages without assigning speculative pick values.

## Adding New Reports

New report commands should follow this contract:

1. Add command metadata and typed options in `src/Sleeper.RosterReport/Cli/ReportCli.cs`.
2. Prefer named options for all new arguments. Keep positional support only when preserving an existing command.
3. Add root or command help text that names required inputs, defaults, examples, generated files, and AI behavior.
4. Add parser tests in `tests/Sleeper.RosterReport.Tests/ReportCliTests.cs`.
5. Keep report generation separate from CLI parsing. `Program.cs` should dispatch typed options and the report runner should do the work.
6. Build and run the RosterReport test project before relying on the interface.

## Validation Commands

```powershell
dotnet build src/Sleeper.RosterReport/Sleeper.RosterReport.csproj
dotnet test tests/Sleeper.RosterReport.Tests/Sleeper.RosterReport.Tests.csproj
```
