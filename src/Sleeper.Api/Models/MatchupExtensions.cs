namespace Sleeper.Api.Models;

public static class MatchupExtensions
{
    public static decimal ScoreOrZero(this Matchup matchup)
        => matchup.CustomPoints ?? matchup.Points ?? 0m;

    public static bool HasScoringData(this Matchup matchup)
    {
        if (matchup.CustomPoints.HasValue)
            return true;

        if (!matchup.Points.HasValue)
            return false;

        if (matchup.Points.Value != 0m)
            return true;

        if (matchup.PlayersPoints?.Values.Any(points => points != 0m) == true)
            return true;

        return matchup.StartersPoints?.Any(points => points != 0m) == true;
    }
}
