# Sleeper

Public .NET 10 services and reporting tools for Sleeper fantasy-football data.

## Projects

- `Sleeper.Api` provides the Sleeper HTTP client, caching, nflverse data, scoring, analysis, and injury ledger.
- `Sleeper.McpServer` exposes the shared services as MCP tools over stdio.
- `Sleeper.Host` exposes MCP over HTTP plus REST and Swagger endpoints.
- `Sleeper.RosterReport` produces league reports, weekly recaps, season summaries, and deterministic artifacts.

Draft assistance is maintained separately in the private sibling repository
`sleeper-draftassist`, which consumes `Sleeper.Api`.

## Build and test

```powershell
dotnet build Sleeper.slnx
dotnet test Sleeper.slnx --no-build
```

See [docs/system-architecture.md](docs/system-architecture.md) for component ownership
and [docs/reporting-cli.md](docs/reporting-cli.md) for reporting commands.
