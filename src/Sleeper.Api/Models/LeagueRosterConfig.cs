namespace Sleeper.Api.Models;

/// <summary>
/// Parsed league roster configuration: teams, starters per position, bench, total.
/// Derived from League.RosterPositions.
/// </summary>
public record LeagueRosterConfig(
    int Teams,
    Dictionary<string, int> StarterSlots,
    int FlexSlots,
    int BenchSlots,
    int TotalRosterSize,
    int MaxKeepers,
    List<IReadOnlySet<string>>? FlexSlotEligibilities = null
)
{
    public static IReadOnlySet<string> DefaultFlexEligiblePositions { get; } =
        new HashSet<string>(["RB", "WR"], StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> FlexEligiblePositions { get; init; } = DefaultFlexEligiblePositions;

    /// <summary>
    /// Parse roster configuration from a League object.
    /// </summary>
    public static LeagueRosterConfig FromLeague(League league)
    {
        var positions = league.RosterPositions ?? [];
        var starters = new Dictionary<string, int>();
        var flexEligibilities = new List<IReadOnlySet<string>>();
        int flex = 0;
        int bench = 0;

        foreach (var slot in positions)
        {
            var upper = slot.ToUpperInvariant();
            switch (upper)
            {
                case "QB":
                case "RB":
                case "WR":
                case "TE":
                case "K":
                case "DEF":
                    starters[upper] = starters.GetValueOrDefault(upper) + 1;
                    break;
                case "BN":
                    bench++;
                    break;
                default:
                    if (upper.Contains("FLEX") || upper.Contains("SUPER"))
                    {
                        flex++;
                        flexEligibilities.Add(upper switch
                        {
                            "SUPER_FLEX" => new HashSet<string>(["QB", "RB", "WR", "TE"]),
                            "WRRB_FLEX" => new HashSet<string>(["WR", "RB"]),
                            "REC_FLEX" => new HashSet<string>(["WR", "TE"]),
                            _ => new HashSet<string>(["RB", "WR", "TE"])
                        });
                    }
                    break;
            }
        }

        // Extract max_keepers from settings
        int maxKeepers = 0;
        if (league.Settings is not null &&
            league.Settings.TryGetValue("max_keepers", out var mk))
        {
            maxKeepers = mk.ValueKind == System.Text.Json.JsonValueKind.Number ? mk.GetInt32() : 0;
        }

        return new LeagueRosterConfig(
            Teams: league.TotalRosters,
            StarterSlots: starters,
            FlexSlots: flex,
            BenchSlots: bench,
            TotalRosterSize: positions.Count,
            MaxKeepers: maxKeepers,
            FlexSlotEligibilities: flexEligibilities
        )
        {
            FlexEligiblePositions = flexEligibilities.Count > 0
                ? flexEligibilities.SelectMany(positions => positions).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : DefaultFlexEligiblePositions
        };
    }

    /// <summary>
    /// Get effective starter count for a position including FLEX eligibility.
    /// FLEX slots count for each eligible position.
    /// </summary>
    public int GetEffectiveStarters(string position)
    {
        var pos = position.ToUpperInvariant();
        var direct = StarterSlots.GetValueOrDefault(pos);

        if (IsFlexEligible(pos))
            return direct + FlexSlots;

        return direct;
    }

    public bool IsFlexEligible(string position) =>
        FlexEligiblePositions.Contains(position.ToUpperInvariant());

    private static IReadOnlyList<string> FlexEligiblePositionsForSlot(string slot) => slot switch
    {
        "WRRB_FLEX" or "RBWR_FLEX" => ["RB", "WR"],
        "REC_FLEX" or "WRTE_FLEX" or "TEWR_FLEX" => ["WR", "TE"],
        "SUPER_FLEX" or "SUPERFLEX" => ["QB", "RB", "WR", "TE"],
        "FLEX" => ["RB", "WR", "TE"],
        _ when slot.Contains("WRRB", StringComparison.OrdinalIgnoreCase) => ["RB", "WR"],
        _ when slot.Contains("REC", StringComparison.OrdinalIgnoreCase) => ["WR", "TE"],
        _ when slot.Contains("SUPER", StringComparison.OrdinalIgnoreCase) => ["QB", "RB", "WR", "TE"],
        _ => DefaultFlexEligiblePositions.ToArray()
    };
}
