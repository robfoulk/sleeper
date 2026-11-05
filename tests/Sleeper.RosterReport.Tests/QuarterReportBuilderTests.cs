using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Sleeper.Api.NflData;
using Sleeper.Api.NflData.Models;
using Sleeper.RosterReport.Recap;

namespace Sleeper.RosterReport.Tests;

public class QuarterReportBuilderTests
{
    [Fact]
    public void SecondQuarterUsesFourLocalOffsetsAndActualWeekNumbers()
    {
        var stats = new[]
        {
            Line("qb", "Quarterback", "QB", 4, 9999),
            Line("qb", "Quarterback", "QB", 5, 250),
            Line("qb", "Quarterback", "QB", 6, 500),
            Line("qb", "Quarterback", "QB", 8, 750),
            Line("qb", "Quarterback", "QB", 9, 9999),
            Line("steady", "Steady QB", "QB", 5, 125),
            Line("steady", "Steady QB", "QB", 6, 125),
            Line("steady", "Steady QB", "QB", 7, 125),
            Line("steady", "Steady QB", "QB", 8, 125)
        };
        var report = QuarterReportBuilder.Build(2026, 12, Scoring, stats, firstWeek: 5, lastWeek: 8);

        report.StartWeek.Should().Be(5);
        report.EndWeek.Should().Be(8);
        report.ThroughWeek.Should().Be(8);
        report.Status.Should().Be("complete");
        report.WeeklyTotals.Select(week => week.Week).Should().Equal(5, 6, 7, 8);
        var leader = report.PositionReports.Single().Leader!;
        leader.TotalPoints.Should().Be(60m);
        leader.PointsPerGame.Should().Be(15m);
        leader.WeeklyPoints.Should().Equal(10m, 20m, 0m, 30m);
        leader.Consistency.Should().Be(11.18m);
        leader.BestWeek.Should().Be(8);
        leader.Trend.Should().Be(20m);
        report.Superlatives.BiggestWeek!.Week.Should().Be(8);
        report.Superlatives.Steadiest!.PlayerName.Should().Be("Steady QB");
        report.Superlatives.HottestFinish!.PlayerName.Should().Be("Quarterback");
        report.MarkGroups.Single(group => group.Group == "Player").Marks
            .Single(mark => mark.Label == "Best Floor").Subject.Should().Be("Steady QB");
    }

    [Fact]
    public void PartialSecondQuarterAveragesOnlyCompletedWeeksAndPreservesNegativeScores()
    {
        var defenses = new[]
        {
            new QuarterDefenseWeekScore("TST", "Test Defense", 4, 100m),
            new QuarterDefenseWeekScore("TST", "Test Defense", 5, -2m),
            new QuarterDefenseWeekScore("TST", "Test Defense", 6, 4m),
            new QuarterDefenseWeekScore("TST", "Test Defense", 7, 100m)
        };
        var report = QuarterReportBuilder.Build(2026, 6, Scoring, [], defenses, firstWeek: 5, lastWeek: 8);

        report.Status.Should().Be("in-progress");
        report.WeeklyTotals.Select(week => week.Week).Should().Equal(5, 6);
        var defense = report.PositionReports.Single().Leader!;
        defense.WeeklyPoints.Should().Equal(-2m, 4m, 0m, 0m);
        defense.TotalPoints.Should().Be(2m);
        defense.PointsPerGame.Should().Be(1m);
        defense.Consistency.Should().Be(3m);
        defense.BestWeek.Should().Be(6);
        defense.Trend.Should().Be(6m);
    }

    [Fact]
    public void SecondQuarterFiltersTeamLineupAndLuckFindingsToItsOwnWindow()
    {
        var teams = new[]
        {
            new QuarterTeamWeekScore(1, "A", "Team A", "TOTAL", 4, 999m, 1),
            new QuarterTeamWeekScore(1, "A", "Team A", "TOTAL", 5, 100m, 1),
            new QuarterTeamWeekScore(2, "B", "Team B", "TOTAL", 5, 90m, 1),
            new QuarterTeamWeekScore(1, "A", "Team A", "TOTAL", 8, 80m, 1),
            new QuarterTeamWeekScore(2, "B", "Team B", "TOTAL", 8, 95m, 1),
            new QuarterTeamWeekScore(2, "B", "Team B", "TOTAL", 9, 999m, 1)
        };
        var lineups = new[]
        {
            new QuarterLineupWeekScore(1, "A", "Team A", 4, 1m, 999m, "Outside Quarter", "QB", 999m),
            new QuarterLineupWeekScore(1, "A", "Team A", 5, 100m, 110m, "Week Five Bench", "QB", 20m),
            new QuarterLineupWeekScore(1, "A", "Team A", 8, 80m, 100m, "Week Eight Bench", "QB", 30m),
            new QuarterLineupWeekScore(2, "B", "Team B", 9, 1m, 999m, "Future Bench", "QB", 999m)
        };
        var report = QuarterReportBuilder.Build(2026, 8, Scoring, [], [], teams, lineups, 5, 8);

        var rankings = report.TeamPositionReports.Single().Rankings;
        rankings.Select(team => team.TotalPoints).Should().Equal(185m, 180m);
        rankings[1].WeeklyPoints.Should().Equal(100m, 0m, 0m, 80m);
        rankings[1].PointsPerWeek.Should().Be(45m);
        report.MarkGroups.Single(group => group.Group == "Lineup").Marks
            .Single(mark => mark.Label == "Bench Explosion").Subject.Should().Be("Week Eight Bench");
        report.MarkGroups.Single(group => group.Group == "Luck").Marks
            .Single(mark => mark.Label == "Statement Win").Detail.Should().Be("Week 8 over Team A");
    }

    [Fact]
    public async Task SecondQuarterDoesNotFetchStatsBeforeWeekFiveIsComplete()
    {
        var nfl = Substitute.For<INflDataClient>();
        var report = await QuarterReportBuilder.BuildAsync(nfl, 2026, 4, Scoring, [], [], [], 5, 8);

        report.Should().BeNull();
        await nfl.DidNotReceive().GetWeeklyStatsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingCompletedWeekStatsBlockTheReport()
    {
        var nfl = Substitute.For<INflDataClient>();
        nfl.GetWeeklyStatsAsync(2026, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<WeeklyPlayerStats> { Line("qb", "Quarterback", "QB", 5, 250) }));

        var build = () => QuarterReportBuilder.BuildAsync(nfl, 2026, 6, Scoring, [], [], [], 5, 8);
        await build.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2026 Week 6*");
    }

    [Fact]
    public void NegativeOnlyOpeningWeekDoesNotInventAFuturePeak()
    {
        var report = QuarterReportBuilder.Build(2026, 5, Scoring, [],
            [new QuarterDefenseWeekScore("TST", "Test Defense", 5, -2m)], firstWeek: 5, lastWeek: 8);

        report.Superlatives.BiggestWeek!.Week.Should().Be(5);
        report.Superlatives.BiggestWeek.Points.Should().Be(-2m);
        report.PositionReports.Single().Leader!.Trend.Should().Be(0m);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(5, 4)]
    public void RejectsInvalidQuarterBounds(int firstWeek, int lastWeek)
    {
        var build = () => QuarterReportBuilder.Build(2026, 8, Scoring, [], firstWeek: firstWeek, lastWeek: lastWeek);
        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EditionScoresUseCapturedCorrectedTotalsAndHistoricalStarters()
    {
        using var edition = JsonDocument.Parse("""
            { "weeks": [{ "week": 5, "games": [{
              "home": { "franchise_id": 1, "owner_name": "A", "team_name": "Team A", "points": 35,
                "players": [
                  { "name": "Starter A", "position": "QB", "nfl_team": "AAA", "points": 10, "starter": true },
                  { "name": "Bench A", "position": "QB", "nfl_team": "BBB", "points": 30, "starter": false },
                  { "name": "Runner A", "position": "RB", "nfl_team": "AAA", "points": 20, "starter": true },
                  { "name": "Defense A", "position": "DEF", "nfl_team": "AAA", "points": 5, "starter": true }
                ] },
              "away": { "franchise_id": 2, "owner_name": "B", "team_name": "Team B", "points": 25,
                "players": [
                  { "name": "Starter B", "position": "QB", "nfl_team": "BBB", "points": 8, "starter": true },
                  { "name": "Runner B", "position": "RB", "nfl_team": "BBB", "points": 12, "starter": true },
                  { "name": "Defense B", "position": "DEF", "nfl_team": "BBB", "points": 5, "starter": true }
                ] }
            }] }] }
            """);
        var scores = SiteDataBuilder.LoadEditionQuarterScores(edition.RootElement, 5, 5, ["QB", "RB", "DEF"]);

        scores.DefenseScores.Should().HaveCount(2);
        scores.TeamScores.Where(score => score.Position == "TOTAL")
            .Select(score => score.Points).Should().Equal(35m, 25m);
        scores.TeamScores.Single(score => score.RosterId == 1 && score.Position == "QB").Points.Should().Be(10m);
        scores.TeamScores.All(score => score.Week == 5 && score.MatchupId == 1).Should().BeTrue();
        scores.LineupScores[0].ActualPoints.Should().Be(35m);
        scores.LineupScores[0].OptimalPoints.Should().Be(55m);
        scores.LineupScores[0].BestBenchPlayer.Should().Be("Bench A");

        var missingWeek = () => SiteDataBuilder.LoadEditionQuarterScores(edition.RootElement, 5, 6, ["QB"]);
        missingWeek.Should().Throw<InvalidOperationException>().WithMessage("*missing Week 6*");
    }

    [Fact]
    public void InvalidFiledQuarterFailsInsteadOfRecomputingThePublishedReport()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sleeper-quarter-{Guid.NewGuid():N}");
        var seasonDir = Path.Combine(root, "2026");
        Directory.CreateDirectory(seasonDir);
        try
        {
            File.WriteAllText(Path.Combine(seasonDir, "quarter-01-04-data.json"), "{}");
            var load = () => SiteDataBuilder.LoadFiledQuarterReport(root, 2026, 1, 4);
            load.Should().Throw<InvalidOperationException>().WithMessage("*does not match the completed window*");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static Dictionary<string, decimal> Scoring => new() { ["pass_yd"] = 0.04m };

    private static WeeklyPlayerStats Line(string id, string name, string position, int week, int yards)
        => new()
        {
            PlayerId = id,
            PlayerDisplayName = name,
            Position = position,
            Team = "TST",
            Season = 2026,
            SeasonType = "REG",
            Week = week,
            PassingYards = yards
        };
}
