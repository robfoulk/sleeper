---
name: week-end
description: 'Close out a fantasy football week with verified NFL results, Sleeper scores, injury updates, weekly roster snapshots, and transaction audits. Use for week-end, end-of-week, weekly closeout, post-Monday updates, or preparing evidence for wrapups.'
argument-hint: 'Season, completed week, and optional league ID; specify separately whether to write or publish a wrapup'
---

# Week-End Closeout

Gather and validate the evidence before writing a wrapup. Data refresh is the
default scope; writing stories, changing site content, committing, and publishing
each require user authorization. Do not rewrite previous predictions or summaries.

## Establish Scope

1. Read the repository instructions, `docs/reporting-cli.md`, and
   `docs/injury-importing.md` from the workspace root.
2. Resolve season, completed NFL week, and Sleeper league ID from the request and
   current league information. The 2026 league ID is `1312539280601522176`;
   validate its season before use. Do not default to 2025 because older files exist.
3. Verify the NFL schedule and that all games in the requested week are final.
   Otherwise label the run interim, not a completed-week closeout.
4. Record retrieval time and inspect `git status --short`. Preserve user changes.
   Before any exporting command, record hashes of existing files under `recaps/`
   and `site/` so unchanged published material can be verified afterward.

## Refresh Injuries First

1. Use the injury MCP tools from the running local host. Check `/health`; follow
   `scripts/start-host.ps1` if startup is needed. If using HTTP `/mcp`, initialize
   a fresh MCP session instead of relying on a previous shell helper or session.
2. Run `import_sleeper_injuries` with `commit=false`. Review candidate count,
   observation timestamp, and errors before repeating with `commit=true`.
   Read back current statuses to verify the write. Report imported observations
   separately from material changes; a full snapshot is not hundreds of new injuries.
3. Research material changes and unresolved concerns using dated official team
   reports and reliable reporting. Include injuries from the week's final games.
   Append evidence with `record_injury_observation`; never edit SQLite directly.
4. Preserve source URL, source authority, body part, practice participation, notes,
   and verified publication/effective timestamps. Never guess a time zone or exact
   onset timestamp. A fresh snapshot does not establish when an injury occurred.
   Use short expiry windows for evolving reports and verify each recorded status.
5. Distinguish prior-game inactivity from next-week availability, planned IR from
   completed transactions, planned practice from participation, and optimistic
   reports from medical clearance. Preserve conflicting sources explicitly.
6. Call `get_weekly_injury_report` with season, week, league ID, and verified
   inclusive-start/exclusive-end timestamps with offsets (maximum 31 days).
   Include the postgame reporting cutoff when needed and state it clearly.
   The report is retrospective, not a strict as-of snapshot. Use requested-week
   roster ownership, not current ownership, when describing weekly impact.
   Report missing coverage and uncertain onset. Do not substitute historical
   nflverse imports for current-season evidence.

## Retrieve Results

1. Run `get_matchup_scoreboard` for the requested week and league.
2. Retrieve the NFL results from a verified source such as
   `https://www.nfl.com/schedules/{season}/reg{week}/`. Confirm every game's
   status; do not infer finality from scores alone.
3. Keep NFL scores and Sleeper fantasy points separate. Sleeper totals can still
   change through stat corrections. Record sources and the retrieval cutoff.
   Scoreboard reads alone do not create local weekly snapshot files.

## Export Snapshots and Asset History

Run from the repository root in PowerShell 7. Set the validated season and league
ID first, inventory existing weekly files, then run each command separately and
check `$LASTEXITCODE` immediately. Validate roster coverage and clean up newly
created future placeholders before running `asset-history`.

```powershell
$season = 2026
$leagueId = '1312539280601522176'
dotnet run --project ./src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- rosters-history --season $season --league-id $leagueId
if ($LASTEXITCODE -ne 0) { throw 'Roster history export failed.' }
```

Inspect emitted weeks against the verified schedule now, before the audit:

```powershell
dotnet run --project ./src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- asset-history --season $season --league-id $leagueId
if ($LASTEXITCODE -ne 0) { throw 'Asset history export failed.' }
```

- `rosters-history` writes `datafiles/{season}/week-NN.json` with weekly matchup
  rosters, starters, bench, and player points. It walks available matchup data,
  not a verified completed-week boundary, and rewrites existing weekly exports.
  Inspect the emitted weeks; future rosters or partial scores are not final data.
   If future placeholders were newly created by this run, explain and remove only
   those new files before auditing. Verify future dates against the NFL schedule;
   zero points alone do not prove a week is unfinished. Never delete pre-existing
   files without approval. If pre-existing unfinished weeks block a valid audit,
   ask how to handle them and do not present inflated start counts as real results.
   Do not alter the exporter as part of routine data refresh.
- `asset-history` must follow roster export. It writes `transactions.json`,
  `asset-movement-audit.json`, and `asset-movement-summary.txt` in the same directory.
  It fetches transaction buckets 1-18; empty future buckets are not completed weeks.
  Distinguish transaction coverage from the roster weeks available for the audit.
- Exports use current player labels and team-name metadata; do not describe those
  labels as historically verified names. Use historical name evidence when needed.
- These files capture Sleeper data, not an NFL game-score archive. Do not imply
  fetched NFL results were persisted unless a separate artifact was actually made.

## Validate and Report

1. Parse generated JSON with `ConvertFrom-Json`. Check season, league ID, distinct
   roster IDs, team count, expected completed weeks, and populated roster arrays.
2. Compare the requested week's team totals against the fresh Sleeper scoreboard.
   Investigate mismatches rather than patching scores by hand.
3. Confirm all three asset-history outputs exist and the audit covers the intended
   roster weeks. Do not interpret empty transaction results as proof without a
   successful fetch. Summarize errors or partial coverage.
4. Verify the saved hashes for existing `recaps/` and `site/` files and inspect the
   changed-file list. A data-only run must not modify previous stories or site data.
5. Summarize exported paths, covered weeks, injury snapshot and sourced-update
   counts, NFL finality, score checks, and unresolved limitations. Stop here unless
   the user separately requested writing or publication.

## When Writing Is Authorized

Read league lore and original predictions before composing a separate dated
wrapup. Keep the original calls intact and compare them fairly with final results.
Use approved first-name owner mappings; do not expose private names or handles.
In this league, "Crazy" in a team name may evoke old "Crazy Davey is giving away
the store; no deal is too low" commercials. Treat that as a suspected
wheeling-and-dealing mindset, not a confirmed motive or evidence of actual trades.
It is background writing context, not an explanation to add to the site.