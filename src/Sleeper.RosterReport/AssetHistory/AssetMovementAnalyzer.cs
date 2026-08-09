using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;
using Sleeper.Api.Models;

namespace Sleeper.RosterReport.AssetHistory;

internal static partial class AssetMovementAnalyzer
{
    [GeneratedRegex(@"\((QB|RB|WR|TE|K|DEF),", RegexOptions.IgnoreCase)]
    private static partial Regex PositionPattern();

    public static async Task<IReadOnlyList<AssetRosterWeek>> LoadRosterWeeksAsync(
        string seasonDirectory,
        CancellationToken ct = default)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var weeks = new List<AssetRosterWeek>();
        foreach (var file in Directory.GetFiles(seasonDirectory, "week-*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(file);
            var week = await JsonSerializer.DeserializeAsync<AssetRosterWeek>(stream, options, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Could not deserialize roster snapshot '{file}'.");
            if (week.Teams.Any(team => team.MatchupId is not null))
                weeks.Add(week);
        }
        return weeks.OrderBy(week => week.Week).ToList();
    }

    public static AssetMovementAudit Analyze(
        int season,
        string leagueId,
        IReadOnlyList<AssetRosterWeek> weeks,
        IReadOnlyList<WeeklyTransactions> transactionWeeks,
        IReadOnlyDictionary<string, string>? playerLabels = null)
    {
        if (weeks.Count < 2)
            throw new InvalidOperationException("Asset movement audit requires at least two matchup weeks.");
        var initialWeek = weeks.OrderBy(week => week.Week).First();
        var futureWeeks = weeks.Where(week => week.Week > initialWeek.Week).OrderBy(week => week.Week).ToList();
        var transactions = transactionWeeks
            .SelectMany(value => value.Transactions.Select(transaction => new TransactionAtWeek(value.Week, transaction)))
            .Where(value => string.Equals(value.Transaction.Status, "complete", StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.Transaction.StatusUpdated ?? value.Transaction.Created ?? 0)
            .ToList();

        var assets = initialWeek.Teams.SelectMany(team => team.Roster.Select(player =>
            AnalyzeAsset(team.RosterId, player, futureWeeks, transactions, playerLabels))).ToList();
        var transitions = assets
            .GroupBy(asset => asset.ExitType)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new AssetMovementAudit(
            season,
            leagueId,
            initialWeek.Week,
            futureWeeks.Max(week => week.Week),
            assets.Count,
            assets.Sum(asset => asset.OriginalRosterStarts),
            assets.Sum(asset => asset.OtherRosterStarts),
            transitions,
            assets);
    }

    public static string BuildText(AssetMovementAudit audit)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Asset movement audit: {audit.Season}");
        builder.AppendLine($"League: {audit.LeagueId}");
        builder.AppendLine($"Week 1 assets: {audit.InitialAssetCount}");
        builder.AppendLine($"Exit classes: {string.Join(", ", audit.ExitCounts.OrderBy(value => value.Key).Select(value => $"{value.Key}={value.Value}"))}");
        builder.AppendLine($"Starts for original roster: {audit.OriginalRosterStarts}");
        builder.AppendLine($"Starts for another roster: {audit.OtherRosterStarts}");
        builder.AppendLine();
        builder.AppendLine("Traded Week 1 assets");
        foreach (var asset in audit.Assets.Where(asset => asset.ExitType == "Trade")
                     .OrderBy(asset => asset.ExitTransactionWeek)
                     .ThenBy(asset => asset.PlayerLabel))
        {
            var players = string.Join(", ", asset.Compensation?.ReceivedPlayers.Select(player => player.Label) ?? []);
            var picks = string.Join(", ", asset.Compensation?.ReceivedDraftPicks.Select(pick => $"{pick.Season} R{pick.Round}") ?? []);
            builder.AppendLine($"- W{asset.ExitTransactionWeek}: {asset.PlayerLabel} R{asset.OriginalRosterId}->R{asset.RecipientRosterId}; " +
                               $"starts original/elsewhere {asset.OriginalRosterStarts}/{asset.OtherRosterStarts}; " +
                               $"sender package received players [{players}], picks [{picks}].");
        }
        return builder.ToString();
    }

    private static AssetMovementRecord AnalyzeAsset(
        int originalRosterId,
        AssetRosterPlayer initialPlayer,
        IReadOnlyList<AssetRosterWeek> futureWeeks,
        IReadOnlyList<TransactionAtWeek> transactions,
        IReadOnlyDictionary<string, string>? playerLabels)
    {
        var originalStarts = 0;
        var otherStarts = 0;
        int? firstOtherRosterId = null;
        int? firstOtherRosterWeek = null;
        var lastOriginalRosterWeek = 1;
        foreach (var week in futureWeeks)
        {
            var owners = week.Teams
                .Where(team => team.Roster.Any(player => string.Equals(player.PlayerId, initialPlayer.PlayerId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var owner in owners)
            {
                var player = owner.Roster.First(value => string.Equals(value.PlayerId, initialPlayer.PlayerId, StringComparison.OrdinalIgnoreCase));
                if (owner.RosterId == originalRosterId)
                {
                    lastOriginalRosterWeek = week.Week;
                    if (player.Started)
                        originalStarts++;
                }
                else
                {
                    firstOtherRosterId ??= owner.RosterId;
                    firstOtherRosterWeek ??= week.Week;
                    if (player.Started)
                        otherStarts++;
                }
            }
        }

        var exit = transactions.FirstOrDefault(value =>
            value.Transaction.Drops?.TryGetValue(initialPlayer.PlayerId, out var sender) == true && sender == originalRosterId);
        var exitType = ExitType(exit?.Transaction.Type, firstOtherRosterId, lastOriginalRosterWeek, futureWeeks.Max(week => week.Week));
        int? transactionRecipient = null;
        if (exit?.Transaction.Adds?.TryGetValue(initialPlayer.PlayerId, out var recipientRosterId) == true)
            transactionRecipient = recipientRosterId;
        var recipient = transactionRecipient ?? firstOtherRosterId;
        var compensation = exitType == "Trade" && exit is not null
            ? BuildCompensation(exit.Transaction, originalRosterId, playerLabels)
            : null;

        return new AssetMovementRecord(
            initialPlayer.PlayerId,
            initialPlayer.Label,
            ParsePosition(initialPlayer.Label),
            originalRosterId,
            initialPlayer.Started ? "Starter" : "Bench",
            originalStarts,
            otherStarts,
            lastOriginalRosterWeek,
            firstOtherRosterWeek,
            recipient,
            exit?.Week,
            exit?.Transaction.TransactionId,
            exitType,
            compensation);
    }

    private static string ExitType(string? transactionType, int? otherRosterId, int lastOriginalWeek, int finalWeek)
    {
        if (string.Equals(transactionType, "trade", StringComparison.OrdinalIgnoreCase))
            return "Trade";
        if (transactionType is "waiver" or "free_agent")
            return otherRosterId is null ? "Dropped" : "WaiverOrFreeAgentMove";
        if (otherRosterId is not null)
            return "UnresolvedMove";
        return lastOriginalWeek == finalWeek ? "Retained" : "DisappearedWithoutTransaction";
    }

    private static TradeCompensation BuildCompensation(
        Transaction transaction,
        int senderRosterId,
        IReadOnlyDictionary<string, string>? playerLabels) =>
        new(
            ReceivedPlayers: (transaction.Adds ?? [])
                .Where(value => value.Value == senderRosterId)
                .Select(value => new AssetPlayerRef(value.Key, playerLabels?.GetValueOrDefault(value.Key) ?? value.Key))
                .OrderBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ReceivedDraftPicks: (transaction.DraftPicks ?? [])
                .Where(pick => pick.OwnerId == senderRosterId)
                .Select(pick => new AssetDraftPick(pick.Season, pick.Round, pick.RosterId, pick.PreviousOwnerId, pick.OwnerId))
                .ToList(),
            FaabReceived: (transaction.WaiverBudget ?? []).Where(value => value.Receiver == senderRosterId).Sum(value => value.Amount),
            PackageRosterIds: (transaction.RosterIds ?? []).Distinct().Order().ToList());

    private static string? ParsePosition(string label)
    {
        var match = PositionPattern().Match(label);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private sealed record TransactionAtWeek(int Week, Transaction Transaction);
}

internal sealed record WeeklyTransactions(int Week, IReadOnlyList<Transaction> Transactions);

internal sealed record AssetMovementAudit(
    int Season,
    string LeagueId,
    int InitialWeek,
    int FinalWeek,
    int InitialAssetCount,
    int OriginalRosterStarts,
    int OtherRosterStarts,
    IReadOnlyDictionary<string, int> ExitCounts,
    IReadOnlyList<AssetMovementRecord> Assets);

internal sealed record AssetMovementRecord(
    string PlayerId,
    string PlayerLabel,
    string? Position,
    int OriginalRosterId,
    string InitialRole,
    int OriginalRosterStarts,
    int OtherRosterStarts,
    int LastOriginalRosterWeek,
    int? FirstOtherRosterWeek,
    int? RecipientRosterId,
    int? ExitTransactionWeek,
    string? ExitTransactionId,
    string ExitType,
    TradeCompensation? Compensation);

internal sealed record TradeCompensation(
    IReadOnlyList<AssetPlayerRef> ReceivedPlayers,
    IReadOnlyList<AssetDraftPick> ReceivedDraftPicks,
    int FaabReceived,
    IReadOnlyList<int> PackageRosterIds);

internal sealed record AssetPlayerRef(string PlayerId, string Label);

internal sealed record AssetDraftPick(string? Season, int Round, int OriginalRosterId, int PreviousOwnerId, int OwnerId);

internal sealed record AssetRosterWeek(
    [property: JsonPropertyName("season")] string Season,
    [property: JsonPropertyName("week")] int Week,
    [property: JsonPropertyName("teams")] IReadOnlyList<AssetRosterTeam> Teams);

internal sealed record AssetRosterTeam(
    [property: JsonPropertyName("roster_id")] int RosterId,
    [property: JsonPropertyName("matchup_id")] int? MatchupId,
    [property: JsonPropertyName("roster")] IReadOnlyList<AssetRosterPlayer> Roster);

internal sealed record AssetRosterPlayer(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("started")] bool Started);