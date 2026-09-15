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

## Weekly changes for recaps

Use the MCP tool `get_injury_changes` (`InjuryTools.GetInjuryChanges`) to retrieve material changes in an explicit time window. Arguments are `start` (inclusive), `end` (exclusive), and optional `limit` (default 100, maximum 500). Include time-zone offsets; the window must be positive and at most 31 days. Verify the relevant NFL week dates rather than assuming a fixed Monday-to-Sunday calendar.

Example tool arguments for a verified reporting window:

```json
{
	"start": "2026-09-09T00:00:00-04:00",
	"end": "2026-09-16T00:00:00-04:00",
	"limit": 100
}
```

The query compares consecutive observations for each player and source, using effective time (or observed time when absent). It returns changes in status, practice participation, or primary/secondary injury, including recoveries. Repeated unchanged snapshots, initial healthy observations, and historical nflverse imports are excluded. Expired observations remain available as evidence of past changes. Different sources are not treated as consecutive status changes from the same source.

Output includes the cohort player name where available, the complete observation, the previous same-source observation, source URLs, timestamps, total change count, and a truncation flag. Results are newest first; narrow the window or raise the limit if output is truncated. A first recorded concern has a null previous observation. Coverage is limited to observations already in the ledger; absence from the result does not establish health.

For a recap's injury section:

1. Refresh the current snapshot through the preview/commit workflow, then query the verified recap window. A refresh today cannot reconstruct a missing snapshot from an earlier week.
2. Match Sleeper IDs to the recap week's team rosters and starters, not just today's ownership. The tool returns ledger-wide changes, not a league-specific roster report.
3. Separate **newly recorded concerns**, **updates to existing injuries**, and **recoveries**. Distinguish non-injury absences and practice rest from injuries.
4. Call something **injured this week** only when a dated report confirms onset during that week. A new database row or a transition from a month-old healthy snapshot does not prove onset. Show stale or missing baselines as uncertainty.
5. Use `get_injury_status` for today's availability and dated source evidence for the recap's historical context. The change query is not an as-of snapshot and may include reports recorded later but effective within the window. An in-game "out" designation is not a confirmed absence for the following week.
6. Preserve direct report URLs and publication/effective times when recording verified follow-up observations. Do not replace source-specific history or infer clearance from an omitted report.

### Weekly league report (CLI and MCP)

Use `get_weekly_injury_report` for the roster-filtered report, rather than manually assembling `get_injury_changes` results. It shares the same service and Markdown/JSON formats as the `injuries` CLI command:

```json
{
	"league_id": "1312539280601522176",
	"season": 2026,
	"week": 1,
	"start": "2026-09-09T00:00:00-04:00",
	"end": "2026-09-16T00:00:00-04:00",
	"format": "json"
}
```

Both dates are required and the end is exclusive (maximum 31 days). Week must be 1-18 and the league must belong to the requested season. The report uses that week's Sleeper matchup player IDs and starter roles; it fails if those rosters are unavailable rather than using today's ownership. Roster IDs are preserved so recap consumers can map them to league teams. Cohort coverage is reported, and players outside the current tracked cohort may lack evidence.

The report preserves all qualifying source-specific changes, including conflicting or repeated updates to the same injury. Entry counts are not counts of distinct injuries. Classifications are:

- **ConfirmedNewInjury**: explicitly recorded onset in the window, with a dated HTTP(S) source URL, primary injury description, and `official` or `reporter` confidence. Publication must precede the window end.
- **ExistingInjuryUpdate**: evidenced onset predates the window, or the same concern has a recent same-source baseline (within 26 hours). The latter does not establish an onset date.
- **Recovery**: a source clears its earlier concern; not independent medical clearance or cross-source reconciliation.
- **NonInjuryAbsence**: rest, personal, coach's decision, or non-injury status designation.
- **UncertainTiming**: insufficient onset evidence or missing/stale baseline. A month-old healthy snapshot followed by today's out tag belongs here.

Reports are retrospective effective-time queries, not strict as-of snapshots. Late-recorded evidence with effective time inside the window can appear on reruns. No automatic refresh, injury-onset inference, or web research is performed. An empty category means no qualifying ledger evidence, not that no injuries occurred.

`record_injury_observation` accepts optional `injury_occurred_at` and `source_published_at` timestamps. Set onset only when the source explicitly confirms it; do not copy the import time or guess an exact timestamp from an undated article. Onset requires publication time, onset no later than publication, an HTTP(S) source URL, primary injury description, and official/reporter confidence. These fields are persisted, returned in timelines, and treated as material evidence corrections. Sleeper body-part descriptions and notes are retained during import, but platform start dates are not promoted to verified onset.

For the CLI use `injuries --week ... --season ... --start ... --end ... [--format json]`. Add paired `--injury-start` and `--injury-end` to `recap` to attach this report to the envelope and writing prompts. Neither interface infers reporting dates. Full commands and option tables are in [reporting-cli.md](reporting-cli.md#injuries).

## Agent workflow

1. Preview the current Sleeper snapshot and commit it after reviewing candidate counts.
2. Preview the nflverse import only when the target-season file exists.
3. Review unmapped GSIS IDs and source-row counts before committing historical data.
4. Use the team catalog to investigate a material discrepancy or a newer official report.
5. Record a corrected observation with its article URL; clear availability with a new healthy observation rather than deleting history.
