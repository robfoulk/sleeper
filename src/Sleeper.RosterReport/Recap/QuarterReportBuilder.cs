using Sleeper.Api.NflData;
using Sleeper.Api.NflData.Models;
using Sleeper.Api.NflData.Scoring;

namespace Sleeper.RosterReport.Recap;

internal static class QuarterReportBuilder
{
    private static readonly string[] PositionOrder = ["QB", "RB", "WR", "TE", "K", "DEF"];

    public static async Task<QuarterReport?> BuildAsync(
        INflDataClient nflData,
        int season,
        int completedWeek,
        Dictionary<string, decimal> scoringSettings,
        IEnumerable<QuarterDefenseWeekScore> defenseScores,
        IEnumerable<QuarterTeamWeekScore> teamScores,
        IEnumerable<QuarterLineupWeekScore> lineupScores,
        int firstWeek = 1,
        int lastWeek = 4,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstWeek, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastWeek, firstWeek);
        var throughWeek = Math.Min(completedWeek, lastWeek);
        if (throughWeek < firstWeek || scoringSettings.Count == 0)
            return null;

        var weeklyStats = await nflData.GetWeeklyStatsAsync(season, cancellationToken).ConfigureAwait(false);
        foreach (var week in Enumerable.Range(firstWeek, throughWeek - firstWeek + 1))
        {
            if (!weeklyStats.Any(stat => stat.Season == season && stat.Week == week
                && string.Equals(stat.SeasonType, "REG", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"NFL player stats are missing for {season} Week {week}; the quarter report cannot be completed.");
            }
        }
        return Build(season, throughWeek, scoringSettings, weeklyStats, defenseScores, teamScores, lineupScores,
            firstWeek, lastWeek);
    }

    internal static QuarterReport Build(
        int season,
        int throughWeek,
        Dictionary<string, decimal> scoringSettings,
        IEnumerable<WeeklyPlayerStats> allStats,
        IEnumerable<QuarterDefenseWeekScore>? defenseScores = null,
        IEnumerable<QuarterTeamWeekScore>? teamScores = null,
        IEnumerable<QuarterLineupWeekScore>? lineupScores = null,
        int firstWeek = 1,
        int lastWeek = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstWeek, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastWeek, firstWeek);
        throughWeek = Math.Clamp(throughWeek, firstWeek, lastWeek);
        var completedWeeks = throughWeek - firstWeek + 1;
        var scorer = new FantasyScorer(scoringSettings);
        var weeks = Enumerable.Range(firstWeek, lastWeek - firstWeek + 1).ToArray();
        var quarterTeamScores = (teamScores ?? [])
            .Where(score => score.Week >= firstWeek && score.Week <= throughWeek).ToList();
        var quarterLineupScores = (lineupScores ?? [])
            .Where(score => score.Week >= firstWeek && score.Week <= throughWeek).ToList();

        var players = allStats
            .Where(stat =>
                stat.Season == season
                && string.Equals(stat.SeasonType, "REG", StringComparison.OrdinalIgnoreCase)
                && stat.Week >= firstWeek
                && stat.Week <= throughWeek
                && stat.Position is not null
                && PositionOrder.Contains(stat.Position, StringComparer.OrdinalIgnoreCase))
            .GroupBy(stat => stat.PlayerId)
            .Select(group => BuildPlayer(group, scorer, weeks, completedWeeks))
            .Where(player => player.TotalPoints != 0m)
            .ToList();
        players.AddRange((defenseScores ?? [])
            .Where(score => score.Week >= firstWeek && score.Week <= throughWeek)
            .GroupBy(score => score.Team)
            .Select(group => BuildDefense(group, weeks, completedWeeks))
            .Where(player => player.TotalPoints != 0m));

        var positionReports = PositionOrder
            .Select(position =>
            {
                var ranked = players
                    .Where(player => player.Position == position)
                    .OrderByDescending(player => player.TotalPoints)
                    .ThenByDescending(player => player.PointsPerGame)
                    .ThenBy(player => player.PlayerName, StringComparer.Ordinal)
                    .Select((player, index) => player with { Rank = index + 1 })
                    .ToList();

                var topTen = ranked.Take(10).ToList();
                return new QuarterPositionReport(
                    Position: position,
                    PlayerCount: ranked.Count,
                    Leader: ranked.FirstOrDefault(),
                    TopTenAverage: topTen.Count == 0 ? 0m : Round(topTen.Average(player => player.TotalPoints)),
                    Rankings: ranked);
            })
            .Where(report => report.PlayerCount > 0)
            .ToList();

        var rankedPlayers = positionReports.SelectMany(report => report.Rankings).ToList();
        var overallLeaders = rankedPlayers
            .OrderByDescending(player => player.TotalPoints)
            .ThenByDescending(player => player.PointsPerGame)
            .Take(12)
            .ToList();

        var weeklyTotals = Enumerable.Range(firstWeek, completedWeeks)
            .Select(week =>
            {
                var weekIndex = week - firstWeek;
                var active = rankedPlayers.Where(player => player.WeeklyPoints[weekIndex] != 0m).ToList();
                return new QuarterWeek(
                    Week: week,
                    TotalPoints: Round(active.Sum(player => player.WeeklyPoints[weekIndex])),
                    PlayerCount: active.Count,
                    Leaders: PositionOrder
                        .Select(position => rankedPlayers
                            .Where(player => player.Position == position)
                            .OrderByDescending(player => player.WeeklyPoints[weekIndex])
                            .ThenBy(player => player.PlayerName, StringComparer.Ordinal)
                            .FirstOrDefault())
                        .Where(player => player is not null)
                        .Select(player => new QuarterWeekLeader(
                            player!.Position,
                            player.PlayerName,
                            player.Team,
                            player.WeeklyPoints[weekIndex]))
                        .ToList());
            })
            .ToList();

        var contenders = positionReports
            .SelectMany(report => report.Rankings.Take(10))
            .ToList();
        var biggestWeek = rankedPlayers
            .SelectMany(player => player.WeeklyPoints.Take(completedWeeks).Select((points, index) => new
            {
                Player = player,
                Week = index + firstWeek,
                Points = points
            }))
            .OrderByDescending(entry => entry.Points)
            .FirstOrDefault();
        var steadiest = contenders
            .Where(player => player.Games >= Math.Min(3, completedWeeks))
            .OrderBy(player => player.Consistency)
            .ThenByDescending(player => player.TotalPoints)
            .FirstOrDefault();
        var hottestFinish = contenders
            .Where(player => completedWeeks > 1)
            .OrderByDescending(player => player.Trend)
            .ThenByDescending(player => player.TotalPoints)
            .FirstOrDefault();
        var biggestMoment = biggestWeek is null
            ? null
            : new QuarterMoment(
                biggestWeek.Player.PlayerName,
                biggestWeek.Player.Position,
                biggestWeek.Player.Team,
                biggestWeek.Week,
                biggestWeek.Points);
        var teamPositionReports = BuildTeamPositionReports(quarterTeamScores, weeks, completedWeeks);
        var markGroups = BuildMarkGroups(
            positionReports,
            teamPositionReports,
            quarterTeamScores,
            quarterLineupScores,
            completedWeeks,
            biggestMoment);

        return new QuarterReport(
            Season: season,
            StartWeek: firstWeek,
            EndWeek: lastWeek,
            ThroughWeek: throughWeek,
            Status: throughWeek == lastWeek ? "complete" : "in-progress",
            Positions: positionReports.Select(report => report.Position).ToArray(),
            ScoringRules: scoringSettings
                .Where(rule => rule.Value != 0m)
                .OrderBy(rule => rule.Key, StringComparer.Ordinal)
                .Select(rule => new QuarterScoringRule(rule.Key, rule.Value))
                .ToList(),
            WeeklyTotals: weeklyTotals,
            OverallLeaders: overallLeaders,
            PositionReports: positionReports,
            TeamPositionReports: teamPositionReports,
            MarkGroups: markGroups,
            Superlatives: new QuarterSuperlatives(
                BiggestWeek: biggestMoment,
                Steadiest: steadiest,
                HottestFinish: hottestFinish));
    }

    private static List<QuarterMarkGroup> BuildMarkGroups(
        IReadOnlyList<QuarterPositionReport> positionReports,
        IReadOnlyList<QuarterTeamPositionReport> teamPositionReports,
        IEnumerable<QuarterTeamWeekScore> teamScores,
        IEnumerable<QuarterLineupWeekScore> lineupScores,
        int completedWeeks,
        QuarterMoment? biggestWeek)
    {
        var playerMarks = new List<QuarterMark>();
        if (biggestWeek is not null)
        {
            playerMarks.Add(new QuarterMark(
                "Highest Ceiling",
                biggestWeek.PlayerName,
                $"{biggestWeek.Points:0.0}",
                $"{biggestWeek.Position} · Week {biggestWeek.Week}",
                biggestWeek.Position));
        }

        var floorLeader = positionReports
            .SelectMany(report => report.Rankings.Take(5))
            .Where(player => player.Games >= completedWeeks)
            .Select(player => new
            {
                Player = player,
                Floor = player.WeeklyPoints.Take(completedWeeks).Min()
            })
            .OrderByDescending(entry => entry.Floor)
            .ThenByDescending(entry => entry.Player.TotalPoints)
            .FirstOrDefault();
        if (floorLeader is not null)
        {
            playerMarks.Add(new QuarterMark(
                "Best Floor",
                floorLeader.Player.PlayerName,
                $"{floorLeader.Floor:0.0}",
                $"Lowest week · {floorLeader.Player.Position}{floorLeader.Player.Rank}",
                floorLeader.Player.Position));
        }

        var quarterKing = positionReports
            .Where(report => report.Rankings.Count >= 2)
            .Select(report => new
            {
                Leader = report.Rankings[0],
                Gap = report.Rankings[0].TotalPoints - report.Rankings[1].TotalPoints,
                GapPercent = report.Rankings[1].TotalPoints == 0m
                    ? 0m
                    : (report.Rankings[0].TotalPoints - report.Rankings[1].TotalPoints)
                        / report.Rankings[1].TotalPoints * 100m
            })
            .OrderByDescending(entry => entry.GapPercent)
            .FirstOrDefault();
        if (quarterKing is not null)
        {
            playerMarks.Add(new QuarterMark(
                "Quarter King",
                quarterKing.Leader.PlayerName,
                $"+{quarterKing.Gap:0.0}",
                $"{quarterKing.GapPercent:0.0}% clear of {quarterKing.Leader.Position}2",
                quarterKing.Leader.Position));
        }

        var teamMarks = new List<QuarterMark>();
        var positionRoom = teamPositionReports
            .Where(report => report.Position != "TOTAL" && report.Rankings.Count >= 2)
            .Select(report => new
            {
                Position = report.Position,
                Team = report.Rankings[0],
                Gap = report.Rankings[0].TotalPoints - report.Rankings[1].TotalPoints,
                GapPercent = report.Rankings[1].TotalPoints == 0m
                    ? 0m
                    : (report.Rankings[0].TotalPoints - report.Rankings[1].TotalPoints)
                        / report.Rankings[1].TotalPoints * 100m
            })
            .OrderByDescending(entry => entry.GapPercent)
            .FirstOrDefault();
        if (positionRoom is not null)
        {
            teamMarks.Add(new QuarterMark(
                "Best Position Room",
                positionRoom.Team.TeamName,
                $"+{positionRoom.Gap:0.0}",
                $"{positionRoom.Position} lead · {positionRoom.GapPercent:0.0}% over second",
                positionRoom.Position));
        }

        var byRoster = teamPositionReports
            .Where(report => report.Position != "TOTAL")
            .SelectMany(report => report.Rankings)
            .GroupBy(team => team.RosterId)
            .Select(group => new
            {
                Team = group.First(),
                RankSpread = group.Max(team => team.Rank) - group.Min(team => team.Rank),
                AverageRank = group.Average(team => team.Rank)
            })
            .OrderBy(entry => entry.RankSpread + entry.AverageRank)
            .ThenBy(entry => entry.AverageRank)
            .FirstOrDefault();
        if (byRoster is not null)
        {
            teamMarks.Add(new QuarterMark(
                "Most Balanced",
                byRoster.Team.TeamName.Trim(),
                $"{byRoster.RankSpread}",
                $"Position-rank spread · average rank {byRoster.AverageRank:0.0}",
                null));
        }

        var totalsByRoster = teamPositionReports
            .SingleOrDefault(report => report.Position == "TOTAL")?
            .Rankings.ToDictionary(team => team.RosterId, team => team.TotalPoints)
            ?? [];
        var carryJob = teamPositionReports
            .Where(report => report.Position != "TOTAL")
            .SelectMany(report => report.Rankings)
            .Where(team => totalsByRoster.GetValueOrDefault(team.RosterId) > 0m)
            .Select(team => new
            {
                Team = team,
                Share = team.TotalPoints / totalsByRoster[team.RosterId] * 100m
            })
            .OrderByDescending(entry => entry.Share)
            .FirstOrDefault();
        if (carryJob is not null)
        {
            teamMarks.Add(new QuarterMark(
                "The Carry Job",
                carryJob.Team.TeamName.Trim(),
                $"{carryJob.Share:0.0}%",
                $"{carryJob.Team.Position} share of all scoring",
                carryJob.Team.Position));
        }

        var lineupRows = lineupScores.ToList();
        var lineupMarks = new List<QuarterMark>();
        var lineupEfficiency = lineupRows
            .GroupBy(row => row.RosterId)
            .Select(group => new
            {
                Team = group.OrderBy(row => row.Week).Last(),
                Actual = group.Sum(row => row.ActualPoints),
                Optimal = group.Sum(row => row.OptimalPoints)
            })
            .Where(entry => entry.Optimal > 0m)
            .Select(entry => new
            {
                entry.Team,
                Efficiency = Math.Min(100m, entry.Actual / entry.Optimal * 100m),
                entry.Actual,
                entry.Optimal
            })
            .OrderByDescending(entry => entry.Efficiency)
            .ThenByDescending(entry => entry.Actual)
            .FirstOrDefault();
        if (lineupEfficiency is not null)
        {
            lineupMarks.Add(new QuarterMark(
                "Lineup Genius",
                lineupEfficiency.Team.TeamName.Trim(),
                $"{lineupEfficiency.Efficiency:0.0}%",
                $"{lineupEfficiency.Actual:0.0} of {lineupEfficiency.Optimal:0.0} available points",
                null));
        }

        var leftBehind = lineupRows
            .GroupBy(row => row.RosterId)
            .Select(group => new
            {
                Team = group.OrderBy(row => row.Week).Last(),
                Points = group.Sum(row => Math.Max(0m, row.OptimalPoints - row.ActualPoints))
            })
            .OrderByDescending(entry => entry.Points)
            .FirstOrDefault();
        if (leftBehind is not null)
        {
            lineupMarks.Add(new QuarterMark(
                "Points Left Behind",
                leftBehind.Team.TeamName.Trim(),
                $"{leftBehind.Points:0.0}",
                "Optimal lineup gap across four weeks",
                null));
        }

        var benchExplosion = lineupRows
            .Where(row => row.BestBenchPoints > 0m)
            .OrderByDescending(row => row.BestBenchPoints)
            .FirstOrDefault();
        if (benchExplosion is not null)
        {
            lineupMarks.Add(new QuarterMark(
                "Bench Explosion",
                benchExplosion.BestBenchPlayer,
                $"{benchExplosion.BestBenchPoints:0.0}",
                $"{benchExplosion.TeamName.Trim()} · Week {benchExplosion.Week}",
                benchExplosion.BestBenchPosition));
        }

        var totalScores = teamScores.Where(score => score.Position == "TOTAL").ToList();
        var luckRows = BuildLuckRows(totalScores);
        var luckMarks = new List<QuarterMark>();
        var scheduleDarling = luckRows
            .OrderByDescending(row => row.ActualWins - row.ExpectedWins)
            .FirstOrDefault();
        if (scheduleDarling is not null)
        {
            luckMarks.Add(new QuarterMark(
                "Schedule Darling",
                scheduleDarling.TeamName.Trim(),
                $"+{scheduleDarling.ActualWins - scheduleDarling.ExpectedWins:0.00}",
                $"{scheduleDarling.ActualWins:0.#} wins · {scheduleDarling.ExpectedWins:0.00} expected",
                null));
        }

        var hardLuck = luckRows
            .OrderByDescending(row => row.ExpectedWins - row.ActualWins)
            .FirstOrDefault();
        if (hardLuck is not null)
        {
            luckMarks.Add(new QuarterMark(
                "Hard-Luck Team",
                hardLuck.TeamName.Trim(),
                $"+{hardLuck.ExpectedWins - hardLuck.ActualWins:0.00}",
                $"{hardLuck.ActualWins:0.#} wins · {hardLuck.ExpectedWins:0.00} expected",
                null));
        }

        var statementWin = totalScores
            .Where(score => score.MatchupId is not null)
            .GroupBy(score => new { score.Week, score.MatchupId })
            .Where(matchup => matchup.Count() == 2)
            .Select(matchup =>
            {
                var ordered = matchup.OrderByDescending(score => score.Points).ToList();
                return new
                {
                    Winner = ordered[0],
                    Loser = ordered[1],
                    Margin = ordered[0].Points - ordered[1].Points
                };
            })
            .OrderByDescending(game => game.Margin)
            .FirstOrDefault();
        if (statementWin is not null)
        {
            luckMarks.Add(new QuarterMark(
                "Statement Win",
                statementWin.Winner.TeamName.Trim(),
                $"+{statementWin.Margin:0.0}",
                $"Week {statementWin.Winner.Week} over {statementWin.Loser.TeamName.Trim()}",
                null));
        }

        return
        [
            new QuarterMarkGroup("Player", playerMarks),
            new QuarterMarkGroup("Team", teamMarks),
            new QuarterMarkGroup("Lineup", lineupMarks),
            new QuarterMarkGroup("Luck", luckMarks)
        ];
    }

    private static List<QuarterLuckRow> BuildLuckRows(IReadOnlyList<QuarterTeamWeekScore> totalScores)
    {
        var rows = new Dictionary<int, QuarterLuckRow>();
        foreach (var week in totalScores.GroupBy(score => score.Week))
        {
            var weekScores = week.ToList();
            foreach (var team in weekScores)
            {
                var expected = weekScores.Count <= 1
                    ? 0m
                    : (weekScores.Count(other => other.Points < team.Points)
                        + weekScores.Count(other => other.RosterId != team.RosterId && other.Points == team.Points) * 0.5m)
                        / (weekScores.Count - 1);
                var opponent = team.MatchupId is null
                    ? null
                    : weekScores.SingleOrDefault(other =>
                        other.RosterId != team.RosterId && other.MatchupId == team.MatchupId);
                var actual = opponent is null
                    ? 0m
                    : team.Points > opponent.Points ? 1m : team.Points == opponent.Points ? 0.5m : 0m;

                if (!rows.TryGetValue(team.RosterId, out var row))
                {
                    row = new QuarterLuckRow(team.RosterId, team.TeamName, 0m, 0m);
                }
                rows[team.RosterId] = row with
                {
                    TeamName = team.TeamName,
                    ActualWins = row.ActualWins + actual,
                    ExpectedWins = row.ExpectedWins + expected
                };
            }
        }

        return rows.Values.ToList();
    }

    private static QuarterPlayer BuildPlayer(
        IGrouping<string, WeeklyPlayerStats> group,
        FantasyScorer scorer,
        IReadOnlyList<int> weeks,
        int completedWeeks)
    {
        var rows = group.OrderBy(stat => stat.Week).ToList();
        var latest = rows[^1];
        var scoredByWeek = rows
            .GroupBy(stat => stat.Week)
            .ToDictionary(
                week => week.Key,
                week => Round(week.Sum(stat => scorer.ScoreWeekly(stat).TotalPoints)));
        var weeklyPoints = weeks.Select(week => scoredByWeek.GetValueOrDefault(week)).ToArray();
        var completedPoints = weeklyPoints.Take(completedWeeks).ToArray();
        var total = Round(completedPoints.Sum());
        var average = Round(total / completedWeeks);
        var variance = completedPoints.Average(points => (double)((points - average) * (points - average)));
        var best = completedPoints
            .Select((points, index) => new { Points = points, Week = weeks[index] })
            .OrderByDescending(entry => entry.Points)
            .First();

        return new QuarterPlayer(
            Rank: 0,
            PlayerId: group.Key,
            PlayerName: latest.PlayerDisplayName ?? latest.PlayerName ?? group.Key,
            Position: latest.Position!.ToUpperInvariant(),
            Team: latest.Team,
            Games: rows.Select(stat => stat.Week).Distinct().Count(),
            TotalPoints: total,
            PointsPerGame: average,
            WeeklyPoints: weeklyPoints,
            BestWeek: best.Week,
            BestWeekPoints: best.Points,
            Consistency: Round((decimal)Math.Sqrt(variance)),
            Trend: completedWeeks == 1
                ? 0m
                : Round(completedPoints[^1] - completedPoints[0]));
    }

    private static QuarterPlayer BuildDefense(
        IGrouping<string, QuarterDefenseWeekScore> group,
        IReadOnlyList<int> weeks,
        int completedWeeks)
    {
        var rows = group.OrderBy(score => score.Week).ToList();
        var latest = rows[^1];
        var scoredByWeek = rows
            .GroupBy(score => score.Week)
            .ToDictionary(week => week.Key, week => Round(week.Sum(score => score.Points)));
        var weeklyPoints = weeks.Select(week => scoredByWeek.GetValueOrDefault(week)).ToArray();
        var completedPoints = weeklyPoints.Take(completedWeeks).ToArray();
        var total = Round(completedPoints.Sum());
        var average = Round(total / completedWeeks);
        var variance = completedPoints.Average(points => (double)((points - average) * (points - average)));
        var best = completedPoints
            .Select((points, index) => new { Points = points, Week = weeks[index] })
            .OrderByDescending(entry => entry.Points)
            .First();

        return new QuarterPlayer(
            Rank: 0,
            PlayerId: group.Key,
            PlayerName: latest.Name,
            Position: "DEF",
            Team: latest.Team,
            Games: rows.Select(score => score.Week).Distinct().Count(),
            TotalPoints: total,
            PointsPerGame: average,
            WeeklyPoints: weeklyPoints,
            BestWeek: best.Week,
            BestWeekPoints: best.Points,
            Consistency: Round((decimal)Math.Sqrt(variance)),
            Trend: completedWeeks == 1
                ? 0m
                : Round(completedPoints[^1] - completedPoints[0]));
    }

    private static List<QuarterTeamPositionReport> BuildTeamPositionReports(
        IEnumerable<QuarterTeamWeekScore> teamScores,
        IReadOnlyList<int> weeks,
        int completedWeeks)
    {
        var scores = teamScores
            .Where(score => score.Week >= weeks[0] && score.Week < weeks[0] + completedWeeks)
            .ToList();

        return new[] { "TOTAL" }
            .Concat(PositionOrder)
            .Select(position =>
            {
                var rankings = scores
                    .Where(score => score.Position == position)
                    .GroupBy(score => score.RosterId)
                    .Select(group =>
                    {
                        var latest = group.OrderBy(score => score.Week).Last();
                        var pointsByWeek = group
                            .GroupBy(score => score.Week)
                            .ToDictionary(week => week.Key, week => Round(week.Sum(score => score.Points)));
                        var weeklyPoints = weeks.Select(week => pointsByWeek.GetValueOrDefault(week)).ToArray();
                        return new QuarterTeam(
                            Rank: 0,
                            RosterId: group.Key,
                            OwnerName: latest.OwnerName,
                            TeamName: latest.TeamName,
                            Position: position,
                            TotalPoints: Round(weeklyPoints.Take(completedWeeks).Sum()),
                            PointsPerWeek: Round(weeklyPoints.Take(completedWeeks).Average()),
                            WeeklyPoints: weeklyPoints);
                    })
                    .OrderByDescending(team => team.TotalPoints)
                    .ThenBy(team => team.TeamName, StringComparer.Ordinal)
                    .Select((team, index) => team with { Rank = index + 1 })
                    .ToList();

                return new QuarterTeamPositionReport(
                    Position: position,
                    Leader: rankings.FirstOrDefault(),
                    Rankings: rankings);
            })
            .Where(report => report.Rankings.Count > 0)
            .ToList();
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

internal sealed record QuarterDefenseWeekScore(
    string Team,
    string Name,
    int Week,
    decimal Points);

internal sealed record QuarterTeamWeekScore(
    int RosterId,
    string OwnerName,
    string TeamName,
    string Position,
    int Week,
    decimal Points,
    int? MatchupId = null);

internal sealed record QuarterLineupWeekScore(
    int RosterId,
    string OwnerName,
    string TeamName,
    int Week,
    decimal ActualPoints,
    decimal OptimalPoints,
    string BestBenchPlayer,
    string? BestBenchPosition,
    decimal BestBenchPoints);

internal sealed record QuarterReport(
    int Season,
    int StartWeek,
    int EndWeek,
    int ThroughWeek,
    string Status,
    string[] Positions,
    List<QuarterScoringRule> ScoringRules,
    List<QuarterWeek> WeeklyTotals,
    List<QuarterPlayer> OverallLeaders,
    List<QuarterPositionReport> PositionReports,
    List<QuarterTeamPositionReport> TeamPositionReports,
    List<QuarterMarkGroup> MarkGroups,
    QuarterSuperlatives Superlatives);

internal sealed record QuarterScoringRule(string Key, decimal PointsPerUnit);

internal sealed record QuarterWeek(
    int Week,
    decimal TotalPoints,
    int PlayerCount,
    List<QuarterWeekLeader> Leaders);

internal sealed record QuarterWeekLeader(
    string Position,
    string PlayerName,
    string? Team,
    decimal Points);

internal sealed record QuarterPositionReport(
    string Position,
    int PlayerCount,
    QuarterPlayer? Leader,
    decimal TopTenAverage,
    List<QuarterPlayer> Rankings);

internal sealed record QuarterPlayer(
    int Rank,
    string PlayerId,
    string PlayerName,
    string Position,
    string? Team,
    int Games,
    decimal TotalPoints,
    decimal PointsPerGame,
    decimal[] WeeklyPoints,
    int BestWeek,
    decimal BestWeekPoints,
    decimal Consistency,
    decimal Trend);

internal sealed record QuarterTeamPositionReport(
    string Position,
    QuarterTeam? Leader,
    List<QuarterTeam> Rankings);

internal sealed record QuarterTeam(
    int Rank,
    int RosterId,
    string OwnerName,
    string TeamName,
    string Position,
    decimal TotalPoints,
    decimal PointsPerWeek,
    decimal[] WeeklyPoints);

internal sealed record QuarterMarkGroup(
    string Group,
    List<QuarterMark> Marks);

internal sealed record QuarterMark(
    string Label,
    string Subject,
    string DisplayValue,
    string Detail,
    string? Position);

internal sealed record QuarterLuckRow(
    int RosterId,
    string TeamName,
    decimal ActualWins,
    decimal ExpectedWins);

internal sealed record QuarterSuperlatives(
    QuarterMoment? BiggestWeek,
    QuarterPlayer? Steadiest,
    QuarterPlayer? HottestFinish);

internal sealed record QuarterMoment(
    string PlayerName,
    string Position,
    string? Team,
    int Week,
    decimal Points);
