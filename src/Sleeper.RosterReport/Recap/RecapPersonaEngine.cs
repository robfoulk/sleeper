using System.Text;

namespace Sleeper.RosterReport.Recap;

public enum GameNarrativeAngle
{
    BlowoutAutopsy,
    NailBiterHeartbreak,
    BenchDisasterMeltdown,
    HighFlyingSlugfest,
    DefensiveGrindout,
    TacticalScout
}

public sealed record MatchupNarrativeStyle(
    GameNarrativeAngle Angle,
    string AngleInstruction,
    string StructureInstruction
);

/// <summary>
/// Provides modular prompt assembly and dynamic narrative angle guidance for game recaps
/// while preserving precomputed fact-card integrity and consistent weekly voice.
/// </summary>
public static class RecapPersonaEngine
{
    public static MatchupNarrativeStyle DetermineStyle(MatchupFactCard card)
    {
        GameNarrativeAngle angle;
        if (card.IsBlowout)
        {
            angle = GameNarrativeAngle.BlowoutAutopsy;
        }
        else if (card.Margin <= 5.0m)
        {
            angle = GameNarrativeAngle.NailBiterHeartbreak;
        }
        else if (card.LoserBenchFlipPossible)
        {
            angle = GameNarrativeAngle.BenchDisasterMeltdown;
        }
        else if (card.WinnerScoreRankInWeek <= 4 && card.LoserScoreRankInWeek <= 4)
        {
            angle = GameNarrativeAngle.HighFlyingSlugfest;
        }
        else if (card.WinnerScoreRankInWeek >= 5 && card.LoserScoreRankInWeek >= 5)
        {
            angle = GameNarrativeAngle.DefensiveGrindout;
        }
        else
        {
            angle = GameNarrativeAngle.TacticalScout;
        }

        return new MatchupNarrativeStyle(
            angle,
            GetAngleInstruction(angle),
            GetStructureInstruction(angle)
        );
    }

    private static string GetAngleInstruction(GameNarrativeAngle angle) => angle switch
    {
        GameNarrativeAngle.BlowoutAutopsy =>
            "NARRATIVE ANGLE (Blowout Autopsy): Focus on total dominance. Highlight how the winner overwhelmed the loser from kickoff to final whistle.",
        GameNarrativeAngle.NailBiterHeartbreak =>
            "NARRATIVE ANGLE (Nail-Biter Heartbreak): Frame as a razor-thin finish where every late yard or stat correction was decisive.",
        GameNarrativeAngle.BenchDisasterMeltdown =>
            "NARRATIVE ANGLE (Bench Disaster Meltdown): Put start/sit decisions under the microscope — points left on the pine cost the game.",
        GameNarrativeAngle.HighFlyingSlugfest =>
            "NARRATIVE ANGLE (High-Flying Slugfest): Highlight offensive firepower and elite individual hero performances from both squads.",
        GameNarrativeAngle.DefensiveGrindout =>
            "NARRATIVE ANGLE (Defensive Grindout): Treat as a sluggish, low-scoring war of attrition where points were hard to come by.",
        _ => "NARRATIVE ANGLE (Tactical Matchup Breakdown): Focus on positional matchups, roster efficiency, and key turning points."
    };

    private static string GetStructureInstruction(GameNarrativeAngle angle) => angle switch
    {
        GameNarrativeAngle.BlowoutAutopsy =>
            "FORMAT & FLOW: Write 2 punchy paragraphs. Start with a bold headline (`**Headline.**`). Para 1: Dominant scoreline & hero performance. Para 2: Summary of the margin and standings fallout.",
        GameNarrativeAngle.NailBiterHeartbreak =>
            "FORMAT & FLOW: Write 2-3 fast-paced paragraphs. Start with a bold headline (`**Headline.**`). Focus on late-game tension, key clutch plays, and close with the exact margin consequence.",
        GameNarrativeAngle.BenchDisasterMeltdown =>
            "FORMAT & FLOW: Write 2-3 paragraphs. Start with a bold headline (`**Headline.**`). Para 1: Game outcome. Para 2: Roster mismanagement & bench points. Para 3: Consequence closer.",
        GameNarrativeAngle.HighFlyingSlugfest =>
            "FORMAT & FLOW: Write 2-3 energetic paragraphs. Start with a bold headline (`**Headline.**`). Celebrate top point-producers before grounding the result in standings impact.",
        GameNarrativeAngle.DefensiveGrindout =>
            "FORMAT & FLOW: Write 2 concise paragraphs. Start with a bold headline (`**Headline.**`). Briefly analyze the low-scoring struggle and name the specific consequence.",
        _ => "FORMAT & FLOW: Write 2-3 clean paragraphs. Start with a bold headline (`**Headline.**`). Provide a balanced recap covering outcome, key roster decisions, and standings impact."
    };
}
