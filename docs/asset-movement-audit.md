# Asset Movement Audit

The asset movement audit tracks what happens to every Week 1 roster asset after the opening lineup snapshot. It is evidence for a future franchise-value evaluator and does not alter current-year lineup points in `RosterTalentEvaluatorV2`.

## Command

```powershell
dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- asset-history --season 2025 --league-id 1180276953741729792
```

Run `rosters-history` first for the same season. The command fetches completed Sleeper transactions for Weeks 1-18 and joins them to kickoff-locked roster snapshots.

Outputs:

```text
datafiles/{season}/transactions.json
datafiles/{season}/asset-movement-audit.json
datafiles/{season}/asset-movement-summary.txt
```

## Classification

Each Week 1 asset is classified by its first exit from the original roster:

- `Retained`: still on the original roster in the final matchup week
- `Trade`: a completed Sleeper trade drops the player from the original roster
- `Dropped`: a completed waiver/free-agent transaction drops the player and no later ownership transfer is credited as a direct trade
- `WaiverOrFreeAgentMove`: the player later appears on another roster after a waiver/free-agent exit
- `UnresolvedMove`: ownership changes in snapshots without a matching transaction
- `DisappearedWithoutTransaction`: the player leaves the original roster without a matched transaction or later owner

For every asset the audit records starts for the original roster, starts for another roster, first other owner/week, exit transaction, and recipient. Trade records preserve the complete package received by that sender: player names, future picks, FAAB, and participating roster IDs.

Multi-asset packages are not divided among individual players. Every player sent by one side points to that sender's complete received package. This prevents unsupported claims that one future pick was payment for one specific player.

## Observed Results

Across 2024 and 2025:

| Measure | 2024 | 2025 | Total |
| --- | ---: | ---: | ---: |
| Week 1 assets | 241 | 240 | 481 |
| Traded Week 1 assets | 12 | 12 | 24 |
| Traded assets later starting elsewhere | 11 | 11 | 22 |
| Post-trade starts | 82 | 96 | 178 |
| Waiver/free-agent moves | 40 | 41 | 81 |
| Post-move starts from waiver/free-agent assets | 124 | 89 | 213 |

Twenty-two of the 24 traded assets are attached to sender packages containing future picks. This confirms that treating every departure as zero asset value understates the league's trade economy.

## Evaluation Boundary

The original roster must not receive both the traded player's later points and the compensation received. A future franchise-value model should use:

```text
direct lineup value before trade + compensation value at trade
```

Post-transfer starts are market-validation evidence. They belong to the acquiring roster's lineup results, not the seller's current-year score.

Future picks remain unpriced. A defensible pick curve requires mapping traded picks to their eventual drafted players and subsequent realized lineup contribution. Until that dataset is built, transaction compensation is reported as structured evidence rather than converted through guessed pick weights.
