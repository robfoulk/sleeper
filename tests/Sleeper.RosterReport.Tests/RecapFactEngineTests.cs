using FluentAssertions;
using Sleeper.RosterReport.Recap;

namespace Sleeper.RosterReport.Tests;

public class RecapFactEngineTests
{
    [Fact]
    public void ComputeMatchupCard_CalculatesRanksMarginAndBenchFlipCorrectly()
    {
        var meta = new RecapMeta("123", "League", 2025, 1, "regular", null, false, 8, "0.5 PPR", DateTimeOffset.UtcNow);
        var owners = new List<OwnerRef>
        {
            new("user1", "user1", "Rob", "Team Rob", 1, "Rob", null),
            new("user2", "user2", "Brian", "Team Brian", 2, "Brian", null)
        };

        var game = new GameRecap(
            MatchupId: 1,
            SeasonContext: "regular",
            PlayoffRound: null,
            Home: new GameSide(1, "user1", "user1", "Team Rob", null, 150.0m, 140.0m, [], [], new PlayerLine("p1", "Josh Allen", "QB", "BUF", "MIA", "Sun", 30.0m, 20.0m, 10.0m, true, false, null, null, null, null, null, null, null, null, null, null, 30.0m), null, 5.0m),
            Away: new GameSide(2, "user2", "user2", "Team Brian", null, 130.0m, 135.0m, [], [], new PlayerLine("p2", "Derrick Henry", "RB", "BAL", "KC", "Sun", 25.0m, 15.0m, 10.0m, true, false, null, null, null, null, null, null, null, null, null, null, 25.0m), null, 10.0m),
            Margin: 20.0m,
            Blowout: false,
            LineupOptimalityHomePct: 90.0m,
            LineupOptimalityAwayPct: 80.0m,
            StoryHookType: null,
            StoryHookLabel: null,
            StoryImportance: 1,
            StoryImportanceReason: null,
            H2H: null
        );

        var standings = new List<StandingsRow>
        {
            new(1, 0, "user1", "user1", "Team Rob", 1, 0, 0, 150.0m, 130.0m, 20.0m, "W1", 100, 1),
            new(5, 0, "user2", "user2", "Team Brian", 0, 1, 0, 130.0m, 150.0m, -20.0m, "L1", 100, 1)
        };

        var env = new RecapEnvelope(
            meta, "", owners, standings, [], [game],
            new LeagueThemes(null, null, null, [], [], [], []),
            new LookAhead(2, [], [], []), [], []
        );

        var card = RecapFactEngine.ComputeMatchupCard(env, game, null);

        card.WinnerRealName.Should().Be("Rob");
        card.LoserRealName.Should().Be("Brian");
        card.Margin.Should().Be(20.0m);
        card.WinnerScoreRankInWeek.Should().Be(1);
        card.LoserScoreRankInWeek.Should().Be(2);
        card.LineupOptimalityGapExceedsThreshold.Should().BeFalse("gap was 10%, below 15% threshold");
        card.LoserBenchFlipPossible.Should().BeFalse("couldaShoulda was 10.0m, margin was 20.0m");
        card.LoserBenchFlipDetails.Should().Contain("COULD NOT have flipped");
    }

    [Fact]
    public void ComputeForecastCard_GeneratesDeterministicForecastFacts()
    {
        var meta = new RecapMeta("123", "League", 2025, 1, "regular", null, false, 8, "0.5 PPR", DateTimeOffset.UtcNow);
        var owners = new List<OwnerRef>
        {
            new("user1", "user1", "Rob", "Team Rob", 1, "Rob", null),
            new("user2", "user2", "Brian", "Team Brian", 2, "Brian", null)
        };

        var nextMatchup = new NextWeekMatchup(
            MatchupId: 10,
            HomeUserId: "user1",
            HomeOwnerDisplay: "user1",
            HomeTeamName: "Team Rob",
            AwayUserId: "user2",
            AwayOwnerDisplay: "user2",
            AwayTeamName: "Team Brian",
            HomeProjection: 135.5m,
            AwayProjection: 112.0m,
            PowerRankingGap: 2.0m,
            StoryHookType: null,
            StoryHookLabel: null,
            SeedImplication: null,
            Pick: "Team Rob",
            Confidence: "Lock",
            KeyXFactor: "Spread: 23.5 pts (Lock)",
            PlayerNotes: ["Rob's Christian McCaffrey (RB): marquee projection 22.5 PPG."]
        );

        var env = new RecapEnvelope(
            meta, "", owners, [], [], [],
            new LeagueThemes(null, null, null, [], [], [], []),
            new LookAhead(2, [nextMatchup], [], []), [], []
        );

        var forecastCard = RecapFactEngine.ComputeForecastCard(env);

        forecastCard.NextWeek.Should().Be(2);
        forecastCard.MatchupForecasts.Should().HaveCount(1);

        var matchFact = forecastCard.MatchupForecasts[0];
        matchFact.HomeTeamName.Should().Be("Team Rob");
        matchFact.AwayTeamName.Should().Be("Team Brian");
        matchFact.PickTeamName.Should().Be("Team Rob");
        matchFact.HomeProjectedScore.Should().Be(135.5m);
        matchFact.AwayProjectedScore.Should().Be(112.0m);
        matchFact.Margin.Should().Be(23.5m);
        matchFact.Confidence.Should().Be("Lock");
        matchFact.PlayerNotes.Should().ContainSingle();

        var promptBlock = forecastCard.RenderPromptBlock();
        promptBlock.Should().Contain("<ground_truth_forecast>");
        promptBlock.Should().Contain("Team Rob vs Team Brian");
        promptBlock.Should().Contain("135.5 – 112.0");
        promptBlock.Should().Contain("Lock");
    }

    [Fact]
    public void ComputeMatchupCard_RepresentsTieWithoutInventingWinner()
    {
        var meta = new RecapMeta("123", "League", 2025, 1, "regular", null, false, 2, "0.5 PPR", DateTimeOffset.UtcNow);
        var owners = new List<OwnerRef>
        {
            new("user1", "user1", "Rob", "Team Rob", 1, "Rob", null),
            new("user2", "user2", "Brian", "Team Brian", 2, "Brian", null)
        };
        var home = new GameSide(1, "user1", "user1", "Team Rob", null, 100m, null, [], [], null, null, 0m);
        var away = new GameSide(2, "user2", "user2", "Team Brian", null, 100m, null, [], [], null, null, 0m);
        var game = new GameRecap(1, "regular", null, home, away, 0m, false, null, null, null, null, 1, null, null);
        var standings = new List<StandingsRow>
        {
            new(1, 0, "user1", "Rob", "Team Rob", 0, 0, 1, 100m, 100m, 0m, "T1", null, null),
            new(2, 0, "user2", "Brian", "Team Brian", 0, 0, 1, 100m, 100m, 0m, "T1", null, null)
        };
        var env = new RecapEnvelope(
            meta, "", owners, standings, [], [game],
            new LeagueThemes(null, null, null, [], [], [], []),
            new LookAhead(2, [], [], []), [], []);

        var card = RecapFactEngine.ComputeMatchupCard(env, game, null);

        card.IsTie.Should().BeTrue();
        card.LoserBenchFlipPossible.Should().BeFalse();
        card.RenderPromptBlock().Should().Contain("tied **100.00** to **100.00**");
        card.RenderPromptBlock().Should().Contain("Neither team won");
        card.RenderPromptBlock().Should().NotContain(" defeated ");
    }
}
