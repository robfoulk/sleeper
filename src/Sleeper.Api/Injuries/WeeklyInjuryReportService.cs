using System.Globalization;

namespace Sleeper.Api.Injuries;

public sealed class WeeklyInjuryReportService(IInjuryStore injuries, ISleeperClient client)
{
    public async Task<WeeklyInjuryReport> BuildAsync(
        string leagueId, int season, int week, InjuryReportWindow window, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leagueId);
        window.Validate();
        if (season < 1 || week is < 1 or > 18)
            throw new ArgumentException("A positive season and week 1-18 are required.");
        if (window.Start.Year < season || window.End.Year > season + 1)
            throw new ArgumentException("The injury window must fall within the season year or following calendar year.");

        var league = await client.GetLeagueAsync(leagueId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"League {leagueId} not found.");
        if (league.Season != season.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException($"League {leagueId} is for season {league.Season}, not {season}. Use that season's league ID.");
        var matchups = await client.GetLeagueMatchupsAsync(leagueId, week, ct).ConfigureAwait(false);
        if (matchups.Count == 0 || matchups.Any(matchup => matchup.Players is null or { Count: 0 }))
            throw new InvalidOperationException("Weekly matchup rosters are unavailable; refusing to substitute today's rosters.");

        var changes = await injuries.GetChangesAsync(window.Start, window.End, ct).ConfigureAwait(false);
        var cohort = (await injuries.GetCohortAsync(ct).ConfigureAwait(false)).ToDictionary(player => player.SleeperId);
        var rostered = matchups.SelectMany(matchup => matchup.Players!).Where(id => id != "0").ToHashSet();
        var warnings = new List<string>
        {
            "Dates are caller-supplied; verify them against the NFL schedule. No calendar inference is performed.",
            "Source-specific changes are not a reconciled availability snapshot. Sources can disagree; an in-game out tag is not a next-week ruling.",
            "Only ledger evidence is used. No automatic refresh or web research is performed; missing observations do not establish health.",
            "Roster IDs and starter/bench roles come from the requested week's matchup data, not current roster ownership.",
            "Reports are retrospective: late-recorded evidence effective within the window may be included. GeneratedAt is not an as-of cutoff."
        };
        if (window.End > DateTimeOffset.UtcNow)
            warnings.Add("The reporting window is still open; this report is provisional.");
        if (rostered.Any(id => !cohort.ContainsKey(id)))
            warnings.Add("Some rostered players are outside the tracked cohort; coverage is incomplete.");

        var entries = new List<WeeklyInjuryEntry>();
        foreach (var change in changes.OrderBy(change => change.Observation.EffectiveAt ?? change.Observation.ObservedAt)
                     .ThenBy(change => change.Observation.Id))
        {
            var observation = change.Observation;
            var (category, reason) = Classify(change, window);
            foreach (var matchup in matchups.Where(matchup => matchup.Players!.Contains(observation.SleeperId)))
                entries.Add(new WeeklyInjuryEntry(observation.SleeperId,
                    cohort.GetValueOrDefault(observation.SleeperId)?.Name ?? $"Sleeper player {observation.SleeperId}",
                    matchup.RosterId, matchup.Starters?.Contains(observation.SleeperId) == true,
                    category, reason, observation, change.PreviousObservation));
        }
        return new WeeklyInjuryReport(leagueId, season, week, window, DateTimeOffset.UtcNow,
            rostered.Count, rostered.Count(cohort.ContainsKey), entries, warnings);
    }

    public static (WeeklyInjuryCategory Category, string Reason) Classify(InjuryChange change, InjuryReportWindow window)
    {
        var observation = change.Observation;
        var previous = change.PreviousObservation;
        var status = observation.Status.Trim().ToLowerInvariant();
        var description = observation.PrimaryInjury?.Trim().ToLowerInvariant() ?? "";
        if (status is "na" or "suspended" || description.Contains("personal") || description.Contains("coach's decision") ||
            description.Contains("not injury") || description is "rest" or "rest day" or "veteran rest")
            return (WeeklyInjuryCategory.NonInjuryAbsence, "Non-injury availability or rest designation; not a medical injury.");
        if (status is "healthy" or "active")
        {
            if (previous is not null && previous.Status is not ("healthy" or "active") &&
                string.IsNullOrWhiteSpace(observation.PrimaryInjury) && string.IsNullOrWhiteSpace(observation.SecondaryInjury))
                return (WeeklyInjuryCategory.Recovery, "Concern cleared in this source only; this is not independent medical clearance.");
            return (WeeklyInjuryCategory.UncertainTiming, "Active status or practice change alone does not establish injury onset or recovery.");
        }

        var evidenced = observation.InjuryOccurredAt.HasValue && observation.SourcePublishedAt.HasValue &&
            observation.InjuryOccurredAt <= observation.SourcePublishedAt &&
            observation.SourcePublishedAt < window.End &&
            observation.Confidence.ToLowerInvariant() is "official" or "reporter" &&
            Uri.TryCreate(observation.SourceUrl, UriKind.Absolute, out var sourceUri) && sourceUri.Scheme is "https" or "http" &&
            !string.IsNullOrWhiteSpace(observation.PrimaryInjury);
        if (evidenced && observation.InjuryOccurredAt >= window.Start && observation.InjuryOccurredAt < window.End)
            return (WeeklyInjuryCategory.ConfirmedNewInjury, "Dated source explicitly places injury onset in this reporting window.");
        if (evidenced && observation.InjuryOccurredAt < window.Start)
            return (WeeklyInjuryCategory.ExistingInjuryUpdate, "Dated source places injury onset before this reporting window.");
        if (previous is not null && previous.Status is not ("healthy" or "active" or "na" or "suspended") &&
            (previous.EffectiveAt ?? previous.ObservedAt) >= (observation.EffectiveAt ?? observation.ObservedAt).AddHours(-26) &&
            !string.IsNullOrWhiteSpace(previous.PrimaryInjury) &&
            string.Equals(previous.PrimaryInjury, observation.PrimaryInjury, StringComparison.OrdinalIgnoreCase))
            return (WeeklyInjuryCategory.ExistingInjuryUpdate, "Update to the same concern in a recent same-source observation; onset is unconfirmed.");
        return (WeeklyInjuryCategory.UncertainTiming, "Newly recorded change, but onset is unverified or the previous same-source baseline is missing/stale.");
    }
}