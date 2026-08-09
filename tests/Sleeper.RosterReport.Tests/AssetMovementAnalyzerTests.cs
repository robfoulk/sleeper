using FluentAssertions;
using Sleeper.Api.Models;
using Sleeper.RosterReport.AssetHistory;

namespace Sleeper.RosterReport.Tests;

public class AssetMovementAnalyzerTests
{
    [Fact]
    public void Analyze_TracksTradeCompensationAndPostTransferStarts()
    {
        var weeks = new[]
        {
            Week(1, Team(1, Player("p1", started: true)), Team(2)),
            Week(2, Team(1), Team(2, Player("p1", started: true))),
            Week(3, Team(1), Team(2, Player("p1", started: false)))
        };
        var trade = Transaction(
            "trade-1",
            "trade",
            rosterIds: [1, 2],
            adds: new Dictionary<string, int> { ["p1"] = 2, ["p2"] = 1 },
            drops: new Dictionary<string, int> { ["p1"] = 1, ["p2"] = 2 },
            draftPicks: [new TransactionDraftPick("2026", 2, 2, 2, 1)]);

        var audit = AssetMovementAnalyzer.Analyze(2025, "league", weeks, [new WeeklyTransactions(2, [trade])]);

        var asset = audit.Assets.Single(value => value.PlayerId == "p1");
        asset.ExitType.Should().Be("Trade");
        asset.RecipientRosterId.Should().Be(2);
        asset.OtherRosterStarts.Should().Be(1);
        asset.Compensation!.ReceivedPlayers.Select(player => player.PlayerId).Should().Equal("p2");
        asset.Compensation.ReceivedDraftPicks.Should().ContainSingle(pick => pick.Season == "2026" && pick.Round == 2);
    }

    [Fact]
    public void Analyze_DistinguishesRetainedAndDroppedAssets()
    {
        var weeks = new[]
        {
            Week(1, Team(1, Player("kept", true), Player("cut", false))),
            Week(2, Team(1, Player("kept", true))),
            Week(3, Team(1, Player("kept", true)))
        };
        var drop = Transaction(
            "drop-1",
            "free_agent",
            rosterIds: [1],
            drops: new Dictionary<string, int> { ["cut"] = 1 });

        var audit = AssetMovementAnalyzer.Analyze(2025, "league", weeks, [new WeeklyTransactions(2, [drop])]);

        audit.Assets.Single(value => value.PlayerId == "kept").ExitType.Should().Be("Retained");
        var cut = audit.Assets.Single(value => value.PlayerId == "cut");
        cut.ExitType.Should().Be("Dropped");
        cut.RecipientRosterId.Should().BeNull();
        cut.Compensation.Should().BeNull();
    }

    private static AssetRosterWeek Week(int week, params AssetRosterTeam[] teams) =>
        new("2025", week, teams);

    private static AssetRosterTeam Team(int rosterId, params AssetRosterPlayer[] players) =>
        new(rosterId, 1, players);

    private static AssetRosterPlayer Player(string id, bool started) =>
        new(id, $"{id} (RB, TST)", started);

    private static Transaction Transaction(
        string id,
        string type,
        List<int> rosterIds,
        Dictionary<string, int>? adds = null,
        Dictionary<string, int>? drops = null,
        List<TransactionDraftPick>? draftPicks = null) =>
        new(id, type, "complete", 1, rosterIds, adds, drops, draftPicks, null, 1, rosterIds, 1, null, null, null);
}