# Reporting CLI

`Sleeper.RosterReport` is the local reporting application for player and roster analysis, matchup scoreboards, weekly recaps, season recaps, and roster-history snapshots.

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

## Layered League Lore

Recap commands merge lore from general to specific. Missing files are ignored:

```text
docs/lore/league.md            # league identity and rules
docs/lore/owners.md            # stable owner personas (first names only)
docs/lore/history.md           # championships, records, and running jokes
docs/lore/seasons/{season}.md  # season membership, names, and narratives
docs/lore/weeks/{season}-{week}.md
```

Later YAML frontmatter overrides earlier structured facts. Owner aliases and
notes accumulate; a relationship with the same `type` replaces the earlier
relationship while keeping its priority position. Markdown prose from every
applicable layer is included in the agent prompt with source markers. Weekly
layers apply only to weekly recaps; season recaps stop at the season layer.

## Commands

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

Run a full roster deep dive with draft outlook.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- team --username rob
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

### `injuries`

Build a league-wide weekly injury report from the shared SQLite ledger, filtered to the requested week's matchup rosters and starter/bench roles. This is deterministic and does not require Foundry.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- injuries --week 1 --season 2026 --start 2026-09-09T00:00:00-04:00 --end 2026-09-16T00:00:00-04:00 --format markdown
```

| Option | Required | Description |
| --- | --- | --- |
| `--week`, `-w` | Yes | Week 1-18. |
| `--season`, `-s` | Yes | Season year; must match the league ID's season. |
| `--start` | Yes | Inclusive effective-time window start, ISO timestamp with `Z` or explicit offset. |
| `--end` | Yes | Exclusive end; positive window no longer than 31 days. |
| `--league-id`, `-l` | No | That season's league ID. Defaults to the current league. |
| `--format` | No | `markdown` (default) or `json`. |

Output: console only; no report files or observations are written. For machine-readable JSON without build output, run the compiled app with `--format json`. Dates are caller-supplied: verify the actual NFL reporting window, including Monday night, rather than assuming a calendar week. Missing weekly roster data causes an error; current rosters are never substituted.

Categories: confirmed new injuries, existing injury updates, recoveries, non-injury absences, and uncertain timing. Every entry retains source evidence and the previous same-source observation. Status changes and fresh imports do not prove onset. Coverage is limited to the ledger; no automatic web research or refresh is performed. See [injury-importing.md](injury-importing.md#weekly-league-report-cli-and-mcp) for evidence recording and the equivalent MCP tool.

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
| `--injury-start` | No | Inclusive injury evidence window start, with explicit time zone. Requires `--injury-end`. |
| `--injury-end` | No | Exclusive injury evidence window end, with explicit time zone. Maximum 31-day window. |

To include the same weekly injury report in the recap envelope, league commentary, and forecast context:

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- recap --week 1 --season 2026 --injury-start 2026-09-09T00:00:00-04:00 --injury-end 2026-09-16T00:00:00-04:00
```

The injury subsection belongs within Around the League. The data-only fallback also includes the full evidence report. Existing invocations without injury options remain valid and do not query the injury ledger. If an explicitly requested injury report cannot be built, the recap fails instead of silently omitting it. Historical reports require that season's league ID.

Output:

```text
recaps/{season}/week-NN.md
```

AI behavior: uses the signed-in GitHub Copilot CLI session. The CLI's live model list is authoritative: when a configured role model is unavailable, the app selects another configured model that is live in the same Copilot session, then falls back to the first live Copilot model. It does not switch recap generation to Foundry. If Copilot is unavailable or reports no models, the app writes a data-only markdown envelope dump for debugging. Copilot uses the resolved game writer for matchup sections, the resolved league writer for league-wide synthesis, and the resolved proofreader for factual checks. Deterministic drama profiles calibrate each matchup and the week from Routine through Defining so excitement follows verified stakes, margins, lineup swings, and scoring rather than free-form hype. Forecast output includes one capsule per matchup. When a proofreader is configured, a final synthesis pass may revise only narrative fragments; deterministic tables remain unchanged, and the revision is accepted only after a second factual check passes.

The internal editorial progression is configured per edition in
`datafiles/narrative-state.json`. Missing historical entries resolve to level `0`.
Before generating a new edition, add exactly one season/week entry. A level may
hold steady or increase by one; ranges outside `0` through `10`, regressions,
jumps, duplicate entries, and disagreements with filed snapshots fail generation.
The progression changes framing only. Factual, privacy, injury-evidence, and
prediction controls remain authoritative.

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

### `copilot-replay`

Replay historical weekly recaps with GitHub Copilot and compare them blindly
against the existing recap files. This experimental command never writes into
`recaps/{season}`.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- copilot-replay
```

Defaults target the 2025 league (`1180276953741729792`) and Weeks 1–3. Options:

| Option | Required | Description |
| --- | --- | --- |
| `--season`, `-s` | No | Historical season. Default: `2025`. |
| `--start-week` | No | First replay week. Default: `1`. |
| `--end-week` | No | Last replay week. Default: `3`. |
| `--league-id`, `-l` | No | Historical Sleeper league ID. |
| `--run-id` | No | Immutable run directory name; defaults to a UTC timestamp. |

Output:

```text
recap-runs/{season}/{run-id}/
```

Each run contains the exact input envelopes, Copilot recaps, blind evaluations,
run manifest, and an `assistant.usage` ledger with tokens, duration, model
multiplier cost, and nano-AIU reported by the SDK. Blind evaluations score factual
accuracy, matchup coverage, weekly narrative, earned excitement, specificity,
humor, structural variety, forecast usefulness, house style, repetition, and the
overall result. AI units are telemetry rather than a dollar invoice or guaranteed
premium-request count.

The command uses the logged-in GitHub Copilot user. Models, reasoning effort,
timeout, and game-story concurrency are configured under the `Copilot` section in
`appsettings.json`. The current quality-oriented lineup is:

| Role | Configuration key | Default model | Purpose |
| --- | --- | --- | --- |
| Game writer | `Copilot:WriterModel` | `claude-sonnet-5.5` | Writes the four structured matchup sections with strong quality and lower cost. |
| League and season writer | `Copilot:LeagueWriterModel` | `claude-opus-5.5` | Uses the premium model where synthesis, voice, and editorial judgment matter most. |
| Proofreader | `Copilot:ProofreaderModel` | `gpt-5-mini` | Checks generated prose against structured facts. |
| Evaluator | `Copilot:EvaluatorModel` | `gpt-5.6-sol` | Blindly compares replay candidates using a different model family. |

Configured model names are preferences rather than availability guarantees. Each role
is resolved against the signed-in CLI session's live model list. The evaluator must
differ from both resolved writer models for blind replay comparisons.
Normal recap and season commands do not require the evaluator model to be
available because they never call it.

For a one-off comparison, override a role without editing committed configuration:

```powershell
$env:Copilot__LeagueWriterModel = 'claude-sonnet-5.5'
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- copilot-replay --run-id sonnet-only
Remove-Item Env:\Copilot__LeagueWriterModel
```

The same double-underscore convention works for `WriterModel`, `ProofreaderModel`,
and `EvaluatorModel`. GitHub's Copilot model roster changes frequently. Each run
resolves its configured preferences against the SDK's live model list and selects
another live Copilot model when a preference has been retired.

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
### `site-data`

Merge every season's sidecars into the single file the published site renders from.

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- site-data
```

Takes no options. It reads `recaps/{season}/season-aggregate.json`, `season-awards.json`,
the latest `week-NN-data.json`, and the recap markdown in `recaps/**`, pulls completed-season
per-week scores from Sleeper's public read API, and writes:

```text
site/src/data/league.json
```

This is the privacy boundary. Sleeper usernames are internal keys and are stripped here;
owners reach the site as first names only. Standings, scores, records, and the article index
are all rendered from this file — never from parsed prose. During an active season, the record
book and owner career totals include the latest edition's completed weeks. Championships and
final places remain completed-season facts. If an aggregate exists for the edition's season,
`site-data` uses that aggregate instead of counting the edition twice.

When a current weekly edition exists, `site-data` also builds `first_quarter` from nflverse
regular-season player stat lines for Weeks 1–4. It scores every QB, RB, WR, TE, and K with
the current edition's captured league scoring settings and merges Sleeper's league-scored
team-defense totals from the four weekly snapshots. It then publishes total and weekly points,
positional ranks, consistency, peak, and trend measures. The same snapshots produce fantasy-team
rankings from official weekly totals and aggregate starting-lineup points at QB, RB, WR, TE, K,
and DEF. It also calculates 12 quarter marks across player dominance, position-room strength,
optimal-lineup efficiency, bench scoring, actual wins, all-play expected wins, and matchup margins.
The report updates on every `site-data` run and powers `/first-quarter`; player ranks do not use
nflverse's default fantasy-point columns.

The same scoring engine also exports `second_quarter` for Weeks 5–8 once Week 5 is complete,
with a complete four-week report after Week 8. It powers `/second-quarter` and adds a Q2
navigation link without replacing the Q1 page. Q2 team, defense, and lineup analysis uses the
latest weekly edition's captured historical player rosters and official totals, including
score corrections; it never rewrites older roster snapshots. Missing completed-week NFL
stats or captured matchups fail the build rather than producing a misleading complete report.
Weekly arrays contain four entries starting at Week 5, and averages divide by completed
weeks within that window, not the season week number.

The Q2 page first presents its own weekly scoring, player and team-position rankings, and
12 player/team/lineup/luck findings. A separate comparison section uses the original Q1
report as the baseline for team scoring changes, quarter-only records, scoring-rank
movement, and individual position leaders. Neither leaderboard is cumulative across eight
weeks. Position filters are progressive enhancement; all rankings remain available without
JavaScript.

Filed complete reports at `recaps/{season}/quarter-NN-NN-data.json` are reused only when their
season, window, completed status, and weekly labels match. An invalid filing fails explicitly.
The original Weeks 1–4 report is pinned this way so scoring fixes do not rewrite its published
rankings. New calculations use nflverse aggregate fumble totals when supplied, including
return-game fumbles, and otherwise retain the older split-field calculation without double
counting. The Q2 methodology discloses this correction and the as-filed Q1 comparison basis.

It also fails loudly rather than degrading: an award whose owner cannot be resolved, or a
franchise with no current owner in the export, throws with the season and the award named.

## The weekly loop

### Single-command agent closeout

In **VS Code Copilot Chat**, invoke the repository's
[week-end skill](../.github/skills/week-end/SKILL.md):

```text
/week-end 3 season 2026
```

This closes Week 3 and prepares Week 4: injury research, NFL/Sleeper results,
roster snapshots, transaction audit, missing recap and preview articles, dated
homepage data, editorial review, site build, tests, and local browser checks.
It leaves a local preview running without committing or publishing. The skill
tracks explicit completion proof for each stage, resumes missing work, and reports
blocked dependencies instead of treating a command attempt as a completed stage.

```text
/week-end 3 season 2026 publish
/week-end 3 season 2026 data-only
```

`publish` adds a scoped commit, push, and deployment verification after validation.
`data-only` stops after evidence/datafile validation. With no arguments, the agent
resolves the latest fully completed NFL week and current league season. These are
Chat skill arguments, not terminal or reporting CLI flags. Existing filed articles,
predictions, and weekly snapshots are reused unchanged; reruns fill missing work.
External evidence or validation failures block publication. Week 17 requires a
season-closeout outlook; the league has no Week 18 fantasy matchup preview.

Narrative continuity is also a required pre-generation gate. The closeout reviews
the prior filed edition and `docs/being-narrative-notes.md`, then normally holds
the current narrative level. It may advance by one only when the next beat is
earned by filed continuity and verified league events. Advancement is never
automatic, and Level 10 requires a deliberate narrative event. The raw numeric
state and private rationale remain internal.

### Current Weekly Editions

The in-season bulletin uses a dated weekly snapshot separately from completed-season
aggregates. Capture facts after Sleeper has advanced beyond the requested week and the
nflverse schedule has final scores for every NFL game in that week:

```powershell
./scripts/build-weekly-edition.ps1 -Season 2026 -Week 1
```

The script writes `recaps/2026/week-01-data.json` and refuses to overwrite an existing
snapshot. It reads the season's existing `export.json` for first-name owner mappings,
uses weekly matchup lineups for historical points, and captures current rosters,
team names, provisional upcoming starters, full completed trade packages, and Sleeper
availability designations. Transactions retain their actual completion dates; Week 1's
feed can include offseason activity. Previous name snapshots are read, never rewritten.
Platform tags do not establish injury onset. Add dated official reporting to the articles.

File the final recap as `week-01.md` and the separate next-week preview as
`week-02-preview.md` in the same season directory. Predictions are editorial entertainment;
leave previously filed predictions and notes intact. Discuss their outcomes in new articles.
Do not run the recap generator over an existing article unless replacement is explicitly intended.

`site-data` exports the latest snapshot as `current_edition`, recognizes `weekly-preview`
articles, and updates current franchise names without rewriting historical names or completed
season aggregates. The homepage and current season page render that edition. The record book
and owner career tables include its completed results; franchise timelines continue to show
completed seasons, with the active record presented separately.
Publication timestamps come from the snapshot, not the time of the latest site rebuild.
For a late recap with no upcoming preview, the homepage leads with the completed
week's recap and keeps the verified upcoming fixtures separate. Do not backfill
predictions once that week's games are underway.

Before running the script for a missing edition, add its explicit
`sentience_level` entry to `datafiles/narrative-state.json`. The script snapshots
that value under its internal `_narrative` field and refuses invalid progression.
Once filed, the snapshot is authoritative and must not be rewritten to change the
level. `site-data` removes `_narrative` and the raw numeric level from public JSON;
only policy-approved editorial cues can be projected at later levels. Existing
snapshots without this field remain level `0`.

After editorial review, run `site-data`, `npm run build --prefix site`, and the reporting
test project. Review the homepage and both articles on desktop and mobile in both themes.
The scheduled publishing workflow rebuilds existing content; it does not fetch a new
weekly snapshot or write the next edition automatically.

### Manual recap-only workflow

The lower-level recap command remains available for drafting or debugging, but it
does not perform the full weekly closeout described above.

1. **Write the recap.** `recap --week N` produces `recaps/{season}/week-NN.md` with the
   Copilot writer and the `gpt-5-mini` proofreader. Numbers come from the data; the model
   only explains and entertains.
2. **Read it.** Check the scores and standings against the sidecars before you accept it.
   Nothing downstream re-checks the prose.
3. **Do not publish it as a completed week by itself.** Injury evidence, NFL
   finality, roster and transaction exports, the weekly edition, standalone
   preview, site validation, and deployment verification remain separate gates.

`.github/workflows/publish.yml` takes it from there: it regenerates `league.json`, builds the
site, runs the privacy guard against both the markdown and the built `site/dist`, and deploys
to Pages. The guard is a gate, not a report — a recap that reintroduces the surname, a Sleeper
username, or the old league name fails the build and never reaches the site.

The workflow also runs on a Tuesday-morning schedule during the season, and can be started by
hand from the Actions tab.

### One-time setup

Pages has to be turned on once by hand before the first deploy: **Settings → Pages → Source:
GitHub Actions**. The workflow deliberately does not enable it automatically, because doing so
requires a personal access token rather than the built-in `GITHUB_TOKEN`.

Note that this repository is private. Pages sites published from a private repository require a
paid GitHub plan; on a free plan the deploy step will fail until the repository is made public
or the plan is upgraded. The privacy scrub and its guard apply either way — they exist so that
making the repository public is a decision, not an accident.

To preview the site locally before committing:

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- site-data
cd site
npm ci
npm run build
npm run preview
```

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
