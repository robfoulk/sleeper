param(
    [Parameter(Mandatory)][int]$Season,
    [Parameter(Mandatory)][ValidateRange(1, 17)][int]$Week,
    [string]$LeagueId = '1312539280601522176'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$seasonPath = Join-Path $root "recaps/$Season"
$outputPath = Join-Path $seasonPath ('week-{0:D2}-data.json' -f $Week)
if (Test-Path $outputPath) { throw "Edition already exists: $outputPath. Published snapshots are not overwritten." }

function Get-SleeperJson([string]$Path) {
    return ,((Invoke-WebRequest "https://api.sleeper.app/v1/$Path").Content | ConvertFrom-Json -AsHashtable)
}

$league = Get-SleeperJson "league/$LeagueId"
$state = Get-SleeperJson 'state/nfl'
if ([int]$league.season -ne $Season) { throw 'League season does not match the requested season.' }
if ([int]$state.season -lt $Season -or ([int]$state.season -eq $Season -and [int]$state.week -le $Week)) {
    throw 'The requested week has not closed according to Sleeper NFL state.'
}

$scheduleUrl = 'https://raw.githubusercontent.com/nflverse/nfldata/master/data/games.csv'
$schedule = @((Invoke-WebRequest $scheduleUrl).Content | ConvertFrom-Csv | Where-Object { [int]$_.season -eq $Season -and $_.game_type -eq 'REG' })
$completedGames = @($schedule | Where-Object { [int]$_.week -eq $Week })
if ($completedGames.Count -eq 0 -or @($completedGames | Where-Object { $_.home_score -eq '' -or $_.away_score -eq '' }).Count -gt 0) {
    throw 'NFL schedule does not confirm every game in the requested week is complete.'
}
$nextSchedule = @($schedule | Where-Object { [int]$_.week -eq ($Week + 1) })
if ($nextSchedule.Count -eq 0) { throw 'Next-week NFL schedule is unavailable.' }

$users = Get-SleeperJson "league/$LeagueId/users"
$rosters = Get-SleeperJson "league/$LeagueId/rosters"
$players = Get-SleeperJson 'players/nfl'
$nextMatchups = Get-SleeperJson "league/$LeagueId/matchups/$($Week + 1)"
$export = Get-Content (Join-Path $seasonPath 'export.json') -Raw | ConvertFrom-Json -AsHashtable
$names = Get-Content (Join-Path $seasonPath 'team-name-history.json') -Raw | ConvertFrom-Json -AsHashtable
$ownerMap = @{}
foreach ($team in $export.teams) { $ownerMap[[int]$team.roster_id] = $team.owner_name }
$nflOpponents = @{}
foreach ($game in $nextSchedule) {
    $homeTeam = if ($game.home_team -eq 'LA') { 'LAR' } else { $game.home_team }
    $awayTeam = if ($game.away_team -eq 'LA') { 'LAR' } else { $game.away_team }
    $nflOpponents[$homeTeam] = [ordered]@{ opponent = $awayTeam; home = $true; date = $game.gameday }
    $nflOpponents[$awayTeam] = [ordered]@{ opponent = $homeTeam; home = $false; date = $game.gameday }
}

function Get-PublicPlayer([string]$PlayerId, [decimal]$Points = 0, [bool]$Starter = $false) {
    $player = $players[$PlayerId]
    if (-not $player) { throw "Unresolved player: $PlayerId" }
    $name = if ($player.full_name) { $player.full_name } else { "$($player.first_name) $($player.last_name)".Trim() }
    if (-not $name) { $name = $PlayerId }
    return [ordered]@{
        name = $name
        position = $player.position
        nfl_team = $player.team
        points = $Points
        starter = $Starter
        status = $player.injury_status
        injury = $player.injury_body_part
        injury_notes = $player.injury_notes
        next_game = if ($player.team) { $nflOpponents[$player.team] } else { $null }
    }
}

$teams = @{}
foreach ($roster in $rosters) {
    $rosterId = [int]$roster.roster_id
    if (-not $ownerMap[$rosterId]) { throw "No public owner name for roster $rosterId" }
    $user = @($users | Where-Object { $_.user_id -eq $roster.owner_id })[0]
    $teamName = $user.metadata.team_name
    if ([string]::IsNullOrWhiteSpace($teamName)) { $teamName = "$($ownerMap[$rosterId])'s team" }
    $oldNames = @($names.Snapshots | ForEach-Object { $_.OwnerNames[$roster.owner_id].TeamName } | Where-Object { $_ -and $_.Trim() -ne $teamName.Trim() } | Select-Object -Unique)
    $next = @($nextMatchups | Where-Object { [int]$_.roster_id -eq $rosterId })
    $teams[$rosterId] = [ordered]@{
        franchise_id = $rosterId
        owner_name = $ownerMap[$rosterId]
        team_name = $teamName.Trim()
        previous_names = $oldNames
        wins = 0
        losses = 0
        ties = 0
        points_for = [decimal]0
        points_against = [decimal]0
        roster = @($roster.players | Where-Object { $_ -ne '0' } | ForEach-Object { Get-PublicPlayer $_ 0 ($next.Count -eq 1 -and $_ -in $next[0].starters) })
    }
}

$weeks = @()
for ($weekNumber = 1; $weekNumber -le $Week; $weekNumber++) {
    $matchups = Get-SleeperJson "league/$LeagueId/matchups/$weekNumber"
    if ($matchups.Count -ne $rosters.Count) { throw "Incomplete roster results for week $weekNumber" }
    $games = @()
    foreach ($pair in ($matchups | Group-Object { $_.matchup_id })) {
        if ($pair.Group.Count -ne 2 -or [string]::IsNullOrEmpty($pair.Name)) { throw 'Incomplete matchup pairing.' }
        $sides = @()
        foreach ($matchup in $pair.Group) {
            if ($null -eq $matchup.points) { throw 'Missing final score.' }
            $team = $teams[[int]$matchup.roster_id]
            $points = if ($null -ne $matchup.custom_points) { [decimal]$matchup.custom_points } else { [decimal]$matchup.points }
            $sides += [ordered]@{
                franchise_id = $team.franchise_id
                owner_name = $team.owner_name
                team_name = $team.team_name
                points = $points
                players = @($matchup.players | Where-Object { $_ -ne '0' } | ForEach-Object { Get-PublicPlayer $_ ([decimal]$matchup.players_points[$_]) ($_ -in $matchup.starters) })
            }
        }
        for ($sideIndex = 0; $sideIndex -lt 2; $sideIndex++) {
            $side = $sides[$sideIndex]
            $opponent = $sides[1 - $sideIndex]
            $team = $teams[$side.franchise_id]
            $team.points_for += $side.points
            $team.points_against += $opponent.points
            if ($side.points -gt $opponent.points) { $team.wins++ }
            elseif ($side.points -lt $opponent.points) { $team.losses++ }
            else { $team.ties++ }
        }
        $games += [ordered]@{ home = $sides[0]; away = $sides[1]; margin = [Math]::Abs($sides[0].points - $sides[1].points) }
    }
    $weeks += [ordered]@{ week = $weekNumber; is_playoff = $weekNumber -gt 15; games = $games }
}

$upcoming = @()
foreach ($pair in ($nextMatchups | Group-Object { $_.matchup_id })) {
    if ($pair.Group.Count -ne 2 -or [string]::IsNullOrEmpty($pair.Name)) { throw 'Incomplete upcoming pairing.' }
    $upcoming += [ordered]@{ home_id = [int]$pair.Group[0].roster_id; away_id = [int]$pair.Group[1].roster_id }
}

$transactions = @()
$seen = @{}
foreach ($weekNumber in 1..($Week + 1)) {
    $response = Get-SleeperJson "league/$LeagueId/transactions/$weekNumber"
    foreach ($transaction in $response) {
        if ($transaction.status -ne 'complete' -or $seen.ContainsKey($transaction.transaction_id)) { continue }
        $seen[$transaction.transaction_id] = $true
        $moves = @()
        if ($transaction.adds) {
            foreach ($entry in $transaction.adds.GetEnumerator()) {
                $moves += [ordered]@{ player = (Get-PublicPlayer $entry.Key).name; to = [int]$entry.Value; from = if ($transaction.drops -and $transaction.drops.ContainsKey($entry.Key)) { [int]$transaction.drops[$entry.Key] } else { $null } }
            }
        }
        $dropped = @()
        if ($transaction.drops) {
            foreach ($entry in $transaction.drops.GetEnumerator()) {
                if (-not $transaction.adds -or -not $transaction.adds.ContainsKey($entry.Key)) {
                    $dropped += [ordered]@{ player = (Get-PublicPlayer $entry.Key).name; from = [int]$entry.Value }
                }
            }
        }
        $transactions += [ordered]@{
            type = $transaction.type
            completed_at = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$transaction.status_updated).ToString('o')
            moves = $moves
            drops = $dropped
            picks = @($transaction.draft_picks | ForEach-Object { [ordered]@{ season = $_.season; round = $_.round; original_roster = $_.roster_id; from = $_.previous_owner_id; to = $_.owner_id } })
            faab = @($transaction.waiver_budget | ForEach-Object { [ordered]@{ from = $_.sender; to = $_.receiver; amount = $_.amount } })
            bid = $transaction.settings.waiver_bid
        }
    }
}

$snapshot = [ordered]@{
    season = $Season
    league_id = $LeagueId
    completed_week = $Week
    next_week = $Week + 1
    phase = 'in-season'
    captured_at = [DateTimeOffset]::UtcNow.ToString('o')
    recap_path = 'recaps/{0}/week-{1:D2}.md' -f $Season, $Week
    preview_path = 'recaps/{0}/week-{1:D2}-preview.md' -f $Season, ($Week + 1)
    sources = @("https://api.sleeper.app/v1/league/$LeagueId/matchups/$Week", "https://api.sleeper.app/v1/league/$LeagueId/rosters", "https://api.sleeper.app/v1/league/$LeagueId/users", 'https://api.sleeper.app/v1/players/nfl', $scheduleUrl)
    notes = @('Final scores reflect this snapshot and remain subject to platform corrections.', 'Team names were observed at publication, not reconstructed at kickoff. Prior names are retained separately.', 'Player NFL teams and availability are current snapshot metadata, not historical injury onset evidence.', 'Upcoming starters are provisional. Predictions are editorial entertainment, not calibrated probabilities.')
    roster_positions = $league.roster_positions
    scoring_settings = $league.scoring_settings
    teams = @($teams.Values | Sort-Object { $_.franchise_id })
    weeks = $weeks
    upcoming = $upcoming
    transactions = @($transactions | Sort-Object { [DateTimeOffset]$_.completed_at } -Descending)
}
$snapshot | ConvertTo-Json -Depth 20 | Set-Content $outputPath -Encoding utf8
Write-Output "Wrote $outputPath"