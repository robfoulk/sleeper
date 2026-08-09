using System.Text.Json.Serialization;

namespace Sleeper.Api.Models;

public record Draft(
    [property: JsonPropertyName("draft_id")] string DraftId,
    [property: JsonPropertyName("league_id")] string? LeagueId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("season")] string? Season,
    [property: JsonPropertyName("sport")] string? Sport,
    [property: JsonPropertyName("settings")] DraftSettings? Settings,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata,
    [property: JsonPropertyName("draft_order")] Dictionary<string, int>? DraftOrder,
    [property: JsonPropertyName("slot_to_roster_id")] Dictionary<string, int>? SlotToRosterId,
    [property: JsonPropertyName("start_time")] long? StartTime,
    [property: JsonPropertyName("last_picked")] long? LastPicked,
    [property: JsonPropertyName("last_message_time")] long? LastMessageTime,
    [property: JsonPropertyName("last_message_id")] string? LastMessageId,
    [property: JsonPropertyName("creators")] List<string>? Creators,
    [property: JsonPropertyName("created")] long? Created
);

public record DraftSettings(
    [property: JsonPropertyName("teams")] int Teams,
    [property: JsonPropertyName("rounds")] int Rounds,
    [property: JsonPropertyName("pick_timer")] int PickTimer,
    [property: JsonPropertyName("slots_qb")] int SlotsQb,
    [property: JsonPropertyName("slots_rb")] int SlotsRb,
    [property: JsonPropertyName("slots_wr")] int SlotsWr,
    [property: JsonPropertyName("slots_te")] int SlotsTe,
    [property: JsonPropertyName("slots_flex")] int SlotsFlex,
    [property: JsonPropertyName("slots_k")] int SlotsK,
    [property: JsonPropertyName("slots_def")] int SlotsDef,
    [property: JsonPropertyName("slots_bn")] int SlotsBn
);
