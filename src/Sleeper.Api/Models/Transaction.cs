using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record Transaction(
    [property: JsonPropertyName("transaction_id")] string TransactionId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("status_updated")] long? StatusUpdated,
    [property: JsonPropertyName("roster_ids")] List<int>? RosterIds,
    [property: JsonPropertyName("adds")] Dictionary<string, int>? Adds,
    [property: JsonPropertyName("drops")] Dictionary<string, int>? Drops,
    [property: JsonPropertyName("draft_picks")] List<TransactionDraftPick>? DraftPicks,
    [property: JsonPropertyName("creator")] string? Creator,
    [property: JsonPropertyName("created")] long? Created,
    [property: JsonPropertyName("consenter_ids")] List<int>? ConsenterIds,
    [property: JsonPropertyName("leg")] int? Leg,
    [property: JsonPropertyName("settings")] Dictionary<string, JsonElement>? Settings,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata,
    [property: JsonPropertyName("waiver_budget")] List<WaiverBudgetTransfer>? WaiverBudget
);

public record TransactionDraftPick(
    [property: JsonPropertyName("season")] string? Season,
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("roster_id")] int RosterId,
    [property: JsonPropertyName("previous_owner_id")] int PreviousOwnerId,
    [property: JsonPropertyName("owner_id")] int OwnerId
);

public record WaiverBudgetTransfer(
    [property: JsonPropertyName("sender")] int Sender,
    [property: JsonPropertyName("receiver")] int Receiver,
    [property: JsonPropertyName("amount")] int Amount
);
