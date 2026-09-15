using System.Text.Json;
using Sleeper.Api;
using Sleeper.Api.Models;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Dumps one season's draft board, keepers, rosters, prior-season production, and schedule
/// as a single JSON file. This is a data dump, not an analysis engine: it grades nothing and
/// ranks nothing, so every judgement made downstream can be traced back to a number in here.
/// </summary>
internal static class SeasonExporter
{
    public static async Task<int> RunAsync(ISleeperClient client, int season, string leagueId)
    {
        var league = await client.GetLeagueAsync(leagueId);
        if (league is null)
        {
            Console.WriteLine($"  League {leagueId} not found.");
            return 1;
        }

        // Same trap as the recap command: --season only labels the output. Exporting the wrong
        // league under a requested season silently mislabels every pick and keeper in the file.
        if (!string.Equals(league.Season, season.ToString(), StringComparison.Ordinal))
        {
            Console.WriteLine(
                $"  Error: League {leagueId} is season {league.Season}, but --season {season} was requested. " +
                "Pass the league ID for that season instead: --league-id <id>.");
            return 1;
        }

        var users = await client.GetLeagueUsersAsync(leagueId);
        var rosters = await client.GetLeagueRostersAsync(leagueId);
        var players = await client.GetAllPlayersAsync();

        var lore = LeagueLore.TryLoadLayers(RecapPaths.LegacyLorePath, RecapPaths.LoreDirectory, season)
            ?? LeagueLore.ParseFrom("");

        string OwnerName(int rosterId)
        {
            var roster = rosters.FirstOrDefault(r => r.RosterId == rosterId);
            var user = users.FirstOrDefault(u => u.UserId == roster?.OwnerId);
            var handle = user?.Username ?? user?.DisplayName ?? "";
            return lore.OwnersByUsername.TryGetValue(handle.ToLowerInvariant(), out var owner)
                && !string.IsNullOrWhiteSpace(owner?.Name)
                ? owner!.Name
                : $"Roster {rosterId}";
        }

        string TeamName(int rosterId)
        {
            var roster = rosters.FirstOrDefault(r => r.RosterId == rosterId);
            var user = users.FirstOrDefault(u => u.UserId == roster?.OwnerId);
            return user?.Metadata is not null
                && user.Metadata.TryGetValue("team_name", out var tn)
                && !string.IsNullOrWhiteSpace(tn)
                ? tn
                : $"Roster {rosterId}";
        }

        Console.WriteLine($"  Exporting {season} for {rosters.Count} teams...");

        var draft = await ResolveDraftAsync(client, league);
        var picks = draft is null ? [] : await client.GetDraftPicksAsync(draft.DraftId);
        var draftTradedPicks = draft is null ? [] : await client.GetDraftTradedPicksAsync(draft.DraftId);
        Console.WriteLine($"  Draft {draft?.DraftId ?? "(none)"}: {picks.Count} picks, " +
                          $"{picks.Count(p => p.IsKeeper == true)} flagged as keepers.");

        var production = await BuildPriorSeasonProductionAsync(client, league);
        var schedule = await BuildScheduleAsync(client, leagueId);

        var keeperCheck = VerifyKeepers(picks, production.RosterByPlayerId);
        foreach (var warning in keeperCheck)
            Console.WriteLine($"  [keeper check] {warning}");

        var doc = new
        {
            season = season.ToString(),
            league_id = leagueId,
            league_name = FirstNonBlank(lore.League.Name, "The League"),
            generated_at_utc = DateTime.UtcNow.ToString("o"),
            notes = new[]
            {
                "Raw export. Nothing here is graded or ranked.",
                "market_rank is Sleeper's search_rank, a popularity-derived market proxy. It is not a sourced ADP.",
                "Keepers cost the round they are listed in, so a keeper is never a late-round steal.",
                "prior_season production is league-scored from weekly matchup data, not from a projection source."
            },
            roster_positions = league.RosterPositions,
            scoring_settings = league.ScoringSettings,
            teams = rosters.OrderBy(r => r.RosterId).Select(r => new
            {
                roster_id = r.RosterId,
                owner_name = OwnerName(r.RosterId),
                team_name = TeamName(r.RosterId),
                keepers = picks
                    .Where(p => p.IsKeeper == true && p.RosterId == r.RosterId)
                    .OrderBy(p => p.Round)
                    .Select(p => DescribePick(p, players, production))
                    .ToList(),
                drafted = picks
                    .Where(p => p.IsKeeper != true && p.RosterId == r.RosterId)
                    .OrderBy(p => p.PickNo)
                    .Select(p => DescribePick(p, players, production))
                    .ToList(),
                roster = (r.Players ?? []).Select(pid => DescribePlayer(pid, players, production)).ToList()
            }).ToList(),
            keeper_summary = rosters.OrderBy(r => r.RosterId).Select(r => new
            {
                roster_id = r.RosterId,
                owner_name = OwnerName(r.RosterId),
                keeper_count = picks.Count(p => p.IsKeeper == true && p.RosterId == r.RosterId),
                rounds_spent = picks.Where(p => p.IsKeeper == true && p.RosterId == r.RosterId)
                    .Select(p => p.Round).OrderBy(x => x).ToList()
            }).ToList(),
            keeper_verification = keeperCheck,
            draft_board = picks.OrderBy(p => p.PickNo).Select(p => new
            {
                pick_no = p.PickNo,
                round = p.Round,
                draft_slot = p.DraftSlot,
                roster_id = p.RosterId,
                owner_name = p.RosterId is { } rosterId ? OwnerName(rosterId) : null,
                is_keeper = p.IsKeeper == true,
                player = DescribePick(p, players, production)
            }).ToList(),
            traded_draft_picks = draftTradedPicks.Select(t => new
            {
                round = t.Round,
                from_roster_id = t.RosterId,
                previous_owner_roster_id = t.PreviousOwnerId,
                current_owner_roster_id = t.OwnerId
            }).ToList(),
            schedule
        };

        var outPath = Path.Combine(RecapPaths.RecapDir(season), "export.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }));

        Console.WriteLine($"  Wrote {outPath}");
        return 0;
    }

    private static async Task<Draft?> ResolveDraftAsync(ISleeperClient client, League league)
    {
        if (!string.IsNullOrWhiteSpace(league.DraftId))
        {
            var byId = await client.GetDraftAsync(league.DraftId);
            if (byId is not null) return byId;
        }

        var drafts = await client.GetLeagueDraftsAsync(league.LeagueId);
        return drafts.FirstOrDefault(d => string.Equals(d.Status, "complete", StringComparison.OrdinalIgnoreCase))
            ?? drafts.FirstOrDefault();
    }

    /// <summary>
    /// Every keeper should be a player who finished the prior season on that same franchise.
    /// A mismatch means either the flag is wrong or a roster slot changed hands, and either way
    /// it must be looked at by hand before any draft value is claimed.
    /// </summary>
    private static List<string> VerifyKeepers(
        IReadOnlyList<DraftPick> picks,
        IReadOnlyDictionary<string, int> priorRosterByPlayerId)
    {
        var warnings = new List<string>();
        var keepers = picks.Where(p => p.IsKeeper == true).ToList();

        if (keepers.Count == 0)
        {
            warnings.Add("No picks are flagged as keepers. If this league has keepers, the flag is unreliable " +
                         "and the board must be reconciled against the prior season's rosters by hand.");
            return warnings;
        }

        if (priorRosterByPlayerId.Count == 0)
        {
            warnings.Add("No prior-season roster data was available, so keeper flags could not be cross-checked.");
            return warnings;
        }

        foreach (var keeper in keepers.OrderBy(k => k.PickNo))
        {
            var name = $"{keeper.Metadata?.FirstName} {keeper.Metadata?.LastName}".Trim();
            if (keeper.PlayerId is null || !priorRosterByPlayerId.TryGetValue(keeper.PlayerId, out var priorRoster))
            {
                warnings.Add($"Keeper {name} (R{keeper.Round}, roster {keeper.RosterId}) was not on any roster at " +
                             "the end of the prior season.");
            }
            else if (priorRoster != keeper.RosterId)
            {
                warnings.Add($"Keeper {name} (R{keeper.Round}) is kept by roster {keeper.RosterId} but finished the " +
                             $"prior season on roster {priorRoster}.");
            }
        }

        if (warnings.Count == 0)
            warnings.Add($"All {keepers.Count} keepers were on the same franchise at the end of the prior season.");

        return warnings;
    }

    private static async Task<PriorSeasonProduction> BuildPriorSeasonProductionAsync(ISleeperClient client, League league)
    {
        var result = new PriorSeasonProduction();
        if (string.IsNullOrWhiteSpace(league.PreviousLeagueId))
            return result;

        var previous = await client.GetLeagueAsync(league.PreviousLeagueId);
        result.Season = previous?.Season;

        for (int week = 1; week <= 18; week++)
        {
            var matchups = await client.GetLeagueMatchupsAsync(league.PreviousLeagueId, week);
            if (matchups.Count == 0) break;
            if (matchups.All(m => (m.Points ?? 0m) == 0m && (m.Players is null || m.Players.Count == 0))) break;

            foreach (var matchup in matchups)
            {
                foreach (var playerId in matchup.Players ?? [])
                {
                    result.RosterByPlayerId[playerId] = matchup.RosterId;

                    var stat = result.Stats.TryGetValue(playerId, out var existing) ? existing : new PlayerProduction();
                    stat.WeeksRostered++;
                    if (matchup.Starters?.Contains(playerId) == true) stat.WeeksStarted++;
                    if (matchup.PlayersPoints is not null && matchup.PlayersPoints.TryGetValue(playerId, out var pts))
                    {
                        stat.TotalPoints += pts;
                        if (pts != 0m) stat.WeeksScored++;
                        if (pts > stat.BestWeek) stat.BestWeek = pts;
                    }
                    result.Stats[playerId] = stat;
                }
            }
        }

        return result;
    }

    private static async Task<object> BuildScheduleAsync(ISleeperClient client, string leagueId)
    {
        var weeks = new List<object>();
        for (int week = 1; week <= 18; week++)
        {
            var matchups = await client.GetLeagueMatchupsAsync(leagueId, week);
            if (matchups.Count == 0) break;

            var pairings = matchups
                .Where(m => m.MatchupId is not null)
                .GroupBy(m => m.MatchupId)
                .Select(g => new
                {
                    matchup_id = g.Key,
                    roster_ids = g.Select(m => m.RosterId).OrderBy(x => x).ToList()
                })
                .OrderBy(p => p.matchup_id)
                .ToList();

            if (pairings.Count == 0) break;
            weeks.Add(new { week, matchups = pairings });
        }

        return weeks;
    }

    private static object DescribePick(
        DraftPick pick,
        IReadOnlyDictionary<string, Player> players,
        PriorSeasonProduction production)
    {
        var described = DescribePlayer(pick.PlayerId, players, production);
        return new
        {
            round = pick.Round,
            pick_no = pick.PickNo,
            is_keeper = pick.IsKeeper == true,
            player = described
        };
    }

    private static object DescribePlayer(
        string? playerId,
        IReadOnlyDictionary<string, Player> players,
        PriorSeasonProduction production)
    {
        if (playerId is null) return new { player_id = (string?)null, name = "(empty)" };

        players.TryGetValue(playerId, out var player);
        var stat = production.Stats.TryGetValue(playerId, out var s) ? s : null;

        return new
        {
            player_id = playerId,
            name = player?.FullName ?? $"{player?.FirstName} {player?.LastName}".Trim(),
            position = player?.Position,
            nfl_team = player?.Team,
            age = player?.Age,
            years_exp = player?.YearsExp,
            injury_status = player?.InjuryStatus,
            depth_chart_order = player?.DepthChartOrder,
            market_rank = player?.SearchRank,
            prior_season = stat is null ? null : new
            {
                season = production.Season,
                total_points = Math.Round(stat.TotalPoints, 2),
                weeks_rostered = stat.WeeksRostered,
                weeks_started = stat.WeeksStarted,
                weeks_scored = stat.WeeksScored,
                best_week = Math.Round(stat.BestWeek, 2),
                // Two denominators, because they answer different questions. Points per week
                // rostered includes byes and inactives; points per week scored measures the
                // player only when he actually played.
                points_per_week_rostered = stat.WeeksRostered == 0
                    ? 0m
                    : Math.Round(stat.TotalPoints / stat.WeeksRostered, 2),
                points_per_week_scored = stat.WeeksScored == 0
                    ? 0m
                    : Math.Round(stat.TotalPoints / stat.WeeksScored, 2)
            }
        };
    }

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private sealed class PriorSeasonProduction
    {
        public string? Season { get; set; }
        public Dictionary<string, PlayerProduction> Stats { get; } = new();
        public Dictionary<string, int> RosterByPlayerId { get; } = new();
    }

    private sealed class PlayerProduction
    {
        public decimal TotalPoints { get; set; }
        public int WeeksRostered { get; set; }
        public int WeeksStarted { get; set; }
        public int WeeksScored { get; set; }
        public decimal BestWeek { get; set; }
    }
}
