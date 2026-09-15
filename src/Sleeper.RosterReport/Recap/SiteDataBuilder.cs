using System.Text.Json;
using Sleeper.Api;

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
    public static async Task<int> RunAsync(ISleeperClient client)
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
                Console.WriteLine($"  {year}: no season-aggregate.json, skipping results.");
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
        var franchises = BuildFranchises(seasons, currentOwners);
        var owners = BuildOwners(seasons, currentOwners);
        var headToHead = BuildHeadToHead(seasons);

        var doc = new
        {
            generated_at_utc = DateTime.UtcNow.ToString("o"),
            league_name = leagueName,
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
                    "Records cover the seasons in this archive, 2024 onward, and the regular season only.",
                    "Head-to-head counts every game two owners have played, playoffs and consolation included.",
                    "Records are per owner. A franchise that changed hands does not transfer its record."
                },
                all_time = BuildAllTime(seasons),
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
                single_week_highs = BuildSingleWeekExtremes(seasons, best: true),
                single_week_lows = BuildSingleWeekExtremes(seasons, best: false),
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
    private static List<OwnerEntry> BuildOwners(List<SeasonBundle> seasons, Dictionary<int, CurrentTeam> currentOwners)
    {
        var activeNames = new HashSet<string>(currentOwners.Values.Select(v => v.OwnerName), StringComparer.OrdinalIgnoreCase);

        var ownerSeasons = seasons
            .SelectMany(s => s.Aggregate.Teams.Select(t => new { s.Year, Team = t }))
            .GroupBy(x => x.Team.OwnerRealName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = ownerSeasons.Select(group =>
        {
            var entries = group.OrderBy(x => x.Year).ToList();

            return new OwnerEntry(
                group.Key,
                activeNames.Contains(group.Key),
                entries[0].Year,
                entries[^1].Year,
                entries.Select(e => e.Team.RosterId).Distinct().OrderBy(x => x).ToList(),
                entries.Select(e => (object)new
                {
                    season = e.Year,
                    team_name = e.Team.FinalTeamName,
                    wins = e.Team.RegularSeasonWins,
                    losses = e.Team.RegularSeasonLosses,
                    ties = e.Team.RegularSeasonTies,
                    points_for = e.Team.RegularSeasonPointsFor,
                    final_place = e.Team.FinalPlace
                }).ToList(),
                new OwnerCareer(
                    entries.Count,
                    entries.Sum(e => e.Team.RegularSeasonWins),
                    entries.Sum(e => e.Team.RegularSeasonLosses),
                    entries.Sum(e => e.Team.RegularSeasonTies),
                    Math.Round(entries.Sum(e => e.Team.RegularSeasonPointsFor), 2),
                    Math.Round(entries.Sum(e => e.Team.RegularSeasonPointsAgainst), 2),
                    entries.Count(e => e.Team.FinalPlace == 1),
                    entries.Where(e => e.Team.FinalPlace.HasValue).Select(e => e.Team.FinalPlace).Min(),
                    entries.Where(e => e.Team.FinalPlace.HasValue).Select(e => e.Team.FinalPlace).Max()));
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

    private static List<object> BuildAllTime(List<SeasonBundle> seasons)
        => seasons
            .SelectMany(s => s.Aggregate.Teams)
            .GroupBy(t => t.OwnerRealName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                owner_name = g.Key,
                wins = g.Sum(t => t.RegularSeasonWins),
                losses = g.Sum(t => t.RegularSeasonLosses),
                ties = g.Sum(t => t.RegularSeasonTies),
                points_for = Math.Round(g.Sum(t => t.RegularSeasonPointsFor), 2),
                points_against = Math.Round(g.Sum(t => t.RegularSeasonPointsAgainst), 2),
                championships = g.Count(t => t.FinalPlace == 1)
            })
            .OrderByDescending(x => x.wins)
            .ThenByDescending(x => x.points_for)
            .Cast<object>()
            .ToList();

    private static List<object> BuildSingleWeekExtremes(List<SeasonBundle> seasons, bool best)
    {
        var all = seasons.SelectMany(s => s.Aggregate.Teams.SelectMany(t => t.Weekly
            .Where(w => w.PointsFor > 0m)
            .Select(w => new
            {
                season = s.Year,
                week = w.Week,
                owner_name = t.OwnerRealName,
                team_name = w.CurrentTeamName ?? t.FinalTeamName,
                points = w.PointsFor
            })));

        var ordered = best
            ? all.OrderByDescending(x => x.points)
            : all.OrderBy(x => x.points);

        return ordered.Take(10).Cast<object>().ToList();
    }

    /// <summary>
    /// Every owner pairing across every season, so a rivalry claim on the site is backed by
    /// the actual series rather than by a writer's memory of it.
    /// </summary>
    private static List<object> BuildHeadToHead(List<SeasonBundle> seasons)
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

                var kind = name switch
                {
                    "season" => "season-review",
                    "draft" => "draft-recap",
                    "preview" => "season-preview",
                    _ when name.StartsWith("week-", StringComparison.OrdinalIgnoreCase) => "weekly-recap",
                    _ => "article"
                };

                int? week = null;
                if (kind == "weekly-recap" && int.TryParse(name[5..], out var parsedWeek)) week = parsedWeek;

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
