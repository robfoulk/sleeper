using FluentAssertions;
using Sleeper.Api.NflData.Analytics;

namespace Sleeper.Api.Tests;

public class AnalyticsTests
{
    // ===== WeightedPpgCalculator =====

    [Fact]
    public void WeightedPpg_ThreeSeasons_WeightsRecencyHigher()
    {
        var seasons = new List<SeasonSummary>
        {
            new(2025, 17, 300m, 17.65m, 5m),  // most recent, weight 3
            new(2024, 17, 250m, 14.71m, 4m),  // weight 2
            new(2023, 16, 200m, 12.50m, 5m),  // weight 1
        };

        var wPpg = WeightedPpgCalculator.Calculate(seasons);

        // (17.65*3 + 14.71*2 + 12.50*1) / 6 = (52.95 + 29.42 + 12.50) / 6 = 15.81
        wPpg.Should().Be(15.81m);
    }

    [Fact]
    public void WeightedPpg_ExcludesLowGameSeasons()
    {
        var seasons = new List<SeasonSummary>
        {
            new(2025, 17, 300m, 17.65m, 5m),
            new(2024, 2, 50m, 25.0m, 3m),   // only 2 games — excluded
            new(2023, 16, 200m, 12.50m, 5m),
        };

        var wPpg = WeightedPpgCalculator.Calculate(seasons, minGames: 4);

        // Only 2025 (w=3) and 2023 (w=2) eligible
        // (17.65*3 + 12.50*2) / 5 = (52.95 + 25.0) / 5 = 15.59
        wPpg.Should().Be(15.59m);
    }

    [Fact]
    public void WeightedPpg_SingleSeason()
    {
        var seasons = new List<SeasonSummary>
        {
            new(2025, 17, 272m, 16.0m, 4m),
        };

        WeightedPpgCalculator.Calculate(seasons).Should().Be(16.0m);
    }

    [Fact]
    public void WeightedPpg_NoEligibleSeasons_ReturnsZero()
    {
        var seasons = new List<SeasonSummary>
        {
            new(2025, 1, 15m, 15.0m, 0m),
        };

        WeightedPpgCalculator.Calculate(seasons, minGames: 4).Should().Be(0m);
    }

    // ===== AgingCurve =====

    [Theory]
    [InlineData("QB", 28, 1.00)]
    [InlineData("QB", 36, 0.90)]
    [InlineData("RB", 24, 1.03)]
    [InlineData("RB", 28, 0.80)]
    [InlineData("RB", 30, 0.58)]
    [InlineData("WR", 27, 1.03)]
    [InlineData("WR", 31, 0.84)]
    [InlineData("TE", 28, 1.02)]
    [InlineData("K", 30, 1.00)]
    public void AgingCurve_ReturnsExpectedFactors(string pos, int age, decimal expected)
    {
        AgingCurve.GetFactor(pos, age).Should().Be(expected);
    }

    [Fact]
    public void AgingCurve_RBDeclinesSteeply()
    {
        var age24 = AgingCurve.GetFactor("RB", 24);
        var age28 = AgingCurve.GetFactor("RB", 28);
        var age31 = AgingCurve.GetFactor("RB", 31);

        age24.Should().BeGreaterThan(age28);
        age28.Should().BeGreaterThan(age31);
        (age24 - age31).Should().BeGreaterThan(0.5m); // massive drop
    }

    [Fact]
    public void AgingCurve_QBPlateausLonger()
    {
        var age28 = AgingCurve.GetFactor("QB", 28);
        var age33 = AgingCurve.GetFactor("QB", 33);

        (age28 - age33).Should().BeLessThan(0.05m); // barely changes
    }

    // ===== VorpCalculator =====

    [Fact]
    public void Vorp_EliteRBHasHighVorp()
    {
        var vorp = VorpCalculator.Calculate(18.0m, "RB");
        vorp.Should().Be(10.5m); // 18.0 - 7.5 replacement
    }

    [Fact]
    public void Vorp_AverageQBHasLowVorp()
    {
        var vorp = VorpCalculator.Calculate(15.0m, "QB");
        vorp.Should().Be(1.0m); // 15.0 - 14.0 replacement
    }

    [Fact]
    public void Vorp_BelowReplacementIsNegative()
    {
        var vorp = VorpCalculator.Calculate(5.0m, "RB");
        vorp.Should().Be(-2.5m); // below replacement
    }

    [Fact]
    public void Vorp_CalculateReplacementLevels_FromData()
    {
        var ppgByPos = new Dictionary<string, List<decimal>>
        {
            ["QB"] = [22m, 20m, 18m, 16m, 14m, 12m, 10m, 8m, 6m, 4m],
            ["RB"] = [18m, 16m, 14m, 12m, 10m, 8m, 6m, 4m, 3m, 2m],
        };

        var levels = VorpCalculator.CalculateReplacementLevels(ppgByPos, teams: 4,
            new Dictionary<string, int> { ["QB"] = 1, ["RB"] = 2 });

        levels["QB"].Should().Be(14m);  // QB5 (4 teams * 1 slot = top 4, replacement is #5)
        levels["RB"].Should().Be(3m);   // RB9 (4 teams * 2 slots = top 8, replacement is #9)
    }

    // ===== ConsistencyCalculator =====

    [Fact]
    public void Consistency_ConsistentPlayer_HighScore()
    {
        var points = new List<decimal> { 14, 15, 13, 16, 14, 15, 13, 14, 16, 15 };
        var score = ConsistencyCalculator.CalculateScore(points);
        score.Should().BeGreaterThan(85m);
    }

    [Fact]
    public void Consistency_BoomBustPlayer_LowScore()
    {
        var points = new List<decimal> { 2, 30, 5, 28, 3, 25, 1, 35, 4, 22 };
        var score = ConsistencyCalculator.CalculateScore(points);
        score.Should().BeLessThan(45m);
    }

    [Fact]
    public void BoomRate_CalculatesCorrectly()
    {
        var points = new List<decimal> { 5, 10, 20, 25, 8, 22, 6, 30 };
        var boom = ConsistencyCalculator.CalculateBoomRate(points, positionalAvgPpg: 12m);
        // threshold = 18, booms = 20, 25, 22, 30 = 4/8 = 50%
        boom.Should().Be(50m);
    }

    [Fact]
    public void BustRate_CalculatesCorrectly()
    {
        var points = new List<decimal> { 5, 10, 20, 25, 3, 22, 1, 30 };
        var bust = ConsistencyCalculator.CalculateBustRate(points, positionalAvgPpg: 12m);
        // threshold = 6, busts = 5, 3, 1 = 3/8 = 37.5%
        bust.Should().Be(37.5m);
    }

    // ===== DurabilityCalculator =====

    [Fact]
    public void Durability_IronMan_100Pct()
    {
        var seasons = new List<SeasonSummary>
        {
            new(2025, 17, 300m, 17.65m, 5m),
            new(2024, 17, 280m, 16.47m, 4m),
            new(2023, 17, 260m, 15.29m, 5m),
        };

        DurabilityCalculator.Calculate(seasons).Should().Be(100.0m);
    }

    [Fact]
    public void Durability_InjuryProne_Discounted()
    {
        var seasons = new List<SeasonSummary>
        {
            new(2025, 10, 180m, 18.0m, 5m),
            new(2024, 8, 140m, 17.5m, 4m),
            new(2023, 6, 100m, 16.67m, 5m),
        };

        var pct = DurabilityCalculator.Calculate(seasons);
        pct.Should().BeApproximately(47.1m, 0.1m); // 24/51

        var mult = DurabilityCalculator.GetMultiplier(pct);
        mult.Should().BeLessThan(0.5m); // severe discount
    }

    [Fact]
    public void Durability_Multiplier_NoDiscountAbove85()
    {
        DurabilityCalculator.GetMultiplier(90m).Should().Be(1.0m);
        DurabilityCalculator.GetMultiplier(100m).Should().Be(1.0m);
    }

    // ===== TrendCalculator =====

    [Fact]
    public void Trend_Ascending_Positive()
    {
        var seasons = new List<SeasonSummary>
        {
            new(2023, 16, 160m, 10.0m, 4m),
            new(2024, 17, 221m, 13.0m, 4m),
            new(2025, 17, 272m, 16.0m, 5m),
        };

        var trend = TrendCalculator.CalculateTrendPerYear(seasons);
        trend.Should().Be(2.4m); // 3.0 raw * 0.8 dampening (3 seasons)
        TrendCalculator.GetDirection(trend).Should().Be("Strong Rise");
    }

    [Fact]
    public void Trend_Declining_Negative()
    {
        var seasons = new List<SeasonSummary>
        {
            new(2023, 17, 306m, 18.0m, 5m),
            new(2024, 16, 224m, 14.0m, 4m),
            new(2025, 15, 150m, 10.0m, 5m),
        };

        var trend = TrendCalculator.CalculateTrendPerYear(seasons);
        trend.Should().Be(-3.2m); // -4.0 raw * 0.8 dampening (3 seasons)
        TrendCalculator.GetDirection(trend).Should().Be("Sharp Decline");
    }

    [Fact]
    public void Trend_Stable_NearZero()
    {
        var seasons = new List<SeasonSummary>
        {
            new(2023, 17, 255m, 15.0m, 4m),
            new(2024, 17, 258m, 15.2m, 4m),
            new(2025, 17, 252m, 14.8m, 5m),
        };

        var trend = TrendCalculator.CalculateTrendPerYear(seasons);
        trend.Should().BeInRange(-0.5m, 0.5m);
        TrendCalculator.GetDirection(trend).Should().Be("Stable");
    }

    // ===== SurplusCalculator =====

    [Fact]
    public void Surplus_LateRoundKeeper_WithHighProjection()
    {
        // Player projects 15 PPG, kept at round 14 (draft value ~3.5 PPG)
        var surplus = SurplusCalculator.Calculate(15.0m, 14);
        surplus.Should().Be(11.5m); // massive surplus
    }

    [Fact]
    public void Surplus_EarlyRoundKeeper_WithMatchingProjection()
    {
        // Player projects 14 PPG, kept at round 2 (draft value ~14 PPG)
        var surplus = SurplusCalculator.Calculate(14.0m, 2);
        surplus.Should().Be(0m); // no surplus — just paying market price
    }

    [Fact]
    public void Surplus_NegativeSurplus_DontKeep()
    {
        // Player projects 8 PPG, kept at round 3 (draft value ~12.5 PPG)
        var surplus = SurplusCalculator.Calculate(8.0m, 3);
        surplus.Should().Be(-4.5m); // negative, don't keep
    }

    // ===== KeeperAnalyzer (Composite) =====

    [Fact]
    public void KeeperAnalyzer_EliteRBLateRound_SmashKeep()
    {
        var analyzer = new KeeperAnalyzer();

        var seasons = new List<SeasonSummary>
        {
            new(2025, 17, 289m, 17.0m, 5m),
            new(2024, 16, 240m, 15.0m, 4m),
            new(2023, 17, 221m, 13.0m, 5m),
        };

        var weekly = Enumerable.Range(0, 17).Select(i => 15m + (i % 3) * 2m).ToList();

        var result = analyzer.Analyze(
            "rb1", "Young Stud RB", "RB", 24, keeperCostRound: 12, canBeKept: true,
            seasons, weekly);

        result.KeeperGrade.Should().BeOneOf("S", "A"); // elite keeper
        result.KeeperScore.Should().BeGreaterThan(3.0m);
        result.TrendDirection.Should().Be("Rising");
        result.AgeAdjustedPpg.Should().BeGreaterThan(result.WeightedPpg); // age 24 RB gets boost
    }

    [Fact]
    public void KeeperAnalyzer_AgingRBEarlyRound_DontKeep()
    {
        var analyzer = new KeeperAnalyzer();

        var seasons = new List<SeasonSummary>
        {
            new(2025, 14, 168m, 12.0m, 6m),
            new(2024, 17, 255m, 15.0m, 4m),
            new(2023, 17, 289m, 17.0m, 5m),
        };

        var weekly = Enumerable.Range(0, 14).Select(i => 10m + (i % 5) * 3m).ToList();

        var result = analyzer.Analyze(
            "rb2", "Old Vet RB", "RB", 30, keeperCostRound: 3, canBeKept: true,
            seasons, weekly);

        result.KeeperGrade.Should().BeOneOf("D", "F"); // bad keeper
        result.TrendDirection.Should().Contain("Declin");
        result.AgeAdjustedPpg.Should().BeLessThan(result.WeightedPpg); // age 30 RB gets penalty
    }

    [Fact]
    public void KeeperAnalyzer_EliteQB_GoodButNotGreat()
    {
        var analyzer = new KeeperAnalyzer();

        var seasons = new List<SeasonSummary>
        {
            new(2025, 17, 357m, 21.0m, 6m),
            new(2024, 17, 340m, 20.0m, 5m),
            new(2023, 16, 304m, 19.0m, 5m),
        };

        var weekly = Enumerable.Range(0, 17).Select(i => 19m + (i % 4) * 1.5m).ToList();

        var result = analyzer.Analyze(
            "qb1", "Elite QB", "QB", 29, keeperCostRound: 8, canBeKept: true,
            seasons, weekly);

        // QB has lower VORP because replacement QBs are good
        result.Vorp.Should().BeLessThan(10m);
        result.KeeperGrade.Should().NotBe("F");
    }

    [Fact]
    public void KeeperAnalyzer_CannotBeKept_ZeroScore()
    {
        var analyzer = new KeeperAnalyzer();

        var seasons = new List<SeasonSummary>
        {
            new(2025, 17, 340m, 20.0m, 5m),
        };

        var result = analyzer.Analyze(
            "p1", "Stud", "RB", 25, keeperCostRound: null, canBeKept: false,
            seasons, []);

        result.KeeperScore.Should().Be(0m);
        result.CanBeKept.Should().BeFalse();
    }

    [Fact]
    public void KeeperAnalyzer_InjuryPronePlayer_Discounted()
    {
        var analyzer = new KeeperAnalyzer();

        var healthySeasons = new List<SeasonSummary>
        {
            new(2025, 17, 306m, 18.0m, 4m),
            new(2024, 17, 289m, 17.0m, 4m),
            new(2023, 17, 272m, 16.0m, 4m),
        };

        var injurySeasons = new List<SeasonSummary>
        {
            new(2025, 8, 144m, 18.0m, 4m),
            new(2024, 6, 102m, 17.0m, 4m),
            new(2023, 10, 160m, 16.0m, 4m),
        };

        var healthy = analyzer.Analyze("h1", "Healthy RB", "RB", 25, 10, true, healthySeasons, []);
        var injured = analyzer.Analyze("i1", "Injured RB", "RB", 25, 10, true, injurySeasons, []);

        // Same talent, but injured player should have lower projected points and keeper score
        healthy.ProjectedSeasonPoints.Should().BeGreaterThan(injured.ProjectedSeasonPoints);
        healthy.KeeperScore.Should().BeGreaterThan(injured.KeeperScore);
    }
}
