using System.Globalization;
using System.Net.Http.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Options;
using Sleeper.Api.NflData;
using Sleeper.Api.NflData.Models;
using Sleeper.Api.Models;

namespace Sleeper.Api.Injuries;

public sealed class NflverseInjuryImporter : IInjuryImporter
{
    private const string Source = "nflverse";
    private readonly HttpClient _http;
    private readonly ISleeperClient _sleeper;
    private readonly INflDataClient _nflData;
    private readonly IInjuryStore _store;
    private readonly InjuryImportOptions _options;

    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = null
    };

    public NflverseInjuryImporter(
        HttpClient http,
        ISleeperClient sleeper,
        INflDataClient nflData,
        IInjuryStore store,
        IOptions<InjuryImportOptions> options)
    {
        _http = http;
        _sleeper = sleeper;
        _nflData = nflData;
        _store = store;
        _options = options.Value;
    }

    public async Task<InjuryImportResult> ImportSleeperAsync(
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var sourceUrl = "https://api.sleeper.app/v1/players/nfl";
        var now = DateTimeOffset.UtcNow;
        var observedAt = new DateTimeOffset(
            now.Year,
            now.Month,
            now.Day,
            now.Hour,
            0,
            0,
            TimeSpan.Zero);
        var players = await _sleeper.GetAllPlayersAsync("nfl", ct).ConfigureAwait(false);
        if (players.Count == 0)
            throw new InvalidOperationException("Sleeper returned an empty player snapshot.");
        var cohort = await GetRequiredCohortAsync(ct).ConfigureAwait(false);
        var inputs = new List<InjuryObservationInput>(cohort.Count);
        var injuredCount = 0;
        foreach (var cohortPlayer in cohort.OrderBy(player => player.Rank))
        {
            if (!players.TryGetValue(cohortPlayer.SleeperId, out var player))
                continue;
            var status = NormalizeValue(player?.InjuryStatus);
            var practice = NormalizeValue(player?.PracticeParticipation);
            var injuryStart = NormalizeValue(player?.InjuryStartDate);
            var hasDesignation = status is not null || practice is not null || injuryStart is not null;
            if (hasDesignation)
                injuredCount++;

            inputs.Add(new InjuryObservationInput(
                cohortPlayer.SleeperId,
                "sleeper",
                sourceUrl,
                status ?? (hasDesignation ? "practice" : "healthy"),
                practice ?? (hasDesignation ? null : "full"),
                null,
                null,
                hasDesignation
                    ? $"Complete Sleeper player snapshot; injury_start_date={injuryStart ?? "unknown"}."
                    : "Complete Sleeper player snapshot contained no current injury designation.",
                "platform",
                observedAt,
                observedAt,
                InjuryObservationScope.Current,
                Authority: 60,
                ExpiresAt: observedAt.AddHours(26),
                Season: now.Year,
                SeasonType: "pre"));
        }

        var inserted = 0;
        if (!dryRun)
            inserted = (await _store.RecordBatchAsync(inputs, ct).ConfigureAwait(false)).InsertedCount;

        return new InjuryImportResult(
            "sleeper",
            DateTime.UtcNow.Year,
            sourceUrl,
            players.Count,
            cohort.Count - injuredCount,
            inputs.Count,
            inserted,
            cohort.Count(player => !players.ContainsKey(player.SleeperId)),
            [],
            observedAt);
    }

    public async Task<InjuryImportResult> ImportNflverseAsync(
        int season,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        if (season < 2009 || season > DateTime.UtcNow.Year + 1)
            throw new ArgumentOutOfRangeException(nameof(season), "Season must be between 2009 and one year beyond the current season.");

        var sourceUrl = string.Format(CultureInfo.InvariantCulture, _options.NflverseUrlTemplate, season);
        var cohort = await GetRequiredCohortAsync(ct).ConfigureAwait(false);
        var cohortIds = cohort.Select(player => player.SleeperId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappings = BuildGsisToSleeperMap(
            await _nflData.GetPlayerIdMappingsAsync(ct).ConfigureAwait(false));

        using var stream = await _http.GetStreamAsync(sourceUrl, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CsvConfig);
        var rows = new List<ParsedRow>();
        var unmapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceRows = 0;
        var skippedRows = 0;
        if (!await csv.ReadAsync().ConfigureAwait(false))
            return new InjuryImportResult(Source, season, sourceUrl, 0, 0, 0, 0, 0, []);
        csv.ReadHeader();
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            sourceRows++;
            var row = ParseRow(csv, season);
            if (row is null)
            {
                skippedRows++;
                continue;
            }
            if (!mappings.TryGetValue(row.GsisId, out var sleeperId))
            {
                unmapped.Add(row.GsisId);
                continue;
            }
            if (!cohortIds.Contains(sleeperId))
            {
                skippedRows++;
                continue;
            }

            rows.Add(row with { SleeperId = sleeperId });
        }

        var ordered = rows
            .OrderBy(row => row.Week)
            .ThenBy(row => row.GsisId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Team, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Status, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var imported = 0;
        if (!dryRun)
        {
            var importedAt = DateTimeOffset.UtcNow;
            var inputs = ordered.Select(row => new InjuryObservationInput(
                    row.SleeperId!,
                    Source,
                    sourceUrl,
                    row.Status,
                    row.PracticeStatus,
                    row.PrimaryInjury,
                    row.SecondaryInjury,
                    row.Notes,
                    "data-provider",
                    importedAt,
                    null,
                    InjuryObservationScope.Historical,
                    Authority: 70,
                    Season: season,
                    Week: row.Week,
                    SeasonType: row.SeasonType)).ToList();
            imported = (await _store.RecordBatchAsync(inputs, ct).ConfigureAwait(false)).InsertedCount;
        }

        return new InjuryImportResult(
            Source,
            season,
            sourceUrl,
            sourceRows,
            skippedRows,
            ordered.Count,
            imported,
            sourceRows - skippedRows - ordered.Count,
            unmapped.Order(StringComparer.OrdinalIgnoreCase).Take(100).ToList());
    }

    public IReadOnlyList<TeamInjuryPage> GetTeamInjuryPages() =>
    [
        new("ARI", "Arizona Cardinals", "https://www.azcardinals.com", "https://www.nfl.com/teams/arizona-cardinals/injuries", true),
        new("ATL", "Atlanta Falcons", "https://www.atlantafalcons.com", "https://www.nfl.com/teams/atlanta-falcons/injuries", true),
        new("BAL", "Baltimore Ravens", "https://www.baltimoreravens.com", "https://www.nfl.com/teams/baltimore-ravens/injuries", true),
        new("BUF", "Buffalo Bills", "https://www.buffalobills.com", "https://www.nfl.com/teams/buffalo-bills/injuries", true),
        new("CAR", "Carolina Panthers", "https://www.panthers.com", "https://www.nfl.com/teams/carolina-panthers/injuries", true),
        new("CHI", "Chicago Bears", "https://www.chicagobears.com", "https://www.nfl.com/teams/chicago-bears/injuries", true),
        new("CIN", "Cincinnati Bengals", "https://www.bengals.com", "https://www.nfl.com/teams/cincinnati-bengals/injuries", true),
        new("CLE", "Cleveland Browns", "https://www.clevelandbrowns.com", "https://www.nfl.com/teams/cleveland-browns/injuries", true),
        new("DAL", "Dallas Cowboys", "https://www.dallascowboys.com", "https://www.nfl.com/teams/dallas-cowboys/injuries", true),
        new("DEN", "Denver Broncos", "https://www.denverbroncos.com", "https://www.nfl.com/teams/denver-broncos/injuries", true),
        new("DET", "Detroit Lions", "https://www.detroitlions.com", "https://www.nfl.com/teams/detroit-lions/injuries", true),
        new("GB", "Green Bay Packers", "https://www.packers.com", "https://www.nfl.com/teams/green-bay-packers/injuries", true),
        new("HOU", "Houston Texans", "https://www.houstontexans.com", "https://www.nfl.com/teams/houston-texans/injuries", true),
        new("IND", "Indianapolis Colts", "https://www.colts.com", "https://www.nfl.com/teams/indianapolis-colts/injuries", true),
        new("JAX", "Jacksonville Jaguars", "https://www.jaguars.com", "https://www.nfl.com/teams/jacksonville-jaguars/injuries", true),
        new("KC", "Kansas City Chiefs", "https://www.chiefs.com", "https://www.nfl.com/teams/kansas-city-chiefs/injuries", true),
        new("LV", "Las Vegas Raiders", "https://www.raiders.com", "https://www.nfl.com/teams/las-vegas-raiders/injuries", true),
        new("LAC", "Los Angeles Chargers", "https://www.chargers.com", "https://www.nfl.com/teams/los-angeles-chargers/injuries", true),
        new("LAR", "Los Angeles Rams", "https://www.therams.com", "https://www.nfl.com/teams/los-angeles-rams/injuries", true),
        new("MIA", "Miami Dolphins", "https://www.miamidolphins.com", "https://www.nfl.com/teams/miami-dolphins/injuries", true),
        new("MIN", "Minnesota Vikings", "https://www.vikings.com", "https://www.nfl.com/teams/minnesota-vikings/injuries", true),
        new("NE", "New England Patriots", "https://www.patriots.com", "https://www.nfl.com/teams/new-england-patriots/injuries", true),
        new("NO", "New Orleans Saints", "https://www.neworleanssaints.com", "https://www.nfl.com/teams/new-orleans-saints/injuries", true),
        new("NYG", "New York Giants", "https://www.giants.com", "https://www.nfl.com/teams/new-york-giants/injuries", true),
        new("NYJ", "New York Jets", "https://www.newyorkjets.com", "https://www.nfl.com/teams/new-york-jets/injuries", true),
        new("PHI", "Philadelphia Eagles", "https://www.philadelphiaeagles.com", "https://www.nfl.com/teams/philadelphia-eagles/injuries", true),
        new("PIT", "Pittsburgh Steelers", "https://www.steelers.com", "https://www.nfl.com/teams/pittsburgh-steelers/injuries", true),
        new("SF", "San Francisco 49ers", "https://www.49ers.com", "https://www.nfl.com/teams/san-francisco-49ers/injuries", true),
        new("SEA", "Seattle Seahawks", "https://www.seahawks.com", "https://www.nfl.com/teams/seattle-seahawks/injuries", true),
        new("TB", "Tampa Bay Buccaneers", "https://www.buccaneers.com", "https://www.nfl.com/teams/tampa-bay-buccaneers/injuries", true),
        new("TEN", "Tennessee Titans", "https://www.tennesseetitans.com", "https://www.nfl.com/teams/tennessee-titans/injuries", true),
        new("WAS", "Washington Commanders", "https://www.commanders.com", "https://www.nfl.com/teams/washington-commanders/injuries", true)
    ];

    public IReadOnlyList<InjurySourcePlan> GetCurrentSeasonSourcePlan() =>
    [
        new("Sleeper", "Automated baseline", "Current snapshot", "API", "Import first; it already uses Sleeper player IDs and is appropriate for preseason availability."),
        new("Official team injury reports", "Primary confirmation", "Same day / latest report", "Agent-verified web page", "Prefer team medical/designation reports and record the article URL plus publication time."),
        new("NFL team injury pages", "Secondary official index", "Latest published report", "Agent-verified web page", "Use as a discovery and cross-check source; verify the team page before recording."),
        new("ESPN / Rotowire / established reporters", "Context and breaking news", "Last 24 hours", "Agent research", "Use only when the report identifies the player and publication; preserve the direct URL and confidence."),
        new("nflverse", "Historical backfill", "Season file availability", "CSV", "Do not use prior-season rows as current preseason status; the 2026 file may not exist yet.")
    ];

    private static ParsedRow? ParseRow(CsvReader csv, int season)
    {
        var gsisId = Get(csv, "gsis_id");
        var weekText = Get(csv, "week");
        if (string.IsNullOrWhiteSpace(gsisId) || !int.TryParse(weekText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var week))
            return null;

        var reportStatus = Get(csv, "report_status");
        var practiceStatus = Get(csv, "practice_status");
        if (string.IsNullOrWhiteSpace(reportStatus) && string.IsNullOrWhiteSpace(practiceStatus))
            return null;
        var status = string.IsNullOrWhiteSpace(reportStatus) ? "practice" : reportStatus;

        var primary = First(Get(csv, "report_primary_injury"), Get(csv, "practice_primary_injury"));
        var secondary = First(Get(csv, "report_secondary_injury"), Get(csv, "practice_secondary_injury"));
        var team = Get(csv, "team") ?? "UNK";
        var gameType = Get(csv, "game_type") ?? Get(csv, "season_type") ?? "unknown";
        return new ParsedRow(
            null,
            gsisId,
            team,
            status.Trim().ToLowerInvariant(),
            practiceStatus,
            primary,
            secondary,
            $"{season} {gameType} week {week}; nflverse injury report.",
            week,
            gameType);
    }

    private static string? Get(CsvReader csv, string name)
    {
        try
        {
            return csv.GetField(name);
        }
        catch (CsvHelper.MissingFieldException)
        {
            return null;
        }
    }

    private static string? First(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second;

    private async Task<IReadOnlyList<InjuryCohortPlayer>> GetRequiredCohortAsync(CancellationToken ct)
    {
        var cohort = await _store.GetCohortAsync(ct).ConfigureAwait(false);
        return cohort.Count == 0
            ? throw new InvalidOperationException("The injury cohort is empty. Publish a cohort before importing injuries.")
            : cohort;
    }

    private static Dictionary<string, string> BuildGsisToSleeperMap(
        IReadOnlyList<PlayerIdMapping> mappings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in mappings
                     .Where(mapping => IsId(mapping.GsisId) && IsId(mapping.SleeperId))
                     .GroupBy(mapping => mapping.GsisId!, StringComparer.OrdinalIgnoreCase))
        {
            var sleeperIds = group.Select(mapping => mapping.SleeperId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sleeperIds.Count == 1)
                result[group.Key] = sleeperIds[0];
        }
        return result;
    }

    private static bool IsId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, "NA", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeValue(string? value) =>
        IsId(value) ? value!.Trim() : null;

    private sealed record ParsedRow(
        string? SleeperId,
        string GsisId,
        string Team,
        string Status,
        string? PracticeStatus,
        string? PrimaryInjury,
        string? SecondaryInjury,
        string Notes,
        int Week,
        string SeasonType);
}
