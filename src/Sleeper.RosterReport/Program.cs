using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sleeper.Api;
using Sleeper.Api.Extensions;
using Sleeper.Api.Injuries;
using Sleeper.Api.Models;
using Sleeper.Api.NflData;
using Sleeper.Api.NflData.Analytics;
using Sleeper.Api.NflData.Models;
using Sleeper.Api.NflData.Scoring;
using Sleeper.Api.Services;
using Sleeper.RosterReport;
using Sleeper.RosterReport.AssetHistory;
using Sleeper.RosterReport.Cli;
using Sleeper.RosterReport.Copilot;
using Sleeper.RosterReport.Recap;

const int HistoryYears = 3;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();
var foundrySettings = FoundryAgentSettings.FromConfiguration(configuration);
var copilotSettings = CopilotAgentSettings.FromConfiguration(configuration);

var cliResult = ReportCli.Parse(args);
if (cliResult is ReportCliHelpResult helpResult)
{
    Console.WriteLine(helpResult.HelpText);
    return helpResult.ExitCode;
}

if (cliResult is ReportCliErrorResult errorResult)
{
    Console.Error.WriteLine(errorResult.Message);
    Console.WriteLine();
    Console.WriteLine(errorResult.HelpText);
    return errorResult.ExitCode;
}

var invocation = ((ReportCliInvocationResult)cliResult).Invocation;

// Wire up DI
var services = new ServiceCollection();
services.AddSleeperApi();
services.AddNflData();
var sp = services.BuildServiceProvider();

var client = sp.GetRequiredService<ISleeperClient>();
var sleeperService = sp.GetRequiredService<ISleeperService>();
var nflData = sp.GetRequiredService<INflDataClient>();
var fantasyService = sp.GetRequiredService<IFantasyService>();

// A season/league mismatch is a user error with an actionable message, so surface it
// as a clean CLI failure rather than an unhandled stack trace.
try
{
    return await (invocation.Command switch
    {
        ReportCommand.Keepers => RunKeeperAnalyzer((KeeperCommandOptions)invocation.Options),
        ReportCommand.Board => RunLeagueBoard((BoardCommandOptions)invocation.Options),
        ReportCommand.Player => RunPlayerDeepDive((PlayerCommandOptions)invocation.Options),
        ReportCommand.Team => RunTeamDeepDive((TeamCommandOptions)invocation.Options),
        ReportCommand.Matchup => RunMatchupScoreboard((MatchupCommandOptions)invocation.Options),
        ReportCommand.Injuries => RunWeeklyInjuries((InjuriesCommandOptions)invocation.Options),
        ReportCommand.Recap => RunWeeklyRecap((WeeklyRecapCommandOptions)invocation.Options),
        ReportCommand.CopilotReplay => RunCopilotReplay((CopilotReplayCommandOptions)invocation.Options),
        ReportCommand.CopilotProofread => RunCopilotProofread((CopilotProofreadCommandOptions)invocation.Options),
        ReportCommand.Season => RunSeasonRecap((SeasonRecapCommandOptions)invocation.Options),
        ReportCommand.RostersHistory => RunRostersHistory((RostersHistoryCommandOptions)invocation.Options),
        ReportCommand.Export => RunExport((ExportCommandOptions)invocation.Options),
        ReportCommand.AssetHistory => RunAssetHistory((AssetHistoryCommandOptions)invocation.Options),
        ReportCommand.SiteData => SiteDataBuilder.RunAsync(client),
        _ => throw new InvalidOperationException($"Unsupported report command {invocation.Command}.")
    });
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine();
    return 1;
}

async Task<int> ResolveLoreSeasonAsync(string leagueId, int? overrideSeason)
{
    if (overrideSeason is not null)
        return overrideSeason.Value;

    var league = await client.GetLeagueAsync(leagueId);
    return int.TryParse(league?.Season, out var season)
        ? season
        : DateTime.UtcNow.Year;
}

async Task<int> RunCopilotReplay(CopilotReplayCommandOptions options)
{
    var runner = new CopilotRecapReplayRunner(client, sleeperService, nflData, copilotSettings);
    try
    {
        var runDirectory = await runner.RunAsync(
            options.LeagueId,
            options.Season,
            options.StartWeek,
            options.EndWeek,
            options.RunId);
        Console.WriteLine();
        Console.WriteLine($"Copilot replay completed: {runDirectory}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Copilot replay failed: {ex.Message}");
        return 1;
    }
}

async Task<int> RunCopilotProofread(CopilotProofreadCommandOptions options)
{
    var runner = new CopilotProofreaderRunner(copilotSettings, options.Model);
    try
    {
        await runner.RunAsync(options.Season, options.RunId);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Copilot proofreading failed: {ex.Message}");
        return 1;
    }
}

// ======================================================================
// REPORT 1: KEEPER ANALYZER
// ======================================================================
async Task<int> RunWeeklyInjuries(InjuriesCommandOptions options)
{
    try
    {
        var report = await sp.GetRequiredService<WeeklyInjuryReportService>()
            .BuildAsync(options.LeagueId, options.Season, options.Week, options.Window);
        Console.WriteLine(options.Format == "json" ? report.ToJson() : report.ToMarkdown());
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Injury report failed: {exception.Message}");
        return 1;
    }
}

async Task<int> RunKeeperAnalyzer(KeeperCommandOptions options)
{
    var username = options.Username;
    var leagueId = options.LeagueId;

    Console.WriteLine($"Analyzing keepers for '{username}'...");

    var user = await client.GetUserAsync(username);
    if (user is null) { Console.WriteLine($"User '{username}' not found."); return 1; }

    var league = await client.GetLeagueAsync(leagueId);
    if (league is null) { Console.WriteLine($"League not found."); return 1; }

    var config = LeagueRosterConfig.FromLeague(league);
    var keeperValues = await sleeperService.GetRosterKeeperValuesAsync(leagueId, username);
    if (keeperValues.Count == 0) { Console.WriteLine("No roster found."); return 1; }

    // Build scorer for this league
    var scorer = new FantasyScorer(league.ScoringSettings ?? new());
    var currentSeason = int.TryParse(league.Season, out var s) ? s : DateTime.UtcNow.Year;

    // Fetch multi-year stats -- use last completed season as baseline
    // (league.Season might be future/pre-draft, nflverse only has completed seasons)
    var nflState = await client.GetNflStateAsync();
    var lastCompletedSeason = nflState is not null
        ? int.Parse(nflState.PreviousSeason ?? (nflState.Season ?? currentSeason.ToString()))
        : currentSeason - 1;

    // If we're in the offseason, the "current" season has no data
    if (nflState?.SeasonType == "off") lastCompletedSeason = int.Parse(nflState.PreviousSeason ?? (currentSeason - 1).ToString());

    Console.WriteLine($"Fetching {HistoryYears}-year stats ({lastCompletedSeason - HistoryYears + 1}-{lastCompletedSeason})...");
    var seasonStatsTasks = new Dictionary<int, Task<Dictionary<string, SeasonPlayerStats>>>();
    var weeklyStatsTasks = new Dictionary<int, Task<Dictionary<string, List<WeeklyPlayerStats>>>>();

    for (int y = lastCompletedSeason; y > lastCompletedSeason - HistoryYears; y--)
    {
        seasonStatsTasks[y] = nflData.GetSeasonStatsBySleeperIdAsync(y, "reg");
        weeklyStatsTasks[y] = nflData.GetWeeklyStatsBySleeperIdAsync(y);
    }

    await Task.WhenAll(
        Task.WhenAll(seasonStatsTasks.Values),
        Task.WhenAll(weeklyStatsTasks.Values));

    // Calculate real replacement levels from league-wide scoring data
    Console.WriteLine("Ranking all NFL players by league scoring...");
    var lastSeasonAllStats = await nflData.GetSeasonStatsAsync(lastCompletedSeason, "reg");
    var sleeperToGsis = await nflData.GetSleeperToGsisMapAsync();
    var ranker = new LeagueRanker(scorer);
    var rankings = ranker.RankBySleeperId(lastSeasonAllStats, sleeperToGsis);

    var replacementLevels = rankings.CalculateReplacementLevels(
        config.Teams,
        config.StarterSlots,
        config.FlexSlots,
        config.FlexEligiblePositions);

    var fantasyPositions = new HashSet<string> { "QB", "RB", "WR", "TE", "K" };
    var fantasyReplacements = replacementLevels.Where(r => fantasyPositions.Contains(r.Key));
    Console.WriteLine($"Replacement levels ({lastCompletedSeason} actual): {string.Join(", ", fantasyReplacements.Select(r => $"{r.Key}={r.Value:F1}"))}");

    var gamesNextSeason = DurabilityCalculator.GamesInSeason(currentSeason);

    // Analyze each player
    var analyzer = new KeeperAnalyzer(replacementLevels);
    var analyses = new List<PlayerAnalysis>();

    foreach (var kv in keeperValues)
    {
        var p = kv.Player;
        if (p.Position is "DEF") continue; // skip defenses for now

        var seasonHistory = new List<SeasonSummary>();
        var recentWeekly = new List<decimal>();

        for (int y = lastCompletedSeason; y > lastCompletedSeason - HistoryYears; y--)
        {
            var seasonStats = seasonStatsTasks[y].Result;
            var weeklyStats = weeklyStatsTasks[y].Result;

            if (seasonStats.TryGetValue(p.PlayerId, out var ss))
            {
                var scored = scorer.ScoreSeason(ss);
                var games = ss.Games ?? 0;
                var ppg = games > 0 ? Math.Round(scored.TotalPoints / games, 2) : 0m;

                // Get weekly points for stddev
                var weeklyPts = new List<decimal>();
                if (weeklyStats.TryGetValue(p.PlayerId, out var weeks))
                {
                    weeklyPts = weeks
                        .Where(w => w.SeasonType == "REG")
                        .Select(w => scorer.ScoreWeekly(w).TotalPoints)
                        .ToList();

                    if (y == lastCompletedSeason)
                        recentWeekly = weeklyPts;
                }

                var stdDev = weeklyPts.Count >= 2 ? ConsistencyCalculator.CalculateStdDev(weeklyPts) : 0m;
                seasonHistory.Add(new SeasonSummary(y, games, scored.TotalPoints, ppg, Math.Round(stdDev, 2)));
            }
        }

        var analysis = analyzer.Analyze(
            p.PlayerId, p.FullName, p.Position, p.Age,
            kv.KeeperCostRound, kv.CanBeKept,
            seasonHistory, recentWeekly, gamesNextSeason);

        analyses.Add(analysis);
    }

    // Sort by keeper score descending
    analyses = analyses.OrderByDescending(x => x.KeeperScore).ToList();

    // Print report
    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    Console.WriteLine($"  KEEPER ANALYSIS -- {league.Name} ({league.Season})");
    Console.WriteLine($"  Owner: {user.DisplayName ?? username}");
    Console.WriteLine($"  League: {config.Teams} teams, {config.MaxKeepers} keepers allowed");
    Console.WriteLine("===================================================================================");

    // Full roster table
    Console.WriteLine();
    Console.WriteLine($"  {"Player",-24} {"Pos",-4} {"Rank",-6} {"Age",-4} {"Rd",-4} {"Pick#",-6} {"wPPG",-7} {"Proj",-7} {"VORP",-7} {"Trend",-12} {"Dur%",-6} {"Grade",-6} {"Score",-7}");
    Console.WriteLine($"  {"------",-24} {"---",-4} {"----",-6} {"---",-4} {"--",-4} {"-----",-6} {"----",-7} {"----",-7} {"----",-7} {"-----",-12} {"----",-6} {"-----",-6} {"-----",-7}");

    foreach (var pa in analyses)
    {
        var rd = pa.KeeperCostRound.HasValue ? $"R{pa.KeeperCostRound}" : "--";
        var age = pa.Age?.ToString() ?? "?";
        var grade = pa.CanBeKept ? pa.KeeperGrade : "N/A";
        var score = pa.CanBeKept ? $"{pa.KeeperScore:F1}" : "--";
        var rank = rankings.GetRankLabel(pa.SleeperId) ?? "--";
        var pickEst = pa.KeeperCostRound.HasValue ? $"~{(pa.KeeperCostRound.Value - 1) * config.Teams + config.Teams / 2}" : "--";

        Console.WriteLine($"  {pa.PlayerName,-24} {pa.Position,-4} {rank,-6} {age,-4} {rd,-4} {pickEst,-6} {pa.WeightedPpg,-7:F1} {pa.ProjectedSeasonPoints,-7:F0} {pa.Vorp,-7:F1} {pa.TrendDirection,-12} {pa.DurabilityPct,-6:F0} {grade,-6} {score,-7}");
    }

    // Top 10 keeper candidates -- positional diversity aware, excludes kickers
    // Kickers have near-zero strategic value as keepers (volatile, easily replaced in draft)
    const int TopCandidates = 10;
    var recommended = new List<PlayerAnalysis>();
    var positionCounts = new Dictionary<string, int>();
    var candidates = analyses.Where(a => a.CanBeKept && a.KeeperScore > 0 && a.Position != "K").ToList();

    foreach (var candidate in candidates)
    {
        if (recommended.Count >= TopCandidates) break;

        var pos = candidate.Position?.ToUpperInvariant() ?? "";
        var currentCount = positionCounts.GetValueOrDefault(pos);
        var starterSlots = config.StarterSlots.GetValueOrDefault(pos, 1);

        // Cap keepers at the number of direct starter slots for that position
        // (two starting QB slots = allow 2 QB keepers, four starting RB slots = allow 4 RB keepers)
        // But never let one position consume more than maxKeepers - 1
        // so there's always room for at least one other position
        var keeperCap = Math.Min(starterSlots, config.MaxKeepers - 1);
        keeperCap = Math.Max(1, keeperCap);

        if (currentCount < keeperCap)
        {
            recommended.Add(candidate);
            positionCounts[pos] = currentCount + 1;
        }
    }

    Console.WriteLine();
    Console.WriteLine("-----------------------------------------------------------------------------------");
    Console.WriteLine($"  TOP {TopCandidates} KEEPER CANDIDATES (you keep {config.MaxKeepers} -- your call)");
    Console.WriteLine("-----------------------------------------------------------------------------------");

    var secondOpinionAgent = await KeeperSecondOpinionAgent.TryCreateAsync(foundrySettings, currentSeason);
    if (secondOpinionAgent is null)
    {
        Console.WriteLine();
        Console.WriteLine($"  (AI second opinion disabled -- {foundrySettings.MissingConfigurationMessage})");
    }

    for (int i = 0; i < recommended.Count; i++)
    {
        var r = recommended[i];
        var rankLabel = rankings.GetRankLabel(r.SleeperId) ?? "N/R";
        Console.WriteLine();
        var keeperPickEst = (r.KeeperCostRound!.Value - 1) * config.Teams + config.Teams / 2;
        Console.WriteLine($"  {i + 1}. {r.PlayerName} ({r.Position}, age {r.Age}) -- {rankLabel} in {lastCompletedSeason}");
        Console.WriteLine($"     Keeper Cost: Round {r.KeeperCostRound} (~Pick {keeperPickEst})  |  Grade: {r.KeeperGrade}  |  Score: {r.KeeperScore:F1}");
        Console.WriteLine($"     Projected: {r.ProjectedSeasonPoints:F0} pts ({r.AgeAdjustedPpg:F1} PPG)  |  VORP: {r.Vorp:F1}  |  Surplus: {r.KeeperSurplus:F1}");
        Console.WriteLine($"     Trend: {r.TrendDirection} ({r.TrendPerYear:+0.0;-0.0}/yr)  |  Durability: {r.DurabilityPct:F0}%  |  Consistency: {r.ConsistencyScore:F0}/100");

        if (r.SeasonHistory.Count > 0)
        {
            Console.Write("     History: ");
            Console.WriteLine(string.Join(" | ", r.SeasonHistory.OrderBy(s => s.Season)
                .Select(s => $"{s.Season}: {s.Ppg:F1} PPG ({s.GamesPlayed}g)")));
        }

        // Reasoning — lead with distinctive qualities, surplus last (it's always present)
        var reasons = new List<string>();

        // Lead with positional rank — the most concrete fact
        if (rankLabel != "N/R")
        {
            var posRank = rankings.BySleeperId.GetValueOrDefault(r.SleeperId)?.PositionalRank ?? 0;
            if (posRank <= 10)
                reasons.Add($"Elite talent -- ranked {rankLabel} league-wide last season");
            else if (posRank <= 20)
                reasons.Add($"Starter-caliber -- ranked {rankLabel} league-wide");
        }

        // Trend and trajectory
        if (r.TrendPerYear > 1) reasons.Add($"Ascending trajectory -- gaining {r.TrendPerYear:F1} PPG/year");
        if (r.Vorp > 5) reasons.Add($"Elite positional scarcity -- {r.Vorp:F1} pts above replacement {r.Position}");
        if (r.ConsistencyScore > 70) reasons.Add($"Reliable weekly scorer -- {r.ConsistencyScore:F0}/100 consistency");
        if (r.DurabilityPct >= 90) reasons.Add("Iron man -- plays every game");

        // Surplus as supporting context (not the headline)
        if (r.KeeperSurplus > 5) reasons.Add($"Massive cost value -- projects {r.AgeAdjustedPpg:F1} PPG at Rd {r.KeeperCostRound} (~Pick {keeperPickEst})");
        else if (r.KeeperSurplus > 2) reasons.Add($"Good value -- worth more than Rd {r.KeeperCostRound} (~Pick {keeperPickEst})");

        // Warnings last
        if (r.Age.HasValue && r.Position == "RB" && r.Age >= 28) reasons.Add("WARNING: RB age cliff approaching");
        if (r.DurabilityPct < 70) reasons.Add($"RISK: Injury-prone -- only {r.DurabilityPct:F0}% games played");
        if (r.TrendPerYear < -1) reasons.Add($"CONCERN: Declining -- losing {Math.Abs(r.TrendPerYear):F1} PPG/year");
        if (r.SeasonHistory.Count == 1) reasons.Add("NOTE: Only 1 season of data -- projection has high uncertainty");

        if (reasons.Count > 0)
        {
            Console.WriteLine($"     Why: {reasons[0]}");
            foreach (var reason in reasons.Skip(1))
                Console.WriteLine($"          {reason}");
        }

        // AI second opinion with recent news (web search)
        if (secondOpinionAgent is not null)
        {
            try
            {
                // Small pacing delay between candidates to avoid rate-limit bursts
                if (i > 0) await Task.Delay(TimeSpan.FromSeconds(3));

                Console.Write("     AI Second Opinion: (searching recent news...)");
                var verdict = await secondOpinionAgent.GetSecondOpinionAsync(r, rankLabel, lastCompletedSeason, config.Teams);
                // Clear the "searching..." line
                Console.Write("\r     AI Second Opinion:                              \n");
                foreach (var line in verdict.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Console.WriteLine($"       {line.Trim()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"     AI Second Opinion: (unavailable -- {ex.Message})");
            }
        }
    }

    // Don't-keep list
    var dontKeep = analyses
        .Where(a => a.CanBeKept && a.KeeperScore <= 0)
        .OrderBy(a => a.KeeperScore)
        .ToList();

    if (dontKeep.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("-----------------------------------------------------------------------------------");
        Console.WriteLine("  NOT RECOMMENDED (negative value -- draft position more valuable)");
        Console.WriteLine("-----------------------------------------------------------------------------------");
        foreach (var d in dontKeep.Take(5))
        {
            var dPickEst = d.KeeperCostRound.HasValue ? (d.KeeperCostRound.Value - 1) * config.Teams + config.Teams / 2 : 0;
            Console.WriteLine($"    {d.PlayerName,-24} {d.Position,-4} Rd {d.KeeperCostRound,-3} (~Pick {dPickEst,-3}) -> Grade: {d.KeeperGrade}  Score: {d.KeeperScore:F1}  ({d.TrendDirection})");
        }
    }

    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    return 0;
}

// ======================================================================
// REPORT 2: LEAGUE KEEPER BOARD
// ======================================================================
async Task<int> RunLeagueBoard(BoardCommandOptions options)
{
    var leagueId = options.LeagueId;

    Console.WriteLine("Generating league-wide keeper board...");

    var league = await client.GetLeagueAsync(leagueId);
    if (league is null) { Console.WriteLine("League not found."); return 1; }

    var config = LeagueRosterConfig.FromLeague(league);
    var rostersWithOwners = await sleeperService.GetRostersWithOwnersAsync(leagueId);
    var scorer = new FantasyScorer(league.ScoringSettings ?? new());
    var currentSeason = int.TryParse(league.Season, out var s) ? s : DateTime.UtcNow.Year;

    // Fetch last year's stats (most relevant for keeper decisions)
    var lastSeason = currentSeason - 1;
    Console.WriteLine($"Fetching {lastSeason} season stats...");
    var seasonStats = await nflData.GetSeasonStatsBySleeperIdAsync(lastSeason, "reg");

    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    Console.WriteLine($"  LEAGUE KEEPER BOARD -- {league.Name} ({league.Season})");
    Console.WriteLine($"  {config.Teams} teams, {config.MaxKeepers} keepers each = {config.Teams * config.MaxKeepers} players kept");
    Console.WriteLine("===================================================================================");

    var allKeptPlayers = new List<(string Owner, PlayerAnalysis Analysis)>();
    var allUnkeptTalent = new List<(string Owner, KeeperValue Kv, decimal LastSeasonPpg)>();

    foreach (var rwo in rostersWithOwners.OrderBy(r => r.OwnerDisplayName))
    {
        var ownerName = rwo.OwnerDisplayName ?? rwo.OwnerUsername ?? $"Roster {rwo.Roster.RosterId}";

        // Get keeper values for this roster
        // We need to manually compute since GetRosterKeeperValuesAsync takes username
        var keeperValues = await sleeperService.GetRosterKeeperValuesAsync(leagueId,
            rwo.OwnerUsername ?? rwo.OwnerDisplayName ?? "");

        if (keeperValues.Count == 0) continue;

        var teamAnalyses = new List<PlayerAnalysis>();
        foreach (var kv in keeperValues.Where(k => k.CanBeKept && k.Player.Position != "DEF"))
        {
            decimal ppg = 0;
            if (seasonStats.TryGetValue(kv.Player.PlayerId, out var ss))
            {
                var scored = scorer.ScoreSeason(ss);
                var games = ss.Games ?? 1;
                ppg = games > 0 ? Math.Round(scored.TotalPoints / games, 2) : 0;
            }

            var history = ppg > 0
                ? new List<SeasonSummary> { new(lastSeason, 17, ppg * 17, ppg, 0) }
                : new List<SeasonSummary>();

            var analyzer = new KeeperAnalyzer();
            var analysis = analyzer.Analyze(kv.Player.PlayerId, kv.Player.FullName,
                kv.Player.Position, kv.Player.Age, kv.KeeperCostRound, true, history, []);

            teamAnalyses.Add(analysis);
        }

        var topKeepers = teamAnalyses.OrderByDescending(t => t.KeeperScore).Take(config.MaxKeepers).ToList();

        Console.WriteLine();
        Console.WriteLine($"  {ownerName}");
        Console.WriteLine($"  {"  Player",-26} {"Pos",-4} {"Age",-4} {"Rd",-4} {"PPG",-7} {"VORP",-6} {"Grade",-6}");

        foreach (var tk in topKeepers)
        {
            var marker = tk.KeeperGrade is "S" or "A" ? "*" : " ";
            Console.WriteLine($"  {marker} {tk.PlayerName,-24} {tk.Position,-4} {tk.Age?.ToString() ?? "?",-4} R{tk.KeeperCostRound,-3} {tk.WeightedPpg,-7:F1} {tk.Vorp,-6:F1} {tk.KeeperGrade,-6}");
            allKeptPlayers.Add((ownerName, tk));
        }

        // Track unkept talent
        var unkept = teamAnalyses.Except(topKeepers).Where(t => t.WeightedPpg > 5).ToList();
        foreach (var u in unkept)
        {
            var kv = keeperValues.First(k => k.Player.PlayerId == u.SleeperId);
            allUnkeptTalent.Add((ownerName, kv, u.WeightedPpg));
        }
    }

    // Draft pool analysis
    Console.WriteLine();
    Console.WriteLine("-----------------------------------------------------------------------------------");
    Console.WriteLine("  BEST TALENT RETURNING TO DRAFT POOL");
    Console.WriteLine("-----------------------------------------------------------------------------------");
    Console.WriteLine($"  {"Player",-24} {"Pos",-4} {"PPG",-7} {"From",-20}");

    foreach (var u in allUnkeptTalent.OrderByDescending(u => u.LastSeasonPpg).Take(15))
    {
        Console.WriteLine($"  {u.Kv.Player.FullName,-24} {u.Kv.Player.Position,-4} {u.LastSeasonPpg,-7:F1} {u.Owner,-20}");
    }

    // Positional scarcity
    Console.WriteLine();
    Console.WriteLine("-----------------------------------------------------------------------------------");
    Console.WriteLine("  KEEPER IMPACT BY POSITION");
    Console.WriteLine("-----------------------------------------------------------------------------------");

    foreach (var pos in new[] { "QB", "RB", "WR", "TE" })
    {
        var kept = allKeptPlayers.Count(k => k.Analysis.Position == pos);
        var totalStarters = config.GetEffectiveStarters(pos) * config.Teams;
        Console.WriteLine($"  {pos}: {kept} kept / {totalStarters} starter slots ({kept * 100 / Math.Max(1, totalStarters)}% of starters locked up)");
    }

    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    return 0;
}

// ======================================================================
// REPORT 3: PLAYER DEEP DIVE
// ======================================================================
async Task<int> RunPlayerDeepDive(PlayerCommandOptions options)
{
    var playerName = options.Name;
    var leagueId = options.LeagueId;

    Console.WriteLine($"Looking up '{playerName}'...");

    var league = await client.GetLeagueAsync(leagueId);
    if (league is null) { Console.WriteLine("League not found."); return 1; }

    var scorer = new FantasyScorer(league.ScoringSettings ?? new());
    var currentSeason = int.TryParse(league.Season, out var s) ? s : DateTime.UtcNow.Year;

    // Find the player in Sleeper data
    var allPlayers = await client.GetAllPlayersAsync();
    var searchName = playerName.ToLowerInvariant();
    var match = allPlayers.Values.FirstOrDefault(p =>
        p.FullName.ToLowerInvariant().Contains(searchName) ||
        (p.SearchFullName?.Contains(searchName.Replace(" ", "")) ?? false));

    if (match is null) { Console.WriteLine($"Player '{playerName}' not found."); return 1; }

    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    Console.WriteLine($"  PLAYER DEEP DIVE: {match.FullName}");
    Console.WriteLine($"  {match.Position} | {match.Team ?? "FA"} | Age {match.Age} | {match.YearsExp ?? 0} yrs exp");
    Console.WriteLine("===================================================================================");

    // Multi-year analysis -- use last completed seasons
    var nflState2 = await client.GetNflStateAsync();
    var lastCompleted = nflState2 is not null
        ? int.Parse(nflState2.PreviousSeason ?? (currentSeason - 1).ToString())
        : currentSeason - 1;

    var seasonHistory = new List<SeasonSummary>();
    var allWeeklyPoints = new Dictionary<int, List<decimal>>();

    for (int y = lastCompleted; y > lastCompleted - HistoryYears; y--)
    {
        var seasonStats = await nflData.GetSeasonStatsBySleeperIdAsync(y, "reg");
        var weeklyStats = await nflData.GetWeeklyStatsBySleeperIdAsync(y);

        if (seasonStats.TryGetValue(match.PlayerId, out var ss))
        {
            var scored = scorer.ScoreSeason(ss);
            var games = ss.Games ?? 0;
            var ppg = games > 0 ? Math.Round(scored.TotalPoints / games, 2) : 0m;

            var weeklyPts = new List<decimal>();
            if (weeklyStats.TryGetValue(match.PlayerId, out var weeks))
            {
                weeklyPts = weeks.Where(w => w.SeasonType == "REG")
                    .OrderBy(w => w.Week)
                    .Select(w => scorer.ScoreWeekly(w).TotalPoints)
                    .ToList();
                allWeeklyPoints[y] = weeklyPts;
            }

            var stdDev = weeklyPts.Count >= 2 ? ConsistencyCalculator.CalculateStdDev(weeklyPts) : 0m;
            seasonHistory.Add(new SeasonSummary(y, games, scored.TotalPoints, ppg, Math.Round(stdDev, 2)));
        }
    }

    if (seasonHistory.Count == 0) { Console.WriteLine("\n  No scoring data found for this player."); return 0; }

    // Season-by-season table
    Console.WriteLine();
    Console.WriteLine($"  {"Season",-8} {"Games",-7} {"Total",-8} {"PPG",-7} {"StdDev",-8} {"Floor",-7} {"Ceil",-7} {"Boom%",-7} {"Bust%",-7}");
    Console.WriteLine($"  {"------",-8} {"-----",-7} {"-----",-8} {"---",-7} {"------",-8} {"-----",-7} {"----",-7} {"-----",-7} {"-----",-7}");

    var posAvg = VorpCalculator.DefaultReplacementPpg.GetValueOrDefault(match.Position?.ToUpperInvariant() ?? "", 8m);
    foreach (var sh in seasonHistory.OrderBy(s => s.Season))
    {
        var weeklyPts = allWeeklyPoints.GetValueOrDefault(sh.Season, []);
        var floor = ConsistencyCalculator.CalculateFloor(weeklyPts);
        var ceil = ConsistencyCalculator.CalculateCeiling(weeklyPts);
        var boom = ConsistencyCalculator.CalculateBoomRate(weeklyPts, posAvg);
        var bust = ConsistencyCalculator.CalculateBustRate(weeklyPts, posAvg);

        Console.WriteLine($"  {sh.Season,-8} {sh.GamesPlayed,-7} {sh.TotalPoints,-8:F1} {sh.Ppg,-7:F1} {sh.StdDev,-8:F1} {floor,-7:F1} {ceil,-7:F1} {boom,-7:F0} {bust,-7:F0}");
    }

    // Weekly sparkline for most recent season
    var recentYear = seasonHistory.OrderByDescending(s => s.Season).First().Season;
    if (allWeeklyPoints.TryGetValue(recentYear, out var recentWeekly) && recentWeekly.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  {recentYear} Weekly Scoring:");
        var max = recentWeekly.Max();
        var scale = max > 0 ? 30m / max : 1m;
        for (int w = 0; w < recentWeekly.Count; w++)
        {
            var pts = recentWeekly[w];
            var bar = new string('#', (int)(pts * scale));
            Console.WriteLine($"  Wk {w + 1,2}: {pts,6:F1} |{bar}");
        }
    }

    // Projections
    var analyzer = new KeeperAnalyzer();
    var recentWkPts = allWeeklyPoints.GetValueOrDefault(recentYear, []);
    var analysis = analyzer.Analyze(match.PlayerId, match.FullName, match.Position, match.Age,
        null, false, seasonHistory, recentWkPts);

    Console.WriteLine();
    Console.WriteLine("-----------------------------------------------------------------------------------");
    Console.WriteLine("  PROJECTIONS & ANALYSIS");
    Console.WriteLine("-----------------------------------------------------------------------------------");
    Console.WriteLine($"  Weighted PPG:        {analysis.WeightedPpg:F1}");
    Console.WriteLine($"  Age-Adjusted PPG:    {analysis.AgeAdjustedPpg:F1} (age factor: {AgingCurve.GetFactor(match.Position, match.Age):F2})");
    Console.WriteLine($"  Projected Season:    {analysis.ProjectedSeasonPoints:F0} pts");
    Console.WriteLine($"  VORP:                {analysis.Vorp:F1} (vs {analysis.ReplacementPpg:F1} replacement PPG)");
    Console.WriteLine($"  Trend:               {analysis.TrendDirection} ({analysis.TrendPerYear:+0.0;-0.0} PPG/year)");
    Console.WriteLine($"  Durability:          {analysis.DurabilityPct:F0}%");
    Console.WriteLine($"  Consistency:         {analysis.ConsistencyScore:F0}/100 (Boom: {analysis.BoomRate:F0}% / Bust: {analysis.BustRate:F0}%)");

    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    return 0;
}

// ======================================================================
// REPORT 5: TEAM DEEP DIVE (full roster with AI draft outlook)
// ======================================================================
async Task<int> RunTeamDeepDive(TeamCommandOptions options)
{
    var username = options.Username;
    var leagueId = options.LeagueId;

    Console.WriteLine($"Loading roster for '{username}'...");

    var user = await client.GetUserAsync(username);
    if (user is null) { Console.WriteLine($"User '{username}' not found."); return 1; }

    var league = await client.GetLeagueAsync(leagueId);
    if (league is null) { Console.WriteLine("League not found."); return 1; }

    var scorer = new FantasyScorer(league.ScoringSettings ?? new());
    var leagueConfig = LeagueRosterConfig.FromLeague(league);
    var currentSeason = int.TryParse(league.Season, out var s) ? s : DateTime.UtcNow.Year;

    var rosterPlayers = await sleeperService.GetRosterPlayersAsync(leagueId, username);
    if (rosterPlayers.Count == 0) { Console.WriteLine("No roster found."); return 1; }

    var keeperValues = await sleeperService.GetRosterKeeperValuesAsync(leagueId, username);
    var keeperLookup = keeperValues.ToDictionary(kv => kv.Player.PlayerId, kv => kv);

    // Determine last completed season
    var nflState = await client.GetNflStateAsync();
    var lastCompleted = nflState is not null
        ? int.Parse(nflState.PreviousSeason ?? (currentSeason - 1).ToString())
        : currentSeason - 1;
    if (nflState?.SeasonType == "off")
        lastCompleted = int.Parse(nflState.PreviousSeason ?? (currentSeason - 1).ToString());

    // Pre-fetch multi-year stats once for all players
    Console.WriteLine($"Fetching {HistoryYears}-year stats ({lastCompleted - HistoryYears + 1}-{lastCompleted})...");
    var seasonStatsByYear = new Dictionary<int, Dictionary<string, SeasonPlayerStats>>();
    var weeklyStatsByYear = new Dictionary<int, Dictionary<string, List<WeeklyPlayerStats>>>();

    var seasonTasks = new Dictionary<int, Task<Dictionary<string, SeasonPlayerStats>>>();
    var weeklyTasks = new Dictionary<int, Task<Dictionary<string, List<WeeklyPlayerStats>>>>();
    for (int y = lastCompleted; y > lastCompleted - HistoryYears; y--)
    {
        seasonTasks[y] = nflData.GetSeasonStatsBySleeperIdAsync(y, "reg");
        weeklyTasks[y] = nflData.GetWeeklyStatsBySleeperIdAsync(y);
    }
    await Task.WhenAll(Task.WhenAll(seasonTasks.Values), Task.WhenAll(weeklyTasks.Values));
    foreach (var (y, t) in seasonTasks) seasonStatsByYear[y] = t.Result;
    foreach (var (y, t) in weeklyTasks) weeklyStatsByYear[y] = t.Result;

    // Fetch rankings and replacement levels for keeper grading
    Console.WriteLine("Ranking all NFL players by league scoring...");
    var lastSeasonAllStats = await nflData.GetSeasonStatsAsync(lastCompleted, "reg");
    var sleeperToGsis = await nflData.GetSleeperToGsisMapAsync();
    var ranker = new LeagueRanker(scorer);
    var rankings = ranker.RankBySleeperId(lastSeasonAllStats, sleeperToGsis);
    var replacementLevels = rankings.CalculateReplacementLevels(
        leagueConfig.Teams,
        leagueConfig.StarterSlots,
        leagueConfig.FlexSlots,
        leagueConfig.FlexEligiblePositions);
    var keeperAnalyzer = new KeeperAnalyzer(replacementLevels);

    var draftAgent = await TeamDraftAgent.TryCreateAsync(foundrySettings, currentSeason);
    if (draftAgent is null)
    {
        Console.WriteLine($"  (AI draft outlook disabled -- {foundrySettings.MissingConfigurationMessage})");
    }

    // Sort players: starters first (by position order), then bench
    var posOrder = new Dictionary<string, int>
    {
        ["QB"] = 1, ["RB"] = 2, ["WR"] = 3, ["TE"] = 4, ["K"] = 5, ["DEF"] = 6
    };
    var sorted = rosterPlayers
        .OrderByDescending(p => p.IsStarter)
        .ThenBy(p => posOrder.GetValueOrDefault(p.Player.Position ?? "", 99))
        .ThenBy(p => p.Player.FullName)
        .ToList();

    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    Console.WriteLine($"  TEAM DEEP DIVE -- {league.Name} ({league.Season})");
    Console.WriteLine($"  Owner: {user.DisplayName ?? username}  |  {sorted.Count} players  |  {leagueConfig.Teams} teams, {leagueConfig.MaxKeepers} keepers");
    Console.WriteLine("===================================================================================");

    var playerNum = 0;
    foreach (var rp in sorted)
    {
        var p = rp.Player;
        playerNum++;

        // Skip DEF — no individual stats
        if (p.Position == "DEF") continue;

        var role = rp.IsStarter ? "STARTER" : rp.IsReserve ? "IR" : "BENCH";

        Console.WriteLine();
        Console.WriteLine("-----------------------------------------------------------------------------------");
        Console.WriteLine($"  [{playerNum}/{sorted.Count}] {p.FullName}  ({p.Position} | {p.Team ?? "FA"} | Age {p.Age} | {p.YearsExp ?? 0} yrs)  [{role}]");
        Console.WriteLine("-----------------------------------------------------------------------------------");

        // Build multi-year history
        var seasonHistory = new List<SeasonSummary>();
        var allWeeklyPoints = new Dictionary<int, List<decimal>>();

        for (int y = lastCompleted; y > lastCompleted - HistoryYears; y--)
        {
            var seasonStats = seasonStatsByYear[y];
            var weeklyStats = weeklyStatsByYear[y];

            if (seasonStats.TryGetValue(p.PlayerId, out var ss))
            {
                var scored = scorer.ScoreSeason(ss);
                var games = ss.Games ?? 0;
                var ppg = games > 0 ? Math.Round(scored.TotalPoints / games, 2) : 0m;

                var weeklyPts = new List<decimal>();
                if (weeklyStats.TryGetValue(p.PlayerId, out var weeks))
                {
                    weeklyPts = weeks.Where(w => w.SeasonType == "REG")
                        .OrderBy(w => w.Week)
                        .Select(w => scorer.ScoreWeekly(w).TotalPoints)
                        .ToList();
                    allWeeklyPoints[y] = weeklyPts;
                }

                var stdDev = weeklyPts.Count >= 2 ? ConsistencyCalculator.CalculateStdDev(weeklyPts) : 0m;
                seasonHistory.Add(new SeasonSummary(y, games, scored.TotalPoints, ppg, Math.Round(stdDev, 2)));
            }
        }

        if (seasonHistory.Count == 0)
        {
            Console.WriteLine("  No scoring data available (rookie or no recent stats).");
            continue;
        }

        // Season-by-season table
        var posAvg = VorpCalculator.DefaultReplacementPpg.GetValueOrDefault(p.Position?.ToUpperInvariant() ?? "", 8m);
        Console.WriteLine($"  {"Season",-8} {"Games",-7} {"Total",-8} {"PPG",-7} {"StdDev",-8} {"Floor",-7} {"Ceil",-7} {"Boom%",-7} {"Bust%",-7}");
        Console.WriteLine($"  {"------",-8} {"-----",-7} {"-----",-8} {"---",-7} {"------",-8} {"-----",-7} {"----",-7} {"-----",-7} {"-----",-7}");

        foreach (var sh in seasonHistory.OrderBy(ss => ss.Season))
        {
            var weeklyPts = allWeeklyPoints.GetValueOrDefault(sh.Season, []);
            var floor = ConsistencyCalculator.CalculateFloor(weeklyPts);
            var ceil = ConsistencyCalculator.CalculateCeiling(weeklyPts);
            var boom = ConsistencyCalculator.CalculateBoomRate(weeklyPts, posAvg);
            var bust = ConsistencyCalculator.CalculateBustRate(weeklyPts, posAvg);

            Console.WriteLine($"  {sh.Season,-8} {sh.GamesPlayed,-7} {sh.TotalPoints,-8:F1} {sh.Ppg,-7:F1} {sh.StdDev,-8:F1} {floor,-7:F1} {ceil,-7:F1} {boom,-7:F0} {bust,-7:F0}");
        }

        // Projections & analysis
        var recentYear = seasonHistory.OrderByDescending(ss => ss.Season).First().Season;
        var recentWkPts = allWeeklyPoints.GetValueOrDefault(recentYear, []);
        var kvLookup = keeperLookup.GetValueOrDefault(p.PlayerId);
        var analysis = keeperAnalyzer.Analyze(p.PlayerId, p.FullName, p.Position, p.Age,
            kvLookup?.KeeperCostRound, kvLookup?.CanBeKept ?? false, seasonHistory, recentWkPts);

        Console.WriteLine();
        Console.WriteLine($"  Weighted PPG: {analysis.WeightedPpg:F1}  |  Age-Adj PPG: {analysis.AgeAdjustedPpg:F1}  |  Projected: {analysis.ProjectedSeasonPoints:F0} pts");
        Console.WriteLine($"  VORP: {analysis.Vorp:F1}  |  Trend: {analysis.TrendDirection} ({analysis.TrendPerYear:+0.0;-0.0}/yr)  |  Durability: {analysis.DurabilityPct:F0}%  |  Consistency: {analysis.ConsistencyScore:F0}/100");
        if (kvLookup is not null && kvLookup.CanBeKept && kvLookup.KeeperCostRound.HasValue)
        {
            var pickEst = (kvLookup.KeeperCostRound.Value - 1) * leagueConfig.Teams + leagueConfig.Teams / 2;
            var rankLabel = rankings.GetRankLabel(p.PlayerId) ?? "N/R";
            Console.WriteLine($"  KEEPER: R{kvLookup.KeeperCostRound} (~Pick {pickEst})  |  Grade: {analysis.KeeperGrade}  |  Score: {analysis.KeeperScore:F1}  |  Surplus: {analysis.KeeperSurplus:F1}  |  {rankLabel}");
        }
        else if (kvLookup is not null)
        {
            Console.WriteLine($"  KEEPER: Cannot keep (no draft pick on record)");
        }

        // AI draft outlook with web search
        if (draftAgent is not null)
        {
            try
            {
                if (playerNum > 1) await Task.Delay(TimeSpan.FromSeconds(3));

                Console.Write("  Draft Outlook: (searching current ADP & news...)");
                var opinion = await draftAgent.GetDraftOpinionAsync(
                    p.FullName, p.Position, p.Age,
                    analysis.WeightedPpg, analysis.ProjectedSeasonPoints, analysis.Vorp,
                    analysis.TrendDirection, analysis.TrendPerYear,
                    analysis.DurabilityPct, analysis.ConsistencyScore);
                Console.Write("\r  Draft Outlook:                                    \n");
                foreach (var line in opinion.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Console.WriteLine($"    {line.Trim()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"  Draft Outlook: (unavailable -- {ex.Message})");
            }
        }
    }

    // Team summary
    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    Console.WriteLine("  TEAM SUMMARY");
    Console.WriteLine("===================================================================================");

    var fantasyPositions = new[] { "QB", "RB", "WR", "TE", "K" };
    foreach (var pos in fantasyPositions)
    {
        var posPlayers = sorted.Where(rp => rp.Player.Position == pos).ToList();
        if (posPlayers.Count == 0) continue;

        var starters = posPlayers.Count(rp => rp.IsStarter);
        Console.WriteLine($"  {pos}: {posPlayers.Count} rostered ({starters} starting)  --  {string.Join(", ", posPlayers.Select(rp => rp.Player.FullName))}");
    }

    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    return 0;
}

// ======================================================================
// REPORT 4: MATCHUP SCOREBOARD
// ======================================================================
async Task<int> RunMatchupScoreboard(MatchupCommandOptions options)
{
    var leagueId = options.LeagueId;

    var league = await client.GetLeagueAsync(leagueId);
    if (league is null) { Console.WriteLine("League not found."); return 1; }

    int week;
    if (options.Week is null)
    {
        var nflState = await client.GetNflStateAsync();
        week = nflState?.Week ?? 1;
        Console.WriteLine($"Using current week: {week}");
    }
    else
    {
        week = options.Week.Value;
    }

    Console.WriteLine($"Fetching Week {week} matchups...");

    var scoreboard = await sleeperService.GetWeekScoreboardAsync(leagueId, week);

    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    Console.WriteLine($"  WEEK {week} SCOREBOARD -- {league.Name} ({league.Season})");
    Console.WriteLine("===================================================================================");
    Console.WriteLine();

    foreach (var m in scoreboard.OrderByDescending(m => (m.Team1Points ?? 0) + (m.Team2Points ?? 0)))
    {
        var t1Name = m.Team1TeamName ?? m.Team1DisplayName ?? $"Team {m.Team1RosterId}";
        var t2Name = m.Team2TeamName ?? m.Team2DisplayName ?? $"Team {m.Team2RosterId}";
        var t1Pts = m.Team1Points?.ToString("F2") ?? "0.00";
        var t2Pts = m.Team2Points?.ToString("F2") ?? "0.00";

        var winner = (m.Team1Points ?? 0) >= (m.Team2Points ?? 0) ? "<<<" : "   ";
        var winner2 = (m.Team2Points ?? 0) >= (m.Team1Points ?? 0) ? ">>>" : "   ";

        Console.WriteLine($"  {t1Name,-20} {t1Pts,8} {winner}  vs  {winner2} {t2Pts,-8} {t2Name}");
    }

    Console.WriteLine();
    Console.WriteLine("===================================================================================");
    return 0;
}

// ======================================================================
// REPORT 5: WEEKLY LEAGUE RECAP (AI-authored via Microsoft Agent Framework)
// ======================================================================
async Task<int> RunWeeklyRecap(WeeklyRecapCommandOptions options)
{
    var week = options.Week;
    var leagueId = options.LeagueId;
    var overrideSeason = options.Season;

    // The league's actual schedule is 17 weeks: regular season through week 15,
    // playoffs week 16 (semifinals) and week 17 (championship). There is no week 18.
    if (week < 1 || week > 17)
    {
        Console.WriteLine($"Invalid week {week}. Valid weeks for this league are 1-17 (regular season 1-15, playoffs 16-17). There is no week 18.");
        return 1;
    }

    var loreSeason = await ResolveLoreSeasonAsync(leagueId, overrideSeason);
    var lore = LeagueLore.TryLoadLayers(
        RecapPaths.LegacyLorePath,
        RecapPaths.LoreDirectory,
        loreSeason,
        week);
    if (lore is null)
    {
        Console.WriteLine($"  (warning: no lore files found -- proceeding without lore)");
        lore = LeagueLore.ParseFrom("");
    }
    else
    {
        Console.WriteLine($"  (loaded {lore.Sources.Count} lore layers for {lore.Owners.Count} owners, {lore.Relationships.Count} relationship rules)");
    }

    Console.WriteLine($"Building recap envelope for week {week}...");
    var builder = new RecapEnvelopeBuilder(client, sleeperService, nflData, lore,
        sp.GetRequiredService<WeeklyInjuryReportService>());
    RecapEnvelope envelope;
    try
    {
        envelope = await builder.BuildAsync(leagueId, week, overrideSeason,
            options: new RecapEnvelopeBuildOptions(InjuryWindow: options.InjuryWindow));
    }
    catch (Exception exception) when (options.InjuryWindow is not null)
    {
        Console.Error.WriteLine($"Recap with injury evidence failed: {exception.Message}");
        return 1;
    }

    Console.WriteLine($"  envelope: {envelope.Owners.Count} owners, {envelope.Games.Count} games, {envelope.Themes.WaiverGrades.Count} waivers, {envelope.Themes.Trades.Count} trades, {envelope.AgentFetchHints.Count} fetch hints");

    await using var agentProvider = await ReportAgentProvider.CreateAsync(copilotSettings);
    var agent = await agentProvider.TryCreateRecapAgentAsync(foundrySettings);
    string output;
    if (agent is null)
    {
        Console.WriteLine();
        Console.WriteLine("  (AI recap disabled -- no Copilot session and no Foundry configuration)");
        Console.WriteLine("  (writing data-only envelope dump for debugging)");
        output = DumpEnvelopeAsMarkdown(envelope);
    }
    else
    {
        output = await agent.WriteRecapAsync(envelope);
    }

    var path = RecapPaths.RecapFile(envelope.Meta.Season, envelope.Meta.Week);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, output);
    Console.WriteLine();
    Console.WriteLine($"  Wrote {path}");
    Console.WriteLine();
    return 0;
}

// ======================================================================
// REPORT 5b: SEASON-IN-REVIEW (deterministic aggregate + AI prose pass)
// ======================================================================
async Task<int> RunSeasonRecap(SeasonRecapCommandOptions options)
{
    var leagueId = options.LeagueId;
    var overrideSeason = options.Season;

    var loreSeason = await ResolveLoreSeasonAsync(leagueId, overrideSeason);
    var lore = LeagueLore.TryLoadLayers(
        RecapPaths.LegacyLorePath,
        RecapPaths.LoreDirectory,
        loreSeason) ?? LeagueLore.ParseFrom("");

    Console.WriteLine($"Building season aggregate for league {leagueId}...");
    var seasonBuilder = new SeasonAggregateBuilder(client, sleeperService, nflData, lore);
    var (aggregate, awards) = await seasonBuilder.BuildAsync(leagueId, overrideSeason);

    if (aggregate.Outcome is null)
    {
        Console.WriteLine("  Season is not finished (no SeasonOutcome resolved). The season recap requires the championship week's bracket to be complete.");
        return 1;
    }

    int season = aggregate.Season;
    Directory.CreateDirectory(Path.GetDirectoryName(RecapPaths.SeasonRecap(season))!);

    // Persist sidecars BEFORE the prose pass so they're on disk even if the agent fails.
    var jsonOpts = new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    File.WriteAllText(RecapPaths.SeasonAggregateJson(season), System.Text.Json.JsonSerializer.Serialize(aggregate, jsonOpts));
    File.WriteAllText(RecapPaths.SeasonAwardsJson(season),    System.Text.Json.JsonSerializer.Serialize(awards, jsonOpts));
    File.WriteAllText(RecapPaths.SeasonOutcomeJson(season),   System.Text.Json.JsonSerializer.Serialize(aggregate.Outcome, jsonOpts));
    Console.WriteLine($"  Wrote season-aggregate.json, season-awards.json, season-outcome.json");

    // Render charts.
    SeasonChartRenderer.WriteAll(season, aggregate);
    Console.WriteLine($"  Wrote 4 SVG charts to {RecapPaths.SeasonChartsDir(season)}");

    // Lift weekly digests for the agent + appendix.
    var digests = SeasonComposer.LoadWeeklyDigests(season, aggregate.Schedule.ChampionshipWeek);
    Console.WriteLine($"  Loaded {digests.Count} weekly digests from disk");

    // Run the agent.
    await using var agentProvider = await ReportAgentProvider.CreateAsync(copilotSettings);
    var agent = await agentProvider.TryCreateSeasonAgentAsync(foundrySettings);
    string proseBody = "";
    if (agent is null)
    {
        Console.WriteLine();
        Console.WriteLine("  (Season agent disabled -- no Copilot session and no Foundry configuration)");
    }
    else
    {
        proseBody = await agent.WriteAsync(aggregate, awards, digests);
    }

    var composed = SeasonComposer.Compose(aggregate, awards, digests, proseBody);
    File.WriteAllText(RecapPaths.SeasonRecap(season), composed);
    Console.WriteLine($"  Wrote {RecapPaths.SeasonRecap(season)}");

    var manifest = SeasonComposer.BuildAndWriteManifest(aggregate);
    Console.WriteLine($"  Wrote manifest.json indexing {manifest.Artifacts.Count} artifacts");
    Console.WriteLine();
    return 0;
}
Task<int> RunExport(ExportCommandOptions options)
    => SeasonExporter.RunAsync(client, options.Season, options.LeagueId);

async Task<int> RunRostersHistory(RostersHistoryCommandOptions options)
{
    var season = options.Season;
    var seasonStr = season.ToString();
    var leagueId = options.LeagueId;

    var league = await client.GetLeagueAsync(leagueId);
    if (league is null) { Console.WriteLine($"  League {leagueId} not found."); return 1; }
    if (!string.Equals(league.Season, seasonStr, StringComparison.Ordinal))
        Console.WriteLine($"  (note: requested season {seasonStr} but league {leagueId} reports season {league.Season})");

    var users = await client.GetLeagueUsersAsync(leagueId);
    var rosters = await client.GetLeagueRostersAsync(leagueId);
    var players = await client.GetAllPlayersAsync();

    // Resolve owner identity through lore so no Sleeper handle reaches these data files.
    var historyLore = LeagueLore.TryLoadLayers(
        RecapPaths.LegacyLorePath,
        RecapPaths.LoreDirectory,
        int.TryParse(seasonStr, out var loreSeasonYear) ? loreSeasonYear : DateTime.UtcNow.Year)
        ?? LeagueLore.ParseFrom("");

    var ownerByRosterId = rosters.ToDictionary(
        r => r.RosterId,
        r =>
        {
            var u = users.FirstOrDefault(x => x.UserId == r.OwnerId);
            var handle = u?.Username ?? u?.DisplayName ?? "";
            historyLore.OwnersByUsername.TryGetValue(handle.ToLowerInvariant(), out var loreOwner);
            return new
            {
                UserId = r.OwnerId,
                OwnerName = loreOwner?.Name ?? $"Roster {r.RosterId}",
                TeamName = u?.Metadata != null && u.Metadata.TryGetValue("team_name", out var tn) && !string.IsNullOrWhiteSpace(tn) ? tn : ""
            };
        });

    // Walk weeks until empty.
    var maxWeek = 18;
    int lastWeekWithData = 0;
    for (int w = 1; w <= maxWeek; w++)
    {
        var ms = await client.GetLeagueMatchupsAsync(leagueId, w);
        if (ms.Count == 0 || ms.All(m => (m.Points ?? 0m) == 0m && (m.Players is null || m.Players.Count == 0))) break;
        lastWeekWithData = w;
    }

    if (lastWeekWithData == 0)
    {
        Console.WriteLine($"  No matchup data for league {leagueId}.");
        return 1;
    }

    Console.WriteLine($"  Found data for weeks 1..{lastWeekWithData}; writing kickoff-locked rosters...");

    var outDir = RecapPaths.DataDir(season);
    Directory.CreateDirectory(outDir);

    var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

    for (int w = 1; w <= lastWeekWithData; w++)
    {
        var matchups = await client.GetLeagueMatchupsAsync(leagueId, w);
        var teams = matchups.OrderBy(m => m.RosterId).Select(m =>
        {
            ownerByRosterId.TryGetValue(m.RosterId, out var o);
            string PlayerLabel(string pid)
            {
                if (players.TryGetValue(pid, out var p) && p is not null)
                    return $"{p.FullName ?? (p.FirstName + " " + p.LastName).Trim()} ({p.Position ?? "?"}, {p.Team ?? "FA"})";
                return pid;
            }
            var startersList = (m.Starters ?? []).Where(s => s != "0").Select(pid => new
            {
                player_id = pid,
                label = PlayerLabel(pid),
                points = m.PlayersPoints != null && m.PlayersPoints.TryGetValue(pid, out var sp) ? (decimal?)sp : null
            }).ToList();
            var allList = (m.Players ?? []).Select(pid => new
            {
                player_id = pid,
                label = PlayerLabel(pid),
                points = m.PlayersPoints != null && m.PlayersPoints.TryGetValue(pid, out var sp) ? (decimal?)sp : null,
                started = (m.Starters ?? []).Contains(pid)
            }).ToList();

            return new
            {
                roster_id = m.RosterId,
                user_id = o?.UserId,
                owner_name = o?.OwnerName,
                team_name = o?.TeamName,
                matchup_id = m.MatchupId,
                points = m.Points,
                starters = startersList,
                bench = allList.Where(p => !p.started).Select(p => new { p.player_id, p.label, p.points }).ToList(),
                roster = allList
            };
        }).ToList();

        var doc = new
        {
            season = seasonStr,
            week = w,
            league_id = leagueId,
            league_name = FirstNonBlankName(historyLore.League.Name, "The League"),
            captured_at_utc = DateTime.UtcNow.ToString("o"),
            note = "Kickoff-locked roster snapshot derived from /league/{id}/matchups/{week}. Includes starters and bench at lock time, with per-player points scored that week.",
            teams
        };

        var file = RecapPaths.DataFile(season, w);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(doc, jsonOpts));
        Console.WriteLine($"    wrote {file} ({teams.Count} teams)");
    }

    Console.WriteLine($"  Done -> {outDir}");
    return 0;
}

async Task<int> RunAssetHistory(AssetHistoryCommandOptions options)
{
    var seasonDirectory = RecapPaths.DataDir(options.Season);
    if (!Directory.Exists(seasonDirectory) || !Directory.EnumerateFiles(seasonDirectory, "week-*.json").Any())
    {
        Console.WriteLine($"  No roster history found under {seasonDirectory}. Run rosters-history first.");
        return 1;
    }

    var transactionTasks = Enumerable.Range(1, 18)
        .Select(async week => new WeeklyTransactions(
            week,
            await client.GetTransactionsAsync(options.LeagueId, week).ConfigureAwait(false)))
        .ToList();
    var transactionWeeks = await Task.WhenAll(transactionTasks).ConfigureAwait(false);
    var rosterWeeks = await AssetMovementAnalyzer.LoadRosterWeeksAsync(seasonDirectory).ConfigureAwait(false);
    var players = await client.GetAllPlayersAsync().ConfigureAwait(false);
    var playerLabels = players.ToDictionary(
        value => value.Key,
        value => $"{value.Value.FullName ?? value.Key} ({value.Value.Position ?? "?"})",
        StringComparer.OrdinalIgnoreCase);
    var audit = AssetMovementAnalyzer.Analyze(options.Season, options.LeagueId, rosterWeeks, transactionWeeks, playerLabels);
    var jsonOptions = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    var history = new
    {
        season = options.Season,
        leagueId = options.LeagueId,
        capturedAtUtc = DateTimeOffset.UtcNow,
        weeks = transactionWeeks
    };
    File.WriteAllText(RecapPaths.TransactionHistory(options.Season), System.Text.Json.JsonSerializer.Serialize(history, jsonOptions));
    File.WriteAllText(RecapPaths.AssetMovementAudit(options.Season), System.Text.Json.JsonSerializer.Serialize(audit, jsonOptions));
    File.WriteAllText(RecapPaths.AssetMovementSummary(options.Season), AssetMovementAnalyzer.BuildText(audit));
    Console.WriteLine($"  Wrote {RecapPaths.TransactionHistory(options.Season)}");
    Console.WriteLine($"  Wrote {RecapPaths.AssetMovementAudit(options.Season)}");
    Console.WriteLine($"  Wrote {RecapPaths.AssetMovementSummary(options.Season)}");
    Console.WriteLine($"  Audited {audit.InitialAssetCount} Week 1 assets: {string.Join(", ", audit.ExitCounts.OrderBy(value => value.Key).Select(value => $"{value.Key}={value.Value}"))}");
    return 0;
}
static string FirstNonBlankName(params string?[] candidates)
    => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "The League";

static string DumpEnvelopeAsMarkdown(RecapEnvelope env)
{
    var sb = new System.Text.StringBuilder();

    // Never leak Sleeper usernames into published markdown: map every owner handle
    // (username or platform display name) to the lore first name. This fails CLOSED —
    // an owner with no lore entry renders as "Roster N" rather than falling back to a
    // platform handle, because most handles embed a surname.
    var nameByHandle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var o in env.Owners)
    {
        var safe = !string.IsNullOrWhiteSpace(o.RealName) ? o.RealName! : $"Roster {o.RosterId}";
        if (!string.IsNullOrWhiteSpace(o.Username)) nameByHandle[o.Username] = safe;
        if (!string.IsNullOrWhiteSpace(o.DisplayName)) nameByHandle[o.DisplayName] = safe;
    }
    string Own(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return "";
        return nameByHandle.TryGetValue(handle.Trim(), out var n) ? n : "(owner)";
    }

    sb.AppendLine($"# Week {env.Meta.Week} -- {env.Meta.LeagueName} ({env.Meta.Season})");
    sb.AppendLine();
    sb.AppendLine($"_Data-only dump (no AI agent configured). {env.Meta.SeasonType}{(env.Meta.PlayoffRound is null ? "" : $" / {env.Meta.PlayoffRound}")}._");
    if (env.InjuryReport is not null)
        sb.AppendLine(env.InjuryReport.ToMarkdown());
    sb.AppendLine();
    sb.AppendLine("## Standings");
    sb.AppendLine();
    sb.AppendLine("| Rank | Team | Owner | W-L-T | PF | PA | FAAB |");
    sb.AppendLine("|---:|---|---|:---:|---:|---:|---:|");
    foreach (var s in env.Standings)
        sb.AppendLine($"| {s.Rank} | {s.TeamName} | {Own(s.OwnerDisplay)} | {s.Wins}-{s.Losses}-{s.Ties} | {s.PointsFor:F2} | {s.PointsAgainst:F2} | {s.WaiverBudgetRemaining?.ToString() ?? "—"} |");
    sb.AppendLine();
    sb.AppendLine("## Games");
    sb.AppendLine();
    foreach (var g in env.Games.OrderByDescending(g => g.Home.FinalScore + g.Away.FinalScore))
    {
        sb.AppendLine($"### {Own(g.Home.OwnerDisplay)} ({g.Home.FinalScore:F2}) vs {Own(g.Away.OwnerDisplay)} ({g.Away.FinalScore:F2})" +
                       (g.StoryHookLabel is null ? "" : $" -- _{g.StoryHookLabel}_"));
        sb.AppendLine($"- Margin: {g.Margin:F2}{(g.Blowout ? " (blowout)" : "")}");
        if (g.Home.KeyPerformer is not null) sb.AppendLine($"- Hero ({Own(g.Home.OwnerDisplay)}): {g.Home.KeyPerformer.FullName} -- {g.Home.KeyPerformer.Points:F1}");
        if (g.Away.KeyPerformer is not null) sb.AppendLine($"- Hero ({Own(g.Away.OwnerDisplay)}): {g.Away.KeyPerformer.FullName} -- {g.Away.KeyPerformer.Points:F1}");
        if (g.LineupOptimalityHomePct.HasValue) sb.AppendLine($"- Optimality: {Own(g.Home.OwnerDisplay)} {g.LineupOptimalityHomePct:F1}% / {Own(g.Away.OwnerDisplay)} {g.LineupOptimalityAwayPct:F1}%");
        sb.AppendLine();
    }
    sb.AppendLine("## League themes");
    sb.AppendLine();
    if (env.Themes.HighestScore is not null) sb.AppendLine($"- High: {Own(env.Themes.HighestScore.OwnerDisplay)} -- {env.Themes.HighestScore.Score:F2}");
    if (env.Themes.LowestScore is not null) sb.AppendLine($"- Low: {Own(env.Themes.LowestScore.OwnerDisplay)} -- {env.Themes.LowestScore.Score:F2}");
    sb.AppendLine();
    sb.AppendLine("**Top performers**");
    foreach (var p in env.Themes.TopFivePerformers)
        sb.AppendLine($"- {p.FullName} ({p.Position}, {p.RealNflTeam}) -- {p.Points:F2} pts (owned by {Own(p.OwnedByDisplay)})");
    sb.AppendLine();
    if (env.Themes.WaiverGrades.Count > 0)
    {
        sb.AppendLine("**Waiver / FA splash**");
        foreach (var w in env.Themes.WaiverGrades.Take(8))
            sb.AppendLine($"- {Own(w.ClaimingDisplay)} added {w.PlayerName}{(w.FaabBid is null ? "" : $" (${w.FaabBid})")}{(w.WeekPoints is null ? "" : $" -- {w.WeekPoints:F2} pts this week")}");
        sb.AppendLine();
    }
    if (env.Themes.Trades.Count > 0)
    {
        sb.AppendLine("**Trades**");
        foreach (var t in env.Themes.Trades)
        {
            sb.AppendLine($"- Trade ({t.CompletedAt:yyyy-MM-dd}):");
            foreach (var side in t.Sides)
                sb.AppendLine($"  - {Own(side.OwnerDisplay)} received: {string.Join(", ", side.ReceivedPlayers)}{(side.ReceivedDraftPicks.Count > 0 ? "; picks: " + string.Join(", ", side.ReceivedDraftPicks) : "")}{(side.FaabReceived > 0 ? $"; ${side.FaabReceived} FAAB" : "")}");
        }
        sb.AppendLine();
    }
    if (env.Themes.StatLineOddities.Count > 0)
    {
        sb.AppendLine("**Stat-line oddities**");
        foreach (var o in env.Themes.StatLineOddities) sb.AppendLine($"- {o}");
        sb.AppendLine();
    }
    sb.AppendLine("## Look-ahead");
    sb.AppendLine();
    foreach (var m in env.LookAhead.Matchups)
        sb.AppendLine($"- Week {env.LookAhead.NextWeek}: {Own(m.HomeOwnerDisplay)} ({m.HomeProjection?.ToString("F1") ?? "?"}) vs {Own(m.AwayOwnerDisplay)} ({m.AwayProjection?.ToString("F1") ?? "?"}){(m.StoryHookLabel is null ? "" : $" -- _{m.StoryHookLabel}_")}");
    sb.AppendLine();
    sb.AppendLine("## Agent fetch hints");
    foreach (var h in env.AgentFetchHints) sb.AppendLine($"- {h}");
    return sb.ToString();
}
