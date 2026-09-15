using FluentAssertions;
using Sleeper.Api.NflData.Models;
using Sleeper.Api.NflData.Scoring;

namespace Sleeper.Api.Tests;

public class FantasyScorerTests
{
    // Your league's scoring settings (non-PPR)
    private static readonly Dictionary<string, decimal> StandardScoring = new()
    {
        ["pass_yd"] = 0.04m,
        ["pass_td"] = 4m,
        ["pass_int"] = -2m,
        ["pass_2pt"] = 2m,
        ["rush_yd"] = 0.1m,
        ["rush_td"] = 6m,
        ["rush_2pt"] = 2m,
        ["rec"] = 0m,       // non-PPR
        ["rec_yd"] = 0.1m,
        ["rec_td"] = 6m,
        ["rec_2pt"] = 2m,
        ["fum_lost"] = -2m,
        ["fum"] = 0m,
        ["st_td"] = 6m,
        ["fgm_0_19"] = 3m,
        ["fgm_20_29"] = 3m,
        ["fgm_30_39"] = 3m,
        ["fgm_40_49"] = 3m,
        ["fgm_50p"] = 5m,
        ["fgmiss"] = 0m,
        ["xpm"] = 1m,
        ["xpmiss"] = -1m,
    };

    private static readonly Dictionary<string, decimal> PprScoring = new(StandardScoring)
    {
        ["rec"] = 1m, // PPR
    };

    private static FantasyScorer CreateStandard() => new(StandardScoring);
    private static FantasyScorer CreatePpr() => new(PprScoring);

    // -- QB Tests --

    [Fact]
    public void ScoreWeekly_QB_CalculatesPassingPoints()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "qb1",
            PlayerDisplayName = "Patrick Mahomes",
            Position = "QB",
            Season = 2025,
            Week = 1,
            Completions = 22,
            Attempts = 30,
            PassingYards = 300,
            PassingTds = 3,
            PassingInterceptions = 1,
            Carries = 3,
            RushingYards = 20,
        };

        var result = scorer.ScoreWeekly(stats);

        // 300 * 0.04 = 12 + 3 * 4 = 12 + 1 * -2 = -2 + 20 * 0.1 = 2 = 24
        result.TotalPoints.Should().Be(24m);
        result.PlayerName.Should().Be("Patrick Mahomes");
        result.Position.Should().Be("QB");

        result.Breakdown.Should().Contain(b => b.Category == "pass_yd" && b.Points == 12m);
        result.Breakdown.Should().Contain(b => b.Category == "pass_td" && b.Points == 12m);
        result.Breakdown.Should().Contain(b => b.Category == "pass_int" && b.Points == -2m);
        result.Breakdown.Should().Contain(b => b.Category == "rush_yd" && b.Points == 2m);
    }

    [Fact]
    public void ScoreWeekly_QB_WithFumbleLost()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "qb1",
            PlayerDisplayName = "Joe Burrow",
            Position = "QB",
            PassingYards = 250,
            PassingTds = 2,
            SackFumblesLost = 1,
        };

        var result = scorer.ScoreWeekly(stats);

        // 250*0.04=10 + 2*4=8 + 1*-2=-2 = 16
        result.TotalPoints.Should().Be(16m);
        result.Breakdown.Should().Contain(b => b.Category == "fum_lost" && b.Points == -2m);
    }

    // -- RB Tests --

    [Fact]
    public void ScoreWeekly_RB_CalculatesRushingAndReceiving()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "rb1",
            PlayerDisplayName = "Saquon Barkley",
            Position = "RB",
            Carries = 22,
            RushingYards = 150,
            RushingTds = 2,
            Receptions = 4,
            ReceivingYards = 30,
        };

        var result = scorer.ScoreWeekly(stats);

        // 150*0.1=15 + 2*6=12 + 0*4(rec)=0 + 30*0.1=3 = 30
        result.TotalPoints.Should().Be(30m);
        result.Breakdown.Should().NotContain(b => b.Category == "rec"); // non-PPR, rec=0
    }

    [Fact]
    public void ScoreWeekly_RB_PprAddsReceptionPoints()
    {
        var scorer = CreatePpr();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "rb1",
            PlayerDisplayName = "Saquon Barkley",
            Position = "RB",
            Carries = 22,
            RushingYards = 150,
            RushingTds = 2,
            Receptions = 4,
            ReceivingYards = 30,
        };

        var result = scorer.ScoreWeekly(stats);

        // 150*0.1=15 + 2*6=12 + 4*1=4(PPR) + 30*0.1=3 = 34
        result.TotalPoints.Should().Be(34m);
        result.Breakdown.Should().Contain(b => b.Category == "rec" && b.Points == 4m);
    }

    // -- WR Tests --

    [Fact]
    public void ScoreWeekly_WR_CalculatesReceivingPoints()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "wr1",
            PlayerDisplayName = "Ja'Marr Chase",
            Position = "WR",
            Receptions = 8,
            Targets = 12,
            ReceivingYards = 120,
            ReceivingTds = 1,
        };

        var result = scorer.ScoreWeekly(stats);

        // 120*0.1=12 + 1*6=6 = 18 (non-PPR)
        result.TotalPoints.Should().Be(18m);
    }

    // -- TE Tests --

    [Fact]
    public void ScoreWeekly_TE_WithReceivingFumbleLost()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "te1",
            PlayerDisplayName = "Travis Kelce",
            Position = "TE",
            Receptions = 6,
            ReceivingYards = 80,
            ReceivingTds = 1,
            ReceivingFumbles = 1,
            ReceivingFumblesLost = 1,
        };

        var result = scorer.ScoreWeekly(stats);

        // 80*0.1=8 + 1*6=6 + 1*-2=-2 = 12
        result.TotalPoints.Should().Be(12m);
    }

    // -- K Tests --

    [Fact]
    public void ScoreWeekly_Kicker_CalculatesFieldGoalsAndPats()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "k1",
            PlayerDisplayName = "Justin Tucker",
            Position = "K",
            FgMade0_19 = 0,
            FgMade20_29 = 1,
            FgMade30_39 = 1,
            FgMade40_49 = 1,
            FgMade50_59 = 1,
            FgMade60Plus = 0,
            PatMade = 3,
            PatMissed = 1,
        };

        var result = scorer.ScoreWeekly(stats);

        // 1*3 + 1*3 + 1*3 + 1*5(50+) + 3*1 + 1*-1 = 3+3+3+5+3-1 = 16
        result.TotalPoints.Should().Be(16m);
        result.Breakdown.Should().Contain(b => b.Category == "fgm_50p" && b.Points == 5m);
        result.Breakdown.Should().Contain(b => b.Category == "xpmiss" && b.Points == -1m);
    }

    [Fact]
    public void ScoreWeekly_Kicker_60PlusCountsAs50Plus()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "k1",
            PlayerDisplayName = "Tucker",
            Position = "K",
            FgMade50_59 = 1,
            FgMade60Plus = 1,
            PatMade = 2,
        };

        var result = scorer.ScoreWeekly(stats);

        // 2*5(50+combined) + 2*1(xp) = 12
        result.TotalPoints.Should().Be(12m);
        result.Breakdown.Should().Contain(b => b.Category == "fgm_50p" && b.StatValue == 2m);
    }

    // -- Season Tests --

    [Fact]
    public void ScoreSeason_QB_CalculatesSeasonTotals()
    {
        var scorer = CreateStandard();
        var stats = new SeasonPlayerStats
        {
            PlayerId = "qb1",
            PlayerDisplayName = "Josh Allen",
            Position = "QB",
            Season = 2025,
            Games = 17,
            PassingYards = 4000,
            PassingTds = 30,
            PassingInterceptions = 10,
            RushingYards = 500,
            RushingTds = 5,
            SackFumblesLost = 3,
        };

        var result = scorer.ScoreSeason(stats);

        // 4000*0.04=160 + 30*4=120 + 10*-2=-20 + 500*0.1=50 + 5*6=30 + 3*-2=-6 = 334
        result.TotalPoints.Should().Be(334m);
        result.PlayerName.Should().Be("Josh Allen");
    }

    // -- Edge Cases --

    [Fact]
    public void ScoreWeekly_AllZeros_ReturnsZero()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "p1",
            Position = "RB",
        };

        var result = scorer.ScoreWeekly(stats);

        result.TotalPoints.Should().Be(0m);
        result.Breakdown.Should().BeEmpty();
    }

    [Fact]
    public void ScoreWeekly_NegativeRushingYards_ScoresCorrectly()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "qb1",
            Position = "QB",
            PassingYards = 200,
            PassingTds = 1,
            RushingYards = -5,
        };

        var result = scorer.ScoreWeekly(stats);

        // 200*0.04=8 + 1*4=4 + (-5)*0.1=-0.5 = 11.5
        result.TotalPoints.Should().Be(11.5m);
    }

    [Fact]
    public void ScoreWeekly_CombinesFumbleSources()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "rb1",
            Position = "RB",
            RushingYards = 100,
            RushingFumblesLost = 1,
            ReceivingFumblesLost = 1,
            SackFumblesLost = 0,
        };

        var result = scorer.ScoreWeekly(stats);

        // 100*0.1=10 + 2*-2=-4 = 6
        result.TotalPoints.Should().Be(6m);
        result.Breakdown.Should().Contain(b => b.Category == "fum_lost" && b.StatValue == 2m && b.Points == -4m);
    }

    [Fact]
    public void ScoreWeekly_BreakdownSortedByAbsolutePoints()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "qb1",
            Position = "QB",
            PassingYards = 300,  // 12 pts
            PassingTds = 3,      // 12 pts
            PassingInterceptions = 2, // -4 pts
            RushingYards = 10,   // 1 pt
        };

        var result = scorer.ScoreWeekly(stats);

        // Breakdown should be sorted by |points| descending
        result.Breakdown[0].Points.Should().Be(12m); // pass_yd or pass_td
        result.Breakdown[1].Points.Should().Be(12m);
        result.Breakdown[2].Points.Should().Be(-4m); // pass_int
        result.Breakdown[3].Points.Should().Be(1m);  // rush_yd
    }

    [Fact]
    public void ScoreWeekly_With2PtConversion()
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "qb1",
            Position = "QB",
            PassingYards = 100,
            Passing2PtConversions = 1,
        };

        var result = scorer.ScoreWeekly(stats);

        // 100*0.04=4 + 1*2=2 = 6
        result.TotalPoints.Should().Be(6m);
        result.Breakdown.Should().Contain(b => b.Category == "pass_2pt" && b.Points == 2m);
    }

    // =====================================================================
    // Validation tests against real Sleeper FPTS (2025 season, league)
    // =====================================================================

    [Theory]
    [InlineData(1, 258, 1, 0, 57, 1, 26.02)]  // @LAC
    [InlineData(2, 187, 1, 1, 66, 1, 22.08)]  // PHI
    [InlineData(3, 224, 1, 0, 2, 0, 13.16)]   // @NYG (2 fumbles, 0 lost)
    [InlineData(4, 270, 4, 0, 5, 0, 27.30)]   // BAL
    [InlineData(5, 318, 1, 1, 60, 1, 26.72)]  // @JAX
    [InlineData(6, 257, 3, 0, 32, 1, 31.48)]  // DET (1 fumble, 0 lost)
    public void Validation_Mahomes_WeeklyFpts(int week, int passYd, int passTd, int passInt, int rushYd, int rushTd, decimal expectedFpts)
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "mahomes",
            PlayerDisplayName = "Patrick Mahomes",
            Position = "QB",
            Season = 2025,
            Week = week,
            PassingYards = passYd,
            PassingTds = passTd,
            PassingInterceptions = passInt,
            RushingYards = rushYd,
            RushingTds = rushTd,
        };

        var result = scorer.ScoreWeekly(stats);

        result.TotalPoints.Should().Be(expectedFpts, $"Mahomes Week {week} should match Sleeper FPTS");
    }

    [Theory]
    [InlineData(1, 60, 1, 4, 24, 0, 14.40)]   // DAL
    [InlineData(2, 88, 1, 2, 6, 0, 15.40)]    // @KC
    [InlineData(3, 46, 0, 4, 9, 0, 5.50)]     // LAR
    [InlineData(4, 43, 1, 4, 31, 0, 13.40)]   // @TB
    [InlineData(5, 30, 0, 3, 58, 1, 14.80)]   // DEN
    [InlineData(6, 58, 0, 2, 9, 0, 6.70)]     // @NYG
    public void Validation_Barkley_WeeklyFpts(int week, int rushYd, int rushTd, int rec, int recYd, int recTd, decimal expectedFpts)
    {
        var scorer = CreateStandard();
        var stats = new WeeklyPlayerStats
        {
            PlayerId = "barkley",
            PlayerDisplayName = "Saquon Barkley",
            Position = "RB",
            Season = 2025,
            Week = week,
            RushingYards = rushYd,
            RushingTds = rushTd,
            Receptions = rec,
            ReceivingYards = recYd,
            ReceivingTds = recTd,
        };

        var result = scorer.ScoreWeekly(stats);

        result.TotalPoints.Should().Be(expectedFpts, $"Barkley Week {week} should match Sleeper FPTS");
    }
}
