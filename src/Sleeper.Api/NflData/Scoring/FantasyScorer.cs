using Sleeper.Api.NflData.Models;

namespace Sleeper.Api.NflData.Scoring;

/// <summary>
/// Calculates fantasy points for a player's stats using a league's scoring settings.
/// Supports QB, RB, WR, TE, and K positions.
/// </summary>
public class FantasyScorer
{
    private readonly Dictionary<string, decimal> _scoringSettings;

    /// <summary>
    /// Human-readable labels for Sleeper scoring keys.
    /// </summary>
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["pass_yd"] = "Passing Yards",
        ["pass_td"] = "Passing TDs",
        ["pass_int"] = "Interceptions Thrown",
        ["pass_2pt"] = "Passing 2PT",
        ["rush_yd"] = "Rushing Yards",
        ["rush_td"] = "Rushing TDs",
        ["rush_2pt"] = "Rushing 2PT",
        ["rec"] = "Receptions",
        ["rec_yd"] = "Receiving Yards",
        ["rec_td"] = "Receiving TDs",
        ["rec_2pt"] = "Receiving 2PT",
        ["fum_lost"] = "Fumbles Lost",
        ["fum"] = "Fumbles",
        ["st_td"] = "Special Teams TDs",
        ["fgm_0_19"] = "FG Made 0-19",
        ["fgm_20_29"] = "FG Made 20-29",
        ["fgm_30_39"] = "FG Made 30-39",
        ["fgm_40_49"] = "FG Made 40-49",
        ["fgm_50p"] = "FG Made 50+",
        ["fgmiss"] = "FG Missed",
        ["xpm"] = "XP Made",
        ["xpmiss"] = "XP Missed",
    };

    public FantasyScorer(Dictionary<string, decimal> scoringSettings)
    {
        _scoringSettings = scoringSettings;
    }

    /// <summary>
    /// Score a weekly stat line.
    /// </summary>
    public ScoredPlayer ScoreWeekly(WeeklyPlayerStats stats)
    {
        var statMap = ExtractStats(stats);
        return Score(stats.PlayerId, stats.PlayerDisplayName ?? stats.PlayerName, stats.Position, statMap);
    }

    /// <summary>
    /// Score a season stat line.
    /// </summary>
    public ScoredPlayer ScoreSeason(SeasonPlayerStats stats)
    {
        var statMap = ExtractStats(stats);
        return Score(stats.PlayerId, stats.PlayerDisplayName ?? stats.PlayerName, stats.Position, statMap);
    }

    private ScoredPlayer Score(string playerId, string? playerName, string? position, Dictionary<string, decimal> statMap)
    {
        var breakdown = new List<ScoreComponent>();

        foreach (var (key, weight) in _scoringSettings)
        {
            if (weight == 0m) continue;
            if (!statMap.TryGetValue(key, out var statValue) || statValue == 0m) continue;

            var points = statValue * weight;
            var label = Labels.GetValueOrDefault(key, key);

            breakdown.Add(new ScoreComponent(
                Category: key,
                Label: label,
                StatValue: statValue,
                PointsPerUnit: weight,
                Points: Math.Round(points, 2)
            ));
        }

        var total = Math.Round(breakdown.Sum(b => b.Points), 2);

        return new ScoredPlayer(
            PlayerId: playerId,
            PlayerName: playerName,
            Position: position,
            TotalPoints: total,
            Breakdown: breakdown.OrderByDescending(b => Math.Abs(b.Points)).ToList()
        );
    }

    /// <summary>
    /// Extract Sleeper scoring key → stat value from a weekly stats object.
    /// </summary>
    private static Dictionary<string, decimal> ExtractStats(WeeklyPlayerStats s)
    {
        var map = new Dictionary<string, decimal>();

        // Passing
        AddIfNotNull(map, "pass_yd", s.PassingYards);
        AddIfNotNull(map, "pass_td", s.PassingTds);
        AddIfNotNull(map, "pass_int", s.PassingInterceptions);
        AddIfNotNull(map, "pass_2pt", s.Passing2PtConversions);

        // Rushing
        AddIfNotNull(map, "rush_yd", s.RushingYards);
        AddIfNotNull(map, "rush_td", s.RushingTds);
        AddIfNotNull(map, "rush_2pt", s.Rushing2PtConversions);

        // Receiving
        AddIfNotNull(map, "rec", s.Receptions);
        AddIfNotNull(map, "rec_yd", s.ReceivingYards);
        AddIfNotNull(map, "rec_td", s.ReceivingTds);
        AddIfNotNull(map, "rec_2pt", s.Receiving2PtConversions);

        // Fumbles — combine all sources
        var totalFumblesLost = (s.RushingFumblesLost ?? 0) + (s.ReceivingFumblesLost ?? 0) + (s.SackFumblesLost ?? 0);
        if (totalFumblesLost != 0) map["fum_lost"] = totalFumblesLost;

        var totalFumbles = (s.RushingFumbles ?? 0) + (s.ReceivingFumbles ?? 0) + (s.SackFumbles ?? 0);
        if (totalFumbles != 0) map["fum"] = totalFumbles;

        // Special Teams
        AddIfNotNull(map, "st_td", s.SpecialTeamsTds);

        // Kicking
        AddIfNotNull(map, "fgm_0_19", s.FgMade0_19);
        AddIfNotNull(map, "fgm_20_29", s.FgMade20_29);
        AddIfNotNull(map, "fgm_30_39", s.FgMade30_39);
        AddIfNotNull(map, "fgm_40_49", s.FgMade40_49);
        var fg50Plus = (s.FgMade50_59 ?? 0) + (s.FgMade60Plus ?? 0);
        if (fg50Plus != 0) map["fgm_50p"] = fg50Plus;
        AddIfNotNull(map, "fgmiss", s.FgMissed);
        AddIfNotNull(map, "xpm", s.PatMade);
        AddIfNotNull(map, "xpmiss", s.PatMissed);

        return map;
    }

    /// <summary>
    /// Extract Sleeper scoring key → stat value from a season stats object.
    /// </summary>
    private static Dictionary<string, decimal> ExtractStats(SeasonPlayerStats s)
    {
        var map = new Dictionary<string, decimal>();

        // Passing
        AddIfNotNull(map, "pass_yd", s.PassingYards);
        AddIfNotNull(map, "pass_td", s.PassingTds);
        AddIfNotNull(map, "pass_int", s.PassingInterceptions);
        AddIfNotNull(map, "pass_2pt", s.Passing2PtConversions);

        // Rushing
        AddIfNotNull(map, "rush_yd", s.RushingYards);
        AddIfNotNull(map, "rush_td", s.RushingTds);
        AddIfNotNull(map, "rush_2pt", s.Rushing2PtConversions);

        // Receiving
        AddIfNotNull(map, "rec", s.Receptions);
        AddIfNotNull(map, "rec_yd", s.ReceivingYards);
        AddIfNotNull(map, "rec_td", s.ReceivingTds);
        AddIfNotNull(map, "rec_2pt", s.Receiving2PtConversions);

        // Fumbles — combine all sources
        var totalFumblesLost = (s.RushingFumblesLost ?? 0) + (s.ReceivingFumblesLost ?? 0) + (s.SackFumblesLost ?? 0);
        if (totalFumblesLost != 0) map["fum_lost"] = totalFumblesLost;

        var totalFumbles = (s.RushingFumbles ?? 0) + (s.ReceivingFumbles ?? 0) + (s.SackFumbles ?? 0);
        if (totalFumbles != 0) map["fum"] = totalFumbles;

        // Special Teams
        AddIfNotNull(map, "st_td", s.SpecialTeamsTds);

        // Kicking
        AddIfNotNull(map, "fgm_0_19", s.FgMade0_19);
        AddIfNotNull(map, "fgm_20_29", s.FgMade20_29);
        AddIfNotNull(map, "fgm_30_39", s.FgMade30_39);
        AddIfNotNull(map, "fgm_40_49", s.FgMade40_49);
        var fg50Plus = (s.FgMade50_59 ?? 0) + (s.FgMade60Plus ?? 0);
        if (fg50Plus != 0) map["fgm_50p"] = fg50Plus;
        AddIfNotNull(map, "fgmiss", s.FgMissed);
        AddIfNotNull(map, "xpm", s.PatMade);
        AddIfNotNull(map, "xpmiss", s.PatMissed);

        return map;
    }

    private static void AddIfNotNull(Dictionary<string, decimal> map, string key, int? value)
    {
        if (value.HasValue && value.Value != 0) map[key] = value.Value;
    }

    private static void AddIfNotNull(Dictionary<string, decimal> map, string key, decimal? value)
    {
        if (value.HasValue && value.Value != 0m) map[key] = value.Value;
    }
}
