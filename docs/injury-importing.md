# Injury importing

The shared injury ledger can be populated by the deterministic `nflverse` importer or by an agent using the MCP tools. Imported observations are append-only and are keyed to Sleeper IDs through the existing DynastyProcess GSIS crosswalk.

## Deterministic nflverse import

The importer reads:

`https://github.com/nflverse/nflverse-data/releases/download/injuries/injuries_{season}.csv`

Rows are mapped by `gsis_id`, filtered to the persisted consumer-defined cohort, sorted by week, GSIS ID, team, and status, then recorded with source URL and season/week provenance. nflverse rows are always historical and cannot materialize current state. Re-running the same source is safe because the injury store deduplicates observations.

Agents should call `ImportNflverseInjuries` with `commit=false` first. The result reports source, mapped, imported, and unmapped counts. Call it again with `commit=true` only after reviewing the preview.

The nflverse file is season-scoped and may not exist yet for the current season during the offseason (for example, `injuries_2026.csv` can legitimately return 404 before nflverse publishes it). Do not substitute the prior season as current injury truth. Use the current Sleeper snapshot and verified team reports for current-season status.

## Current preseason snapshot

During the August 2026 preseason, current status must come from current-season sources:

1. Run `GetCurrentSeasonInjurySourcePlan`.
2. Preview and commit `ImportSleeperInjuries` as the automated baseline.
3. Use `GetTeamInjuryPageCatalog` to find official pages, then have an agent verify the page and record material updates with `RecordInjuryObservation`.
4. Use established reporters and fantasy news sites for breaking context only when the direct report and publication time are preserved.

The importing consumer must first persist the player cohort it wants tracked. `ImportSleeperInjuries` reads the current Sleeper NFL player endpoint and writes an explicit current observation for every cohort player. Players without a designation receive a source-specific `healthy` observation; this clears an older Sleeper designation without deleting history or clearing a newer report from another source. Sleeper observations expire after 26 hours.

Current state is selected from unexpired source-specific observations by effective time, then source authority. Historical observations remain available for summaries and timelines but never participate in this selection.

## Team injury pages

`GetTeamInjuryPageCatalog` returns all 32 teams, their official site, and a deterministic NFL.com candidate injury URL. Candidate URLs are marked `requires_verification=true`; an agent must verify the page and source authority before recording a team report. Team-page scraping is intentionally not automatic because page layouts, publication dates, and injury semantics vary by team.

## Agent workflow

1. Preview the current Sleeper snapshot and commit it after reviewing candidate counts.
2. Preview the nflverse import only when the target-season file exists.
3. Review unmapped GSIS IDs and source-row counts before committing historical data.
4. Use the team catalog to investigate a material discrepancy or a newer official report.
5. Record a corrected observation with its article URL; clear availability with a new healthy observation rather than deleting history.
