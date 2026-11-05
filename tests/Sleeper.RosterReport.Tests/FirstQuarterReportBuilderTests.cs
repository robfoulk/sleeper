using FluentAssertions;
using Sleeper.Api.NflData.Models;
using Sleeper.RosterReport.Recap;

namespace Sleeper.RosterReport.Tests;

public class FirstQuarterReportBuilderTests
{
    [Fact]
    public void RanksPlayersByTotalPointsUsingLeagueScoring()
    {
        var scoring = new Dictionary<string, decimal>
        {
            ["pass_yd"] = 0.04m,
            ["pass_td"] = 6m,
            ["pass_int"] = -2m,
            ["rush_yd"] = 0.1m
        };
        var stats = new[]
        {
            QuarterLine("qb-a", "Alpha QB", "QB", 1, passingYards: 300, passingTds: 3),
            QuarterLine("qb-a", "Alpha QB", "QB", 2, passingYards: 250, passingTds: 2),
            QuarterLine("qb-b", "Beta QB", "QB", 1, passingYards: 350, passingTds: 2),
            QuarterLine("qb-b", "Beta QB", "QB", 2, passingYards: 350, passingTds: 2),
            QuarterLine("rb-a", "Runner One", "RB", 1, rushingYards: 120),
            QuarterLine("rb-a", "Runner One", "RB", 2, rushingYards: 80)
        };

        var report = QuarterReportBuilder.Build(2026, 2, scoring, stats);

        report.Status.Should().Be("in-progress");
        report.ThroughWeek.Should().Be(2);
        var quarterbacks = report.PositionReports.Single(position => position.Position == "QB");
        quarterbacks.Rankings.Select(player => player.PlayerName)
            .Should().ContainInOrder("Alpha QB", "Beta QB");
        quarterbacks.Rankings[0].TotalPoints.Should().Be(52m);
        quarterbacks.Rankings[0].WeeklyPoints.Should().Equal(30m, 22m, 0m, 0m);
        quarterbacks.Rankings[0].Rank.Should().Be(1);
        report.PositionReports.Single(position => position.Position == "RB")
            .Leader!.TotalPoints.Should().Be(20m);
    }

    [Fact]
    public void UsesFourWeekWindowForConsistencyTrendAndPeak()
    {
        var scoring = new Dictionary<string, decimal> { ["rush_yd"] = 0.1m };
        var stats = new[]
        {
            QuarterLine("rb-steady", "Steady Back", "RB", 1, rushingYards: 100),
            QuarterLine("rb-steady", "Steady Back", "RB", 2, rushingYards: 100),
            QuarterLine("rb-steady", "Steady Back", "RB", 3, rushingYards: 100),
            QuarterLine("rb-steady", "Steady Back", "RB", 4, rushingYards: 100),
            QuarterLine("rb-boom", "Boom Back", "RB", 1, rushingYards: 20),
            QuarterLine("rb-boom", "Boom Back", "RB", 2, rushingYards: 50),
            QuarterLine("rb-boom", "Boom Back", "RB", 3, rushingYards: 100),
            QuarterLine("rb-boom", "Boom Back", "RB", 4, rushingYards: 250)
        };

        var report = QuarterReportBuilder.Build(2026, 4, scoring, stats);

        report.Status.Should().Be("complete");
        report.Superlatives.Steadiest!.PlayerName.Should().Be("Steady Back");
        report.Superlatives.HottestFinish!.PlayerName.Should().Be("Boom Back");
        report.Superlatives.HottestFinish.Trend.Should().Be(23m);
        report.Superlatives.BiggestWeek!.PlayerName.Should().Be("Boom Back");
        report.Superlatives.BiggestWeek.Week.Should().Be(4);
        report.Superlatives.BiggestWeek.Points.Should().Be(25m);
    }

    [Fact]
    public void RanksTeamDefensesWithTheOtherPositions()
    {
        var defenses = new[]
        {
            new QuarterDefenseWeekScore("BAL", "Baltimore Ravens", 1, 18m),
            new QuarterDefenseWeekScore("BAL", "Baltimore Ravens", 2, 7m),
            new QuarterDefenseWeekScore("PIT", "Pittsburgh Steelers", 1, 10m),
            new QuarterDefenseWeekScore("PIT", "Pittsburgh Steelers", 2, 11m)
        };

        var report = QuarterReportBuilder.Build(
            2026,
            2,
            new Dictionary<string, decimal> { ["pass_td"] = 4m },
            [],
            defenses);

        var rankings = report.PositionReports.Single(position => position.Position == "DEF").Rankings;
        rankings.Select(defense => defense.PlayerName)
            .Should().ContainInOrder("Baltimore Ravens", "Pittsburgh Steelers");
        rankings[0].TotalPoints.Should().Be(25m);
        rankings[0].WeeklyPoints.Should().Equal(18m, 7m, 0m, 0m);
        report.Positions.Should().Contain("DEF");
    }

    [Fact]
    public void RanksFantasyTeamsByOverallAndStartingPositionPoints()
    {
        var teamScores = new[]
        {
            new QuarterTeamWeekScore(1, "Rob", "Team One", "TOTAL", 1, 200m),
            new QuarterTeamWeekScore(1, "Rob", "Team One", "QB", 1, 45m),
            new QuarterTeamWeekScore(1, "Rob", "Team One", "RB", 1, 60m),
            new QuarterTeamWeekScore(1, "Rob", "Team One", "TOTAL", 2, 180m),
            new QuarterTeamWeekScore(1, "Rob", "Team One", "QB", 2, 30m),
            new QuarterTeamWeekScore(1, "Rob", "Team One", "RB", 2, 50m),
            new QuarterTeamWeekScore(2, "Jake", "Team Two", "TOTAL", 1, 190m),
            new QuarterTeamWeekScore(2, "Jake", "Team Two", "QB", 1, 35m),
            new QuarterTeamWeekScore(2, "Jake", "Team Two", "RB", 1, 70m),
            new QuarterTeamWeekScore(2, "Jake", "Team Two", "TOTAL", 2, 195m),
            new QuarterTeamWeekScore(2, "Jake", "Team Two", "QB", 2, 35m),
            new QuarterTeamWeekScore(2, "Jake", "Team Two", "RB", 2, 65m)
        };

        var report = QuarterReportBuilder.Build(
            2026,
            2,
            new Dictionary<string, decimal> { ["pass_td"] = 4m },
            [],
            [],
            teamScores);

        report.TeamPositionReports.Single(position => position.Position == "TOTAL")
            .Leader!.TeamName.Should().Be("Team Two");
        report.TeamPositionReports.Single(position => position.Position == "QB")
            .Leader!.TeamName.Should().Be("Team One");
        report.TeamPositionReports.Single(position => position.Position == "RB")
            .Leader!.TeamName.Should().Be("Team Two");
    }

    [Fact]
    public void BuildsTeamLineupAndLuckMarkGroups()
    {
        var teamScores = new[]
        {
            new QuarterTeamWeekScore(1, "A", "Team A", "TOTAL", 1, 100m, 1),
            new QuarterTeamWeekScore(1, "A", "Team A", "QB", 1, 35m, 1),
            new QuarterTeamWeekScore(1, "A", "Team A", "RB", 1, 25m, 1),
            new QuarterTeamWeekScore(2, "B", "Team B", "TOTAL", 1, 90m, 1),
            new QuarterTeamWeekScore(2, "B", "Team B", "QB", 1, 20m, 1),
            new QuarterTeamWeekScore(2, "B", "Team B", "RB", 1, 40m, 1),
            new QuarterTeamWeekScore(3, "C", "Team C", "TOTAL", 1, 80m, 2),
            new QuarterTeamWeekScore(3, "C", "Team C", "QB", 1, 25m, 2),
            new QuarterTeamWeekScore(3, "C", "Team C", "RB", 1, 30m, 2),
            new QuarterTeamWeekScore(4, "D", "Team D", "TOTAL", 1, 70m, 2),
            new QuarterTeamWeekScore(4, "D", "Team D", "QB", 1, 15m, 2),
            new QuarterTeamWeekScore(4, "D", "Team D", "RB", 1, 20m, 2)
        };
        var lineupScores = new[]
        {
            new QuarterLineupWeekScore(1, "A", "Team A", 1, 90m, 100m, "Bench A", "RB", 10m),
            new QuarterLineupWeekScore(2, "B", "Team B", 1, 75m, 100m, "Bench B", "QB", 30m),
            new QuarterLineupWeekScore(3, "C", "Team C", 1, 70m, 100m, "Bench C", "WR", 20m),
            new QuarterLineupWeekScore(4, "D", "Team D", 1, 60m, 100m, "Bench D", "TE", 15m)
        };

        var report = QuarterReportBuilder.Build(
            2026,
            1,
            new Dictionary<string, decimal> { ["pass_td"] = 4m },
            [],
            [],
            teamScores,
            lineupScores);

        report.MarkGroups.Single(group => group.Group == "Team").Marks.Should().HaveCount(3);
        var lineup = report.MarkGroups.Single(group => group.Group == "Lineup").Marks;
        lineup.Should().HaveCount(3);
        lineup.Single(mark => mark.Label == "Lineup Genius").Subject.Should().Be("Team A");
        lineup.Single(mark => mark.Label == "Points Left Behind").Subject.Should().Be("Team D");
        lineup.Single(mark => mark.Label == "Bench Explosion").Subject.Should().Be("Bench B");

        var luck = report.MarkGroups.Single(group => group.Group == "Luck").Marks;
        luck.Should().HaveCount(3);
        luck.Single(mark => mark.Label == "Schedule Darling").Subject.Should().Be("Team C");
        luck.Single(mark => mark.Label == "Hard-Luck Team").Subject.Should().Be("Team B");
        luck.Single(mark => mark.Label == "Hard-Luck Team").DisplayValue.Should().Be("+0.67");
        luck.Single(mark => mark.Label == "Statement Win").DisplayValue.Should().Be("+10.0");
    }

    private static WeeklyPlayerStats QuarterLine(
        string id,
        string name,
        string position,
        int week,
        int passingYards = 0,
        int passingTds = 0,
        int rushingYards = 0)
        => new()
        {
            PlayerId = id,
            PlayerDisplayName = name,
            Position = position,
            Team = "TST",
            Season = 2026,
            SeasonType = "REG",
            Week = week,
            PassingYards = passingYards,
            PassingTds = passingTds,
            RushingYards = rushingYards
        };
}
