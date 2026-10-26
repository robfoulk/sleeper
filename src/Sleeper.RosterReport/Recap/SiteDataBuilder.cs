using System.Text.Json;
using System.Text.Json.Nodes;
using Sleeper.Api;
using Sleeper.Api.NflData;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Merges every season's sidecars into a single <c>site/src/data/league.json</c>.
/// The site renders standings, scores, and records from this file rather than from parsed
/// prose, so a number on a page can always be traced back to a season sidecar.
/// </summary>
/// <remarks>
/// Franchise-vs-owner continuity is decided here, once, and every page reads the result.
/// A franchise is a roster slot and persists across ownership changes; an owner is a person.
/// Results are recorded per owner-season and then aggregated both ways, so a former owner
/// keeps the record he earned and an incoming owner inherits the roster but not the results.
/// </remarks>
internal static class SiteDataBuilder
{
    public static async Task<int> RunAsync(ISleeperClient client, INflDataClient nflData)
    {
        var root = RecapPaths.WorkspaceRoot;
        var recapsRoot = Path.Combine(root, "recaps");
        if (!Directory.Exists(recapsRoot))
        {
            Console.WriteLine("  No recaps directory found.");
            return 1;
        }

        var readOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var seasons = new List<SeasonBundle>();

        foreach (var dir in Directory.EnumerateDirectories(recapsRoot).OrderBy(d => d))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var year)) continue;

            var aggregatePath = Path.Combine(dir, "season-aggregate.json");
            if (!File.Exists(aggregatePath))
            {
                Console.WriteLine($"  {year}: no season-aggregate.json, skipping completed-season archive.");
                continue;
            }

            var aggregate = JsonSerializer.Deserialize<SeasonAggregate>(
                await File.ReadAllTextAsync(aggregatePath), readOptions);
            if (aggregate is null) continue;

            SeasonAwards? awards = null;
            var awardsPath = Path.Combine(dir, "season-awards.json");
            if (File.Exists(awardsPath))
                awards = JsonSerializer.Deserialize<SeasonAwards>(await File.ReadAllTextAsync(awardsPath), readOptions);

            Console.WriteLine($"  {year}: loading matchups from league {aggregate.LeagueId}...");
            var games = await LoadGamesAsync(client, aggregate);

            seasons.Add(new SeasonBundle(year, aggregate, awards, games));
        }

        if (seasons.Count == 0)
        {
            Console.WriteLine("  No completed seasons found.");
            return 1;
        }

        var leagueName = seasons[^1].Aggregate.LeagueName;
        var currentOwners = LoadCurrentOwners(recapsRoot);
        var currentEdition = LoadWeeklyEdition(recapsRoot);
        if (currentEdition is { } edition)
        {
            foreach (var team in edition.GetProperty("teams").EnumerateArray())
            {
                currentOwners[team.GetProperty("franchise_id").GetInt32()] = new CurrentTeam(
                    team.GetProperty("owner_name").GetString()!,
                    team.GetProperty("team_name").GetString()!);
            }
        }
        var currentResults = currentEdition is { } resultsEdition
            && seasons.All(season => season.Year != ReadEditionSeason(resultsEdition))
                ? ParseCurrentSeason(resultsEdition)
                : null;
        if (currentResults is not null)
            Console.WriteLine($"  {currentResults.Year}: including active results through Week {currentResults.CompletedWeek} in records.");

        var franchises = BuildFranchises(seasons, currentOwners);
        var owners = BuildOwners(seasons, currentOwners, currentResults);
        var headToHead = BuildHeadToHead(seasons, currentResults);
        var publicEdition = currentEdition is { } loadedEdition
            ? BuildPublicWeeklyEdition(loadedEdition)
            : null;
        var firstQuarter = currentEdition is { } current
            ? await BuildFirstQuarterAsync(nflData, root, current)
            : null;

        var doc = new
        {
            generated_at_utc = DateTime.UtcNow.ToString("o"),
            league_name = leagueName,
            current_edition = publicEdition,
            first_quarter = firstQuarter,
            seasons = seasons.Select(BuildSeason).ToList(),
            franchises,
            owners,
            records = new
            {
                // Regular-season records and head-to-head are deliberately different scopes.
                // Standings use the 15-week regular season; head-to-head counts every game
                // two owners have played, playoffs and consolation included, because an
                // elimination is exactly the kind of result a rivalry claim rests on.
                notes = new[]
                {
                    currentResults is null
                        ? "Records cover the completed seasons in this archive, 2024 onward, and the regular season only."
                        : $"Records cover completed seasons from 2024 onward and {currentResults.Year} through Week {currentResults.CompletedWeek}, regular season only.",
                    "Head-to-head counts every available game two owners have played, playoffs and consolation included.",
                    "Records are per owner. A franchise that changed hands does not transfer its record."
                },
                all_time = BuildAllTime(seasons, currentResults),
                championships = seasons
                    .Where(s => s.Aggregate.Outcome?.Champion is not null)
                    .Select(s => new
                    {
                        season = s.Year,
                        owner_name = s.Aggregate.Outcome!.Champion!.OwnerRealName,
                        team_name = s.Aggregate.Outcome.Champion.TeamName,
                        runner_up = s.Aggregate.Outcome.RunnerUp?.OwnerRealName
                    })
                    .ToList(),
                single_week_highs = BuildSingleWeekExtremes(seasons, currentResults, best: true),
                single_week_lows = BuildSingleWeekExtremes(seasons, currentResults, best: false),
                head_to_head = headToHead
            },
            articles = BuildArticleIndex(root, recapsRoot),
            draft = BuildDraftSummary(recapsRoot)
        };

        var outPath = Path.Combine(root, "site", "src", "data", "league.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            // The hand-written members below are already snake_case; this makes the records
            // and the reused sidecar types match them instead of leaking PascalCase.
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }));

        Console.WriteLine($"  Wrote {outPath}");
        Console.WriteLine($"  {seasons.Count} season(s), {franchises.Count} franchises, {owners.Count} owners.");
        return 0;
    }

    private static async Task<FirstQuarterReport?> BuildFirstQuarterAsync(
        INflDataClient nflData,
        string root,
        JsonElement edition)
    {
        if (!edition.TryGetProperty("season", out var seasonNode)
            || !edition.TryGetProperty("completed_week", out var completedWeekNode)
            || !edition.TryGetProperty("scoring_settings", out var scoringNode))
        {
            return null;
        }

        var season = seasonNode.ValueKind == JsonValueKind.String
            ? int.Parse(seasonNode.GetString()!)
            : seasonNode.GetInt32();
        var scoringSettings = scoringNode.Deserialize<Dictionary<string, decimal>>()
            ?? throw new InvalidOperationException(
                $"The {season} weekly edition has no usable league scoring settings.");

        Console.WriteLine($"  {season}: calculating First Quarter player scoring through Week {Math.Min(completedWeekNode.GetInt32(), 4)}...");
        var throughWeek = Math.Min(completedWeekNode.GetInt32(), 4);
        var rosterPositions = edition.TryGetProperty("roster_positions", out var rosterPositionsNode)
            ? rosterPositionsNode.EnumerateArray().Select(position => position.GetString()!).ToList()
            : throw new InvalidOperationException(
                $"The {season} weekly edition has no roster positions for lineup analysis.");
        var (defenseScores, teamScores, lineupScores) =
            LoadFirstQuarterScores(root, season, throughWeek, rosterPositions);
        return await FirstQuarterReportBuilder.BuildAsync(
            nflData,
            season,
            completedWeekNode.GetInt32(),
            scoringSettings,
            defenseScores,
            teamScores,
            lineupScores);
    }

    private static (
        List<QuarterDefenseWeekScore> DefenseScores,
        List<QuarterTeamWeekScore> TeamScores,
        List<QuarterLineupWeekScore> LineupScores) LoadFirstQuarterScores(
        string root,
        int season,
        int throughWeek,
        IReadOnlyList<string> rosterPositions)
    {
        var defenseScores = new List<QuarterDefenseWeekScore>();
        var teamScores = new List<QuarterTeamWeekScore>();
        var lineupScores = new List<QuarterLineupWeekScore>();
        for (var week = 1; week <= throughWeek; week++)
        {
            var path = Path.Combine(root, "datafiles", season.ToString(), $"week-{week:00}.json");
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"The {season} Week {week} snapshot is required to rank team defenses.");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var teams = document.RootElement.GetProperty("teams").EnumerateArray().ToList();
            var defenses = teams
                .SelectMany(team => team.GetProperty("roster").EnumerateArray())
                .Where(player => player.GetProperty("label").GetString()?.Contains("(DEF,", StringComparison.Ordinal) == true)
                .GroupBy(player => player.GetProperty("player_id").GetString()!, StringComparer.OrdinalIgnoreCase);

            foreach (var defense in defenses)
            {
                var entry = defense.Single();
                var label = entry.GetProperty("label").GetString()!;
                var nameEnd = label.LastIndexOf(" (DEF,", StringComparison.Ordinal);
                defenseScores.Add(new QuarterDefenseWeekScore(
                    Team: defense.Key,
                    Name: nameEnd > 0 ? label[..nameEnd] : label,
                    Week: week,
                    Points: entry.GetProperty("points").GetDecimal()));
            }

            foreach (var team in teams)
            {
                var rosterId = team.GetProperty("roster_id").GetInt32();
                var ownerName = team.GetProperty("owner_name").GetString()!;
                var teamName = team.GetProperty("team_name").GetString()!;
                var matchupId = team.TryGetProperty("matchup_id", out var matchupNode)
                    && matchupNode.ValueKind != JsonValueKind.Null
                    ? matchupNode.GetInt32()
                    : (int?)null;
                var rosterPlayers = team
                    .GetProperty("roster")
                    .EnumerateArray()
                    .Select(player => new QuarterRosterPlayer(
                        player.GetProperty("label").GetString()!,
                        ParsePlayerPosition(player.GetProperty("label").GetString()!),
                        player.GetProperty("points").GetDecimal(),
                        player.GetProperty("started").GetBoolean()))
                    .ToList();
                var bestBench = rosterPlayers
                    .Where(player => !player.Started)
                    .OrderByDescending(player => player.Points)
                    .FirstOrDefault();
                lineupScores.Add(new QuarterLineupWeekScore(
                    rosterId,
                    ownerName,
                    teamName,
                    week,
                    team.GetProperty("points").GetDecimal(),
                    CalculateOptimalLineup(rosterPlayers, rosterPositions),
                    bestBench?.Label ?? "No bench player",
                    bestBench?.Position,
                    bestBench?.Points ?? 0m));
                teamScores.Add(new QuarterTeamWeekScore(
                    rosterId,
                    ownerName,
                    teamName,
                    "TOTAL",
                    week,
                    team.GetProperty("points").GetDecimal(),
                    matchupId));

                var positionTotals = team
                    .GetProperty("roster")
                    .EnumerateArray()
                    .Where(player => player.GetProperty("started").GetBoolean())
                    .Select(player => new
                    {
                        Position = ParsePlayerPosition(player.GetProperty("label").GetString()!),
                        Points = player.GetProperty("points").GetDecimal()
                    })
                    .Where(player => PositionOrderForTeamTotals.Contains(player.Position))
                    .GroupBy(player => player.Position, StringComparer.OrdinalIgnoreCase);

                foreach (var position in positionTotals)
                {
                    teamScores.Add(new QuarterTeamWeekScore(
                        rosterId,
                        ownerName,
                        teamName,
                        position.Key,
                        week,
                        position.Sum(player => player.Points),
                        matchupId));
                }
            }
        }

        return (defenseScores, teamScores, lineupScores);
    }

    private static readonly HashSet<string> PositionOrderForTeamTotals =
        new(["QB", "RB", "WR", "TE", "K", "DEF"], StringComparer.OrdinalIgnoreCase);

    private static string ParsePlayerPosition(string label)
    {
        var positionStart = label.LastIndexOf('(');
        if (positionStart < 0)
            return "";

        var positionEnd = label.IndexOf(',', positionStart);
        if (positionEnd < 0)
            positionEnd = label.IndexOf(')', positionStart);

        return positionEnd > positionStart
            ? label[(positionStart + 1)..positionEnd].ToUpperInvariant()
            : "";
    }

    private static decimal CalculateOptimalLineup(
        IReadOnlyList<QuarterRosterPlayer> players,
        IReadOnlyList<string> rosterPositions)
    {
        var remaining = players.ToList();
        var total = 0m;
        var directSlots = rosterPositions
            .Where(PositionOrderForTeamTotals.Contains)
            .GroupBy(position => position, StringComparer.OrdinalIgnoreCase);

        foreach (var slots in directSlots)
        {
            var selected = remaining
                .Where(player => string.Equals(player.Position, slots.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(player => player.Points)
                .Take(slots.Count())
                .ToList();
            total += selected.Sum(player => player.Points);
            foreach (var player in selected)
                remaining.Remove(player);
        }

        foreach (var flexSlot in rosterPositions.Where(position => position.Contains("FLEX", StringComparison.OrdinalIgnoreCase)))
        {
            var eligible = flexSlot.ToUpperInvariant() switch
            {
                "WRRB_FLEX" => new HashSet<string>(["WR", "RB"], StringComparer.OrdinalIgnoreCase),
                "REC_FLEX" => new HashSet<string>(["WR", "TE"], StringComparer.OrdinalIgnoreCase),
                "SUPER_FLEX" => new HashSet<string>(["QB", "RB", "WR", "TE"], StringComparer.OrdinalIgnoreCase),
                _ => new HashSet<string>(["RB", "WR", "TE"], StringComparer.OrdinalIgnoreCase)
            };
            var selected = remaining
                .Where(player => eligible.Contains(player.Position))
                .OrderByDescending(player => player.Points)
                .FirstOrDefault();
            if (selected is null) continue;

            total += selected.Points;
            remaining.Remove(selected);
        }

        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }

    private sealed record QuarterRosterPlayer(
        string Label,
        string Position,
        decimal Points,
        bool Started);

    /// <summary>
    /// The aggregates record points for and against but not who the opponent was, and
    /// head-to-head history is the backbone of every rivalry claim on the site. Pull the real
    /// pairings from the season's own league rather than inferring them from matching scores.
    /// </summary>
    private static async Task<List<Game>> LoadGamesAsync(ISleeperClient client, SeasonAggregate aggregate)
    {
        var games = new List<Game>();
        var lastWeek = Math.Max(aggregate.Schedule.TotalWeeks, 1);

        for (int week = 1; week <= lastWeek; week++)
        {
            var matchups = await client.GetLeagueMatchupsAsync(aggregate.LeagueId, week);
            if (matchups.Count == 0) break;

            foreach (var pair in matchups.Where(m => m.MatchupId is not null).GroupBy(m => m.MatchupId))
            {
                var sides = pair.ToList();
                if (sides.Count != 2) continue;

                var a = sides[0];
                var b = sides[1];
                var aPoints = a.Points ?? 0m;
                var bPoints = b.Points ?? 0m;
                if (aPoints == 0m && bPoints == 0m) continue;

                games.Add(new Game(week, a.RosterId, b.RosterId, aPoints, bPoints,
                    week > aggregate.Schedule.RegularSeasonLastWeek));
            }
        }

        return games;
    }

    private static object BuildSeason(SeasonBundle bundle)
    {
        var aggregate = bundle.Aggregate;
        var outcome = aggregate.Outcome;

        return new
        {
            season = bundle.Year,
            league_id = aggregate.LeagueId,
            league_name = aggregate.LeagueName,
            schedule = new
            {
                regular_season_last_week = aggregate.Schedule.RegularSeasonLastWeek,
                playoff_start_week = aggregate.Schedule.PlayoffStartWeek,
                championship_week = aggregate.Schedule.ChampionshipWeek,
                total_weeks = aggregate.Schedule.TotalWeeks
            },
            champion = outcome?.Champion is null ? null : new
            {
                owner_name = outcome.Champion.OwnerRealName,
                team_name = outcome.Champion.TeamName
            },
            standings = aggregate.Teams
                .OrderBy(t => t.FinalPlace ?? int.MaxValue)
                .ThenByDescending(t => t.RegularSeasonWins)
                .Select(t => new
                {
                    franchise_id = t.RosterId,
                    owner_name = t.OwnerRealName,
                    team_name = t.FinalTeamName,
                    final_place = t.FinalPlace,
                    wins = t.RegularSeasonWins,
                    losses = t.RegularSeasonLosses,
                    ties = t.RegularSeasonTies,
                    points_for = t.RegularSeasonPointsFor,
                    points_against = t.RegularSeasonPointsAgainst,
                    total_wins = t.TotalWins,
                    total_losses = t.TotalLosses,
                    high_score = t.SeasonHighScore,
                    high_score_week = t.SeasonHighScoreWeek,
                    low_score = t.SeasonLowScore,
                    low_score_week = t.SeasonLowScoreWeek,
                    longest_win_streak = t.LongestWinStreak,
                    longest_loss_streak = t.LongestLossStreak,
                    next_year_draft_pick = t.NextYearDraftPick
                })
                .ToList(),
            weeks = bundle.Games
                .GroupBy(g => g.Week)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    week = g.Key,
                    is_playoff = g.First().IsPlayoff,
                    games = g.Select(game => new
                    {
                        home = new
                        {
                            franchise_id = game.HomeRosterId,
                            owner_name = OwnerFor(bundle, game.HomeRosterId),
                            team_name = TeamNameFor(bundle, game.HomeRosterId, g.Key),
                            points = game.HomePoints
                        },
                        away = new
                        {
                            franchise_id = game.AwayRosterId,
                            owner_name = OwnerFor(bundle, game.AwayRosterId),
                            team_name = TeamNameFor(bundle, game.AwayRosterId, g.Key),
                            points = game.AwayPoints
                        },
                        margin = Math.Abs(game.HomePoints - game.AwayPoints)
                    }).ToList()
                })
                .ToList(),
            weekly_champions = aggregate.WeeklyChampions.Select(w => new
            {
                week = w.Week,
                owner_name = w.OwnerRealName,
                team_name = w.TeamName,
                points_for = w.PointsFor
            }).ToList(),
            highlights = aggregate.Highlights,
            awards = bundle.Awards?.Awards.Select(a => new
            {
                name = a.Name,
                description = a.Description,
                // Game-level awards name the team but not the owner, so the owner is resolved
                // from the season roster. UserId is the stable key and is tried first; team
                // names are mutable, so matching on them is only a fallback.
                owner_name = RequireAwardOwner(bundle, a),
                team_name = a.TeamName,
                player_name = a.PlayerName,
                metric = a.Metric,
                value = a.Value,
                week = a.Week,
                citation = a.Citation
            }).ToList()
        };
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// Resolves the owner an award belongs to, and refuses to emit a null the site would
    /// crash on. The site types <c>owner_name</c> as required and slugs it at build time,
    /// so a silent null here fails the Astro build with no indication of which award is at
    /// fault. Failing here instead names the season and the award.
    /// </summary>
    private static string RequireAwardOwner(SeasonBundle bundle, SeasonAward award)
    {
        var resolved = FirstNonBlank(
            award.OwnerRealName,
            OwnerForUserId(bundle, award.UserId),
            OwnerForTeamName(bundle, award.TeamName));

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(
                $"{bundle.Aggregate.Season} award '{award.Name}' has no resolvable owner " +
                $"(user_id: {award.UserId ?? "none"}, team: {award.TeamName ?? "none"}). " +
                "Regenerate the season sidecars before building site data.");
        }

        return resolved;
    }

    private static string? OwnerForUserId(SeasonBundle bundle, string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        return bundle.Aggregate.Teams
            .FirstOrDefault(t => string.Equals(t.UserId, userId, StringComparison.Ordinal))
            ?.OwnerRealName;
    }

    private static string? OwnerForTeamName(SeasonBundle bundle, string? teamName)
    {
        if (string.IsNullOrWhiteSpace(teamName)) return null;

        foreach (var team in bundle.Aggregate.Teams)
        {
            if (string.Equals(team.FinalTeamName, teamName, StringComparison.OrdinalIgnoreCase))
                return team.OwnerRealName;

            if (team.Weekly.Any(w => string.Equals(w.CurrentTeamName, teamName, StringComparison.OrdinalIgnoreCase)))
                return team.OwnerRealName;
        }

        return null;
    }

    private static string OwnerFor(SeasonBundle bundle, int rosterId)
        => bundle.Aggregate.Teams.FirstOrDefault(t => t.RosterId == rosterId)?.OwnerRealName ?? $"Roster {rosterId}";

    private static string TeamNameFor(SeasonBundle bundle, int rosterId, int week)
    {
        var team = bundle.Aggregate.Teams.FirstOrDefault(t => t.RosterId == rosterId);
        if (team is null) return $"Roster {rosterId}";

        var atWeek = team.Weekly.FirstOrDefault(w => w.Week == week)?.CurrentTeamName;
        return string.IsNullOrWhiteSpace(atWeek) ? team.FinalTeamName : atWeek;
    }

    /// <summary>
    /// Who holds each franchise right now. This cannot be inferred from results: the most
    /// recent completed season is the season an outgoing owner played, so treating "played
    /// last season" as "active" would keep a departed owner listed as current and hide the
    /// incoming one entirely. The upcoming season's export is the only source that knows.
    /// </summary>
    private static Dictionary<int, CurrentTeam> LoadCurrentOwners(string recapsRoot)
    {
        var result = new Dictionary<int, CurrentTeam>();

        var exports = Directory
            .EnumerateFiles(recapsRoot, "export.json", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        if (exports.Count == 0) return result;

        using var doc = JsonDocument.Parse(File.ReadAllText(exports[^1]));
        if (!doc.RootElement.TryGetProperty("teams", out var teams)) return result;

        foreach (var team in teams.EnumerateArray())
        {
            if (!team.TryGetProperty("roster_id", out var rosterId)) continue;
            if (!team.TryGetProperty("owner_name", out var ownerName)) continue;

            var name = ownerName.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Owners rename their teams every year, so the name in the current export is the
            // only one that is actually current. The newest played season is already history.
            var teamName = team.TryGetProperty("team_name", out var t) ? t.GetString()?.Trim() : null;
            result[rosterId.GetInt32()] = new CurrentTeam(name, string.IsNullOrWhiteSpace(teamName) ? null : teamName);
        }

        return result;
    }

    private sealed record CurrentTeam(string OwnerName, string? TeamName);

    /// <summary>A franchise is the roster slot. It outlives the person holding it.</summary>
    private static List<object> BuildFranchises(List<SeasonBundle> seasons, Dictionary<int, CurrentTeam> currentOwners)
    {
        var rosterIds = seasons
            .SelectMany(s => s.Aggregate.Teams.Select(t => t.RosterId))
            .Concat(currentOwners.Keys)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        return rosterIds.Select(rosterId =>
        {
            var timeline = seasons
                .Select(s => new { Season = s, Team = s.Aggregate.Teams.FirstOrDefault(t => t.RosterId == rosterId) })
                .Where(x => x.Team is not null)
                .Select(x => new
                {
                    season = x.Season.Year,
                    owner_name = x.Team!.OwnerRealName,
                    team_name = x.Team.FinalTeamName,
                    wins = x.Team.RegularSeasonWins,
                    losses = x.Team.RegularSeasonLosses,
                    ties = x.Team.RegularSeasonTies,
                    points_for = x.Team.RegularSeasonPointsFor,
                    points_against = x.Team.RegularSeasonPointsAgainst,
                    final_place = x.Team.FinalPlace
                })
                .OrderBy(x => x.season)
                .ToList();

            var owners = timeline.Select(t => t.owner_name).Distinct().ToList();
            currentOwners.TryGetValue(rosterId, out var current);

            // The site types current_owner_name as required and slugs it into a route at
            // build time, so a null here fails the Astro build with no hint of the cause.
            // The current export is the only authority on who holds a slot, so if it does
            // not cover a franchise, say which one and stop.
            if (current is null)
            {
                throw new InvalidOperationException(
                    $"Franchise {rosterId} has no current owner in the export. " +
                    "Run 'export' for the current season before building site data.");
            }

            return (object)new
            {
                franchise_id = rosterId,
                current_team_name = FirstNonBlank(current.TeamName, timeline.LastOrDefault()?.team_name),
                current_owner_name = current.OwnerName,
                // An owner appears here once, in the order he held the slot, so a handoff
                // reads as a handoff rather than as two unrelated teams.
                owner_history = owners
                    .Concat(new[] { current.OwnerName })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                timeline,
                championships = timeline.Count(t => t.final_place == 1)
            };
        }).ToList();
    }

    /// <summary>
    /// Owners aggregate only their own seasons. A person who left the league keeps the record
    /// he earned and is marked inactive rather than being folded into whoever took the slot.
    /// An incoming owner appears with an empty record rather than inheriting one.
    /// </summary>
    private static List<OwnerEntry> BuildOwners(
        List<SeasonBundle> seasons,
        Dictionary<int, CurrentTeam> currentOwners,
        CurrentSeasonResults? currentResults)
    {
        var activeNames = new HashSet<string>(currentOwners.Values.Select(v => v.OwnerName), StringComparer.OrdinalIgnoreCase);

        var ownerSeasons = BuildRecordTeams(seasons, currentResults)
            .GroupBy(x => x.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = ownerSeasons.Select(group =>
        {
            var entries = group.OrderBy(x => x.Year).ToList();

            return new OwnerEntry(
                group.Key,
                activeNames.Contains(group.Key),
                entries[0].Year,
                entries[^1].Year,
                entries.Select(e => e.RosterId).Distinct().OrderBy(x => x).ToList(),
                entries.Select(e => (object)new
                {
                    season = e.Year,
                    team_name = e.TeamName,
                    wins = e.Wins,
                    losses = e.Losses,
                    ties = e.Ties,
                    points_for = e.PointsFor,
                    final_place = e.FinalPlace
                }).ToList(),
                BuildOwnerCareer(entries));
        }).ToList();

        var known = new HashSet<string>(ownerSeasons.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var (rosterId, current) in currentOwners.OrderBy(kv => kv.Key))
        {
            if (known.Contains(current.OwnerName)) continue;

            result.Add(new OwnerEntry(
                current.OwnerName,
                Active: true,
                FirstSeason: null,
                LastSeason: null,
                Franchises: [rosterId],
                Seasons: [],
                Career: new OwnerCareer(0, 0, 0, 0, 0m, 0m, 0, null, null)));
        }

        return result.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static OwnerCareer BuildOwnerCareer(List<RecordTeam> entries)
    {
        var finishes = entries
            .Where(entry => entry.FinalPlace.HasValue)
            .Select(entry => entry.FinalPlace!.Value)
            .ToList();

        return new OwnerCareer(
            entries.Count,
            entries.Sum(entry => entry.Wins),
            entries.Sum(entry => entry.Losses),
            entries.Sum(entry => entry.Ties),
            Math.Round(entries.Sum(entry => entry.PointsFor), 2),
            Math.Round(entries.Sum(entry => entry.PointsAgainst), 2),
            entries.Count(entry => entry.FinalPlace == 1),
            finishes.Count == 0 ? null : finishes.Min(),
            finishes.Count == 0 ? null : finishes.Max());
    }

    internal sealed record OwnerEntry(
        string Name,
        bool Active,
        int? FirstSeason,
        int? LastSeason,
        List<int> Franchises,
        List<object> Seasons,
        OwnerCareer Career);

    internal sealed record OwnerCareer(
        int SeasonsPlayed,
        int Wins,
        int Losses,
        int Ties,
        decimal PointsFor,
        decimal PointsAgainst,
        int Championships,
        int? BestFinish,
        int? WorstFinish);

    private static List<RecordTeam> BuildRecordTeams(
        List<SeasonBundle> seasons,
        CurrentSeasonResults? currentResults)
    {
        var teams = seasons
            .SelectMany(season => season.Aggregate.Teams.Select(team => new RecordTeam(
                season.Year,
                team.RosterId,
                team.OwnerRealName,
                team.FinalTeamName,
                team.RegularSeasonWins,
                team.RegularSeasonLosses,
                team.RegularSeasonTies,
                team.RegularSeasonPointsFor,
                team.RegularSeasonPointsAgainst,
                team.FinalPlace)))
            .ToList();

        if (currentResults is not null)
            teams.AddRange(currentResults.Teams);

        return teams;
    }

    private static List<object> BuildAllTime(List<SeasonBundle> seasons, CurrentSeasonResults? currentResults)
        => BuildRecordTeams(seasons, currentResults)
            .GroupBy(team => team.OwnerName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                owner_name = group.Key,
                wins = group.Sum(team => team.Wins),
                losses = group.Sum(team => team.Losses),
                ties = group.Sum(team => team.Ties),
                points_for = Math.Round(group.Sum(team => team.PointsFor), 2),
                points_against = Math.Round(group.Sum(team => team.PointsAgainst), 2),
                championships = group.Count(team => team.FinalPlace == 1)
            })
            .OrderByDescending(entry => entry.wins)
            .ThenByDescending(entry => entry.points_for)
            .Cast<object>()
            .ToList();

    private static List<object> BuildSingleWeekExtremes(
        List<SeasonBundle> seasons,
        CurrentSeasonResults? currentResults,
        bool best)
    {
        var all = seasons.SelectMany(season => season.Aggregate.Teams.SelectMany(team => team.Weekly
            .Where(week => week.PointsFor > 0m)
            .Select(week => new RecordWeek(
                season.Year,
                week.Week,
                team.OwnerRealName,
                week.CurrentTeamName ?? team.FinalTeamName,
                week.PointsFor))))
            .ToList();

        if (currentResults is not null)
            all.AddRange(currentResults.Weeks);

        var ordered = best
            ? all.OrderByDescending(entry => entry.Points)
            : all.OrderBy(entry => entry.Points);

        return ordered.Take(10).Select(entry => (object)new
        {
            season = entry.Year,
            week = entry.Week,
            owner_name = entry.OwnerName,
            team_name = entry.TeamName,
            points = entry.Points
        }).ToList();
    }

    /// <summary>
    /// Every owner pairing across every season, so a rivalry claim on the site is backed by
    /// the actual series rather than by a writer's memory of it.
    /// </summary>
    private static List<object> BuildHeadToHead(List<SeasonBundle> seasons, CurrentSeasonResults? currentResults)
    {
        var records = new Dictionary<(string A, string B), H2H>();

        foreach (var bundle in seasons)
        {
            foreach (var game in bundle.Games)
            {
                var home = OwnerFor(bundle, game.HomeRosterId);
                var away = OwnerFor(bundle, game.AwayRosterId);
                if (string.Equals(home, away, StringComparison.OrdinalIgnoreCase)) continue;

                Record(home, away, game.HomePoints, game.AwayPoints);
                Record(away, home, game.AwayPoints, game.HomePoints);
            }
        }

        if (currentResults is not null)
        {
            foreach (var game in currentResults.Games)
            {
                if (string.Equals(game.HomeOwnerName, game.AwayOwnerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                Record(game.HomeOwnerName, game.AwayOwnerName, game.HomePoints, game.AwayPoints);
                Record(game.AwayOwnerName, game.HomeOwnerName, game.AwayPoints, game.HomePoints);
            }
        }

        void Record(string owner, string opponent, decimal pointsFor, decimal pointsAgainst)
        {
            var key = (owner, opponent);
            if (!records.TryGetValue(key, out var entry))
            {
                entry = new H2H();
                records[key] = entry;
            }

            entry.PointsFor += pointsFor;
            entry.PointsAgainst += pointsAgainst;
            if (pointsFor > pointsAgainst) entry.Wins++;
            else if (pointsFor < pointsAgainst) entry.Losses++;
            else entry.Ties++;
        }

        return records
            .OrderBy(kv => kv.Key.A, StringComparer.OrdinalIgnoreCase)
            .ThenBy(kv => kv.Key.B, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (object)new
            {
                owner_name = kv.Key.A,
                opponent_name = kv.Key.B,
                wins = kv.Value.Wins,
                losses = kv.Value.Losses,
                ties = kv.Value.Ties,
                points_for = Math.Round(kv.Value.PointsFor, 2),
                points_against = Math.Round(kv.Value.PointsAgainst, 2)
            })
            .ToList();
    }

    private static List<object> BuildArticleIndex(string root, string recapsRoot)
        => Directory
            .EnumerateFiles(recapsRoot, "*.md", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .Select(file =>
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(file);
                var seasonText = Path.GetFileName(Path.GetDirectoryName(file)!);
                int.TryParse(seasonText, out var season);

                var (kind, week) = ClassifyArticle(name);

                return (object)new
                {
                    season,
                    week,
                    kind,
                    path = relative,
                    title = ReadTitle(file) ?? name
                };
            })
            .ToList();

    internal static (string Kind, int? Week) ClassifyArticle(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^week-(\d{2})(-preview|-what-to-watch)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var suffix = match.Groups[2].Value;
            var kind = suffix switch
            {
                "-preview" => "weekly-preview",
                "-what-to-watch" => "monday-watch",
                _ => "weekly-recap"
            };
            return (kind, int.Parse(match.Groups[1].Value));
        }

        return (name switch
        {
            "season" => "season-review",
            "draft" => "draft-recap",
            "preview" => "season-preview",
            _ => "article"
        }, null);
    }

    internal static JsonElement? LoadWeeklyEdition(string recapsRoot)
    {
        var path = Directory.EnumerateFiles(recapsRoot, "week-??-data.json", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.Ordinal).LastOrDefault();
        if (path is null) return null;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var edition = document.RootElement;
        var completedWeek = edition.GetProperty("completed_week").GetInt32();
        var teams = edition.GetProperty("teams").EnumerateArray().ToList();
        if (completedWeek < 1 || completedWeek > 17
            || edition.GetProperty("next_week").GetInt32() != completedWeek + 1
            || teams.Count == 0
            || teams.Any(team => string.IsNullOrWhiteSpace(team.GetProperty("owner_name").GetString())
                || string.IsNullOrWhiteSpace(team.GetProperty("team_name").GetString())))
            throw new InvalidOperationException($"Invalid weekly edition: {path}");

        return edition.Clone();
    }

    private static int ReadEditionSeason(JsonElement edition)
    {
        var season = edition.GetProperty("season");
        return season.ValueKind == JsonValueKind.Number
            ? season.GetInt32()
            : int.Parse(season.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CurrentSeasonResults ParseCurrentSeason(JsonElement edition)
    {
        var year = ReadEditionSeason(edition);
        var completedWeek = edition.GetProperty("completed_week").GetInt32();
        var teams = edition.GetProperty("teams").EnumerateArray()
            .Select(team => new RecordTeam(
                year,
                team.GetProperty("franchise_id").GetInt32(),
                team.GetProperty("owner_name").GetString()!,
                team.GetProperty("team_name").GetString()!,
                team.GetProperty("wins").GetInt32(),
                team.GetProperty("losses").GetInt32(),
                team.GetProperty("ties").GetInt32(),
                team.GetProperty("points_for").GetDecimal(),
                team.GetProperty("points_against").GetDecimal(),
                FinalPlace: null))
            .ToList();

        var games = new List<CurrentGame>();
        var weeks = new List<RecordWeek>();
        foreach (var weekElement in edition.GetProperty("weeks").EnumerateArray())
        {
            var week = weekElement.GetProperty("week").GetInt32();
            if (week > completedWeek) continue;

            foreach (var gameElement in weekElement.GetProperty("games").EnumerateArray())
            {
                var home = gameElement.GetProperty("home");
                var away = gameElement.GetProperty("away");
                var homeOwner = home.GetProperty("owner_name").GetString()!;
                var awayOwner = away.GetProperty("owner_name").GetString()!;
                var homeTeam = home.GetProperty("team_name").GetString()!;
                var awayTeam = away.GetProperty("team_name").GetString()!;
                var homePoints = home.GetProperty("points").GetDecimal();
                var awayPoints = away.GetProperty("points").GetDecimal();

                games.Add(new CurrentGame(homeOwner, awayOwner, homePoints, awayPoints));
                if (homePoints > 0m)
                    weeks.Add(new RecordWeek(year, week, homeOwner, homeTeam, homePoints));
                if (awayPoints > 0m)
                    weeks.Add(new RecordWeek(year, week, awayOwner, awayTeam, awayPoints));
            }
        }

        return new CurrentSeasonResults(year, completedWeek, teams, weeks, games);
    }

    internal static JsonObject BuildPublicWeeklyEdition(JsonElement edition)
    {
        var projected = JsonNode.Parse(edition.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("Weekly edition could not be projected.");
        var level = 0;
        if (edition.TryGetProperty("_narrative", out var narrative)
            && narrative.TryGetProperty("sentience_level", out var levelElement))
        {
            level = levelElement.GetInt32();
        }

        projected.Remove("_narrative");
        projected.Remove("editorial_cues");

        var cues = NarrativePolicy.ForLevel(level).PublicCues;
        if (cues is not null)
        {
            projected["editorial_cues"] = JsonSerializer.SerializeToNode(
                cues,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
        }

        return projected;
    }

    private static string? ReadTitle(string path)
    {
        foreach (var line in File.ReadLines(path).Take(20))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                return trimmed[2..].Trim();
        }
        return null;
    }

    /// <summary>
    /// The upcoming season has no results yet, so it contributes its draft rather than a
    /// standings table. Keeper counts and the rounds they cost are the part the site needs.
    /// </summary>
    private static object? BuildDraftSummary(string recapsRoot)
    {
        var exports = Directory
            .EnumerateFiles(recapsRoot, "export.json", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();

        if (exports.Count == 0) return null;

        var latest = exports[^1];
        using var doc = JsonDocument.Parse(File.ReadAllText(latest));
        var rootElement = doc.RootElement;

        if (!rootElement.TryGetProperty("keeper_summary", out var keeperSummary)) return null;

        // export.json is written in snake_case, unlike the PascalCase season sidecars, so it
        // needs its own naming policy. Reading it case-insensitively silently produced a
        // summary of zeroes instead of failing, so the result is checked rather than trusted.
        var exportOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        var entries = JsonSerializer.Deserialize<List<KeeperSummaryEntry>>(keeperSummary.GetRawText(), exportOptions)
            ?? new List<KeeperSummaryEntry>();

        if (entries.Count > 0 && entries.All(e => e.RosterId == 0))
            throw new InvalidOperationException(
                $"Keeper summary in {latest} did not deserialize; every roster_id is 0. The export schema likely changed.");

        return new
        {
            season = rootElement.TryGetProperty("season", out var s) ? s.GetString() : null,
            keeper_summary = entries,
            verification = rootElement.TryGetProperty("keeper_verification", out var v)
                ? JsonSerializer.Deserialize<List<string>>(v.GetRawText(), exportOptions)
                : null
        };
    }

    private sealed record KeeperSummaryEntry(int RosterId, string? OwnerName, int KeeperCount, List<int>? RoundsSpent);

    private sealed record RecordTeam(
        int Year,
        int RosterId,
        string OwnerName,
        string TeamName,
        int Wins,
        int Losses,
        int Ties,
        decimal PointsFor,
        decimal PointsAgainst,
        int? FinalPlace);

    private sealed record RecordWeek(
        int Year,
        int Week,
        string OwnerName,
        string TeamName,
        decimal Points);

    private sealed record CurrentGame(
        string HomeOwnerName,
        string AwayOwnerName,
        decimal HomePoints,
        decimal AwayPoints);

    private sealed record CurrentSeasonResults(
        int Year,
        int CompletedWeek,
        List<RecordTeam> Teams,
        List<RecordWeek> Weeks,
        List<CurrentGame> Games);

    private sealed record SeasonBundle(int Year, SeasonAggregate Aggregate, SeasonAwards? Awards, List<Game> Games);

    private sealed record Game(
        int Week,
        int HomeRosterId,
        int AwayRosterId,
        decimal HomePoints,
        decimal AwayPoints,
        bool IsPlayoff);

    private sealed class H2H
    {
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Ties { get; set; }
        public decimal PointsFor { get; set; }
        public decimal PointsAgainst { get; set; }
    }
}
