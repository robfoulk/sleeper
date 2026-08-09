using System.Text;

namespace Sleeper.RosterReport.Cli;

internal static class ReportCli
{
    public const string DefaultLeagueId = "1312539280601522176";
    public const string ProjectPath = "src/Sleeper.RosterReport/Sleeper.RosterReport.csproj";

    private static readonly Dictionary<string, ReportCommandDefinition> Commands = BuildCommands()
        .ToDictionary(command => command.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["keeper"] = "keepers",
        ["scoreboard"] = "matchup",
        ["weekly-recap"] = "recap",
        ["season-recap"] = "season",
        ["roster-history"] = "rosters-history",
        ["assets-history"] = "asset-history"
    };

    public static ReportCliResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new ReportCliHelpResult(RenderRootHelp(), 1);

        if (IsHelp(args[0]))
            return new ReportCliHelpResult(RenderRootHelp(), 0);

        if (IsHelpCommand(args[0]))
        {
            if (args.Count == 1)
                return new ReportCliHelpResult(RenderRootHelp(), 0);

            return TryGetCommand(args[1], out var command)
                ? new ReportCliHelpResult(RenderCommandHelp(command), 0)
                : UnknownCommand(args[1]);
        }

        if (!TryGetCommand(args[0], out var definition))
            return UnknownCommand(args[0]);

        var commandArgs = args.Skip(1).ToArray();
        if (commandArgs.Any(IsHelp))
            return new ReportCliHelpResult(RenderCommandHelp(definition), 0);

        return ParseCommand(definition, commandArgs);
    }

    public static string RenderRootHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sleeper Fantasy Football Reports");
        sb.AppendLine("================================");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        sb.AppendLine($"  dotnet run --project {ProjectPath} -- <command> [options]");
        sb.AppendLine("  Sleeper.RosterReport <command> [options]");
        sb.AppendLine();
        sb.AppendLine("Commands:");
        foreach (var command in Commands.Values.OrderBy(command => command.SortOrder))
            sb.AppendLine($"  {command.Name,-16} {command.Summary}");
        sb.AppendLine();
        sb.AppendLine("Global conventions:");
        sb.AppendLine($"  --league-id <id>  Sleeper league ID. Default: {DefaultLeagueId}");
        sb.AppendLine("  --help, -h        Show root or command help. Help exits with code 0.");
        sb.AppendLine("  help <command>    Show detailed help for one report command.");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine($"  dotnet run --project {ProjectPath} -- keepers --username robfoulk");
        sb.AppendLine($"  dotnet run --project {ProjectPath} -- player --name \"Justin Jefferson\"");
        sb.AppendLine($"  dotnet run --project {ProjectPath} -- recap --week 10 --season 2025");
        sb.AppendLine($"  dotnet run --project {ProjectPath} -- help season");
        sb.AppendLine();
        sb.AppendLine("Agent notes:");
        sb.AppendLine("  Reports that use Foundry agents degrade to deterministic output when Foundry is not configured.");
        sb.AppendLine("  Foundry configuration is documented in docs/foundry-agent-configuration.md.");
        return sb.ToString();
    }

    public static string RenderCommandHelp(string commandName)
        => TryGetCommand(commandName, out var command) ? RenderCommandHelp(command) : RenderRootHelp();

    private static ReportCliResult ParseCommand(ReportCommandDefinition definition, IReadOnlyList<string> args)
    {
        var parsed = ParseTokens(definition, args);
        if (parsed.Errors.Count > 0)
            return new ReportCliErrorResult(string.Join(Environment.NewLine, parsed.Errors), RenderCommandHelp(definition));

        return definition.Command switch
        {
            ReportCommand.Keepers => ParseKeepers(definition, parsed),
            ReportCommand.Board => ParseBoard(definition, parsed),
            ReportCommand.Player => ParsePlayer(definition, parsed),
            ReportCommand.Team => ParseTeam(definition, parsed),
            ReportCommand.Matchup => ParseMatchup(definition, parsed),
            ReportCommand.Recap => ParseRecap(definition, parsed),
            ReportCommand.Season => ParseSeason(definition, parsed),
            ReportCommand.RostersHistory => ParseRostersHistory(definition, parsed),
                ReportCommand.AssetHistory => ParseAssetHistory(definition, parsed),
            _ => new ReportCliErrorResult($"Unsupported command '{definition.Name}'.", RenderRootHelp())
        };
    }

    private static ReportCliResult ParseKeepers(ReportCommandDefinition definition, ParsedTokens parsed)
    {
        if (parsed.Positionals.Count > (GetOption(parsed, "username") is null ? 2 : 1))
            return TooManyPositionals(definition);

        var username = GetOption(parsed, "username") ?? GetSinglePositional(parsed, 0);
        if (string.IsNullOrWhiteSpace(username))
            return Missing(definition, "Missing required option --username <username>.");

        var leagueId = LeagueId(parsed, positionalIndex: 1);
        return Invoke(ReportCommand.Keepers, new KeeperCommandOptions(username, leagueId));
    }

    private static ReportCliResult ParseBoard(ReportCommandDefinition definition, ParsedTokens parsed)
    {
        if (parsed.Positionals.Count > 1)
            return TooManyPositionals(definition);

        return Invoke(ReportCommand.Board, new BoardCommandOptions(LeagueId(parsed, positionalIndex: 0)));
    }

    private static ReportCliResult ParsePlayer(ReportCommandDefinition definition, ParsedTokens parsed)
    {
        if (GetOption(parsed, "name") is not null && parsed.Positionals.Count > 1)
            return TooManyPositionals(definition);

        var name = GetOption(parsed, "name") ?? GetPlayerNameFromPositionals(parsed);
        if (string.IsNullOrWhiteSpace(name))
            return Missing(definition, "Missing required option --name <player name>.");

        var leagueId = LeagueId(parsed, positionalIndex: PositionalLeagueIndex(parsed));
        return Invoke(ReportCommand.Player, new PlayerCommandOptions(name, leagueId));
    }

    private static ReportCliResult ParseTeam(ReportCommandDefinition definition, ParsedTokens parsed)
    {
        if (parsed.Positionals.Count > (GetOption(parsed, "username") is null ? 2 : 1))
            return TooManyPositionals(definition);

        var username = GetOption(parsed, "username") ?? GetSinglePositional(parsed, 0);
        if (string.IsNullOrWhiteSpace(username))
            return Missing(definition, "Missing required option --username <username>.");

        var leagueId = LeagueId(parsed, positionalIndex: 1);
        return Invoke(ReportCommand.Team, new TeamCommandOptions(username, leagueId));
    }

    private static ReportCliResult ParseMatchup(ReportCommandDefinition definition, ParsedTokens parsed)
    {
        if (parsed.Positionals.Count > 2)
            return TooManyPositionals(definition);

        var weekText = GetOption(parsed, "week");
        if (weekText is null && parsed.Positionals.Count > 0 && !LooksLikeLeagueId(parsed.Positionals[0]))
            weekText = parsed.Positionals[0];

        if (!TryOptionalPositiveInt(weekText, "week", out var week, out var error))
            return Missing(definition, error!);

        var leagueId = LeagueId(parsed, positionalIndex: weekText is null ? 0 : 1);
        return Invoke(ReportCommand.Matchup, new MatchupCommandOptions(week, leagueId));
    }

    private static ReportCliResult ParseRecap(ReportCommandDefinition definition, ParsedTokens parsed)
    {
        if (parsed.Positionals.Count > 3)
            return TooManyPositionals(definition);

        var weekText = GetOption(parsed, "week") ?? GetSinglePositional(parsed, 0);
        if (!TryRequiredPositiveInt(weekText, "week", out var week, out var error))
            return Missing(definition, error!);

        var seasonText = GetOption(parsed, "season") ?? GetSinglePositional(parsed, 2);
        if (!TryOptionalPositiveInt(seasonText, "season", out var season, out error))
            return Missing(definition, error!);

        var leagueId = LeagueId(parsed, positionalIndex: 1);
        return Invoke(ReportCommand.Recap, new WeeklyRecapCommandOptions(week, leagueId, season));
    }

    private static ReportCliResult ParseSeason(ReportCommandDefinition definition, ParsedTokens parsed)
    {
        var seasonText = GetOption(parsed, "season");
        var leagueId = GetOption(parsed, "league-id") ?? DefaultLeagueId;

        if (parsed.Positionals.Count > 2)
            return TooManyPositionals(definition);

        foreach (var positional in parsed.Positionals)
        {
            if (LooksLikeSeason(positional) && seasonText is null)
                seasonText = positional;
            else if (leagueId == DefaultLeagueId)
                leagueId = positional;
            else if (seasonText is null)
                seasonText = positional;
            else
                return TooManyPositionals(definition);
        }

        if (!TryOptionalPositiveInt(seasonText, "season", out var season, out var error))
            return Missing(definition, error!);

        return Invoke(ReportCommand.Season, new SeasonRecapCommandOptions(leagueId, season));
    }

    private static ReportCliResult ParseRostersHistory(ReportCommandDefinition definition, ParsedTokens parsed)
    {
        if (parsed.Positionals.Count > 2)
            return TooManyPositionals(definition);

        var seasonText = GetOption(parsed, "season") ?? GetSinglePositional(parsed, 0);
        if (!TryRequiredPositiveInt(seasonText, "season", out var season, out var error))
            return Missing(definition, error!);

        var leagueId = LeagueId(parsed, positionalIndex: 1);
        return Invoke(ReportCommand.RostersHistory, new RostersHistoryCommandOptions(season, leagueId));
    }
    
    private static ReportCliResult ParseAssetHistory(ReportCommandDefinition definition, ParsedTokens parsed)
    {
        if (parsed.Positionals.Count > 2)
            return TooManyPositionals(definition);

        var seasonText = GetOption(parsed, "season") ?? GetSinglePositional(parsed, 0);
        if (!TryRequiredPositiveInt(seasonText, "season", out var season, out var error))
            return Missing(definition, error!);

        var leagueId = LeagueId(parsed, positionalIndex: 1);
        return Invoke(ReportCommand.AssetHistory, new AssetHistoryCommandOptions(season, leagueId));
    }

    private static ParsedTokens ParseTokens(ReportCommandDefinition definition, IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        var errors = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("-", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }

            var (nameToken, inlineValue) = SplitOption(token);
            if (!definition.OptionAliases.TryGetValue(nameToken, out var canonicalName))
            {
                errors.Add($"Unknown option '{nameToken}' for command '{definition.Name}'.");
                continue;
            }

            var value = inlineValue;
            if (value is null)
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    errors.Add($"Option '{nameToken}' requires a value.");
                    continue;
                }

                value = args[++i];
            }

            values[canonicalName] = value;
        }

        return new ParsedTokens(values, positionals, errors);
    }

    private static (string Name, string? Value) SplitOption(string token)
    {
        var equalsIndex = token.IndexOf('=', StringComparison.Ordinal);
        return equalsIndex < 0
            ? (token, null)
            : (token[..equalsIndex], token[(equalsIndex + 1)..]);
    }

    private static ReportCliResult Missing(ReportCommandDefinition definition, string message)
        => new ReportCliErrorResult(message, RenderCommandHelp(definition));

    private static ReportCliResult TooManyPositionals(ReportCommandDefinition definition)
        => Missing(definition, $"Too many positional arguments for command '{definition.Name}'. Use named options for clarity.");

    private static ReportCliResult Invoke(ReportCommand command, IReportCommandOptions options)
        => new ReportCliInvocationResult(new ReportInvocation(command, options));

    private static ReportCliResult UnknownCommand(string commandName)
        => new ReportCliErrorResult($"Unknown command '{commandName}'.", RenderRootHelp());

    private static string? GetOption(ParsedTokens parsed, string name)
        => parsed.Options.TryGetValue(name, out var value) ? value : null;

    private static string? GetSinglePositional(ParsedTokens parsed, int index)
        => index >= 0 && parsed.Positionals.Count > index ? parsed.Positionals[index] : null;

    private static string LeagueId(ParsedTokens parsed, int positionalIndex)
        => GetOption(parsed, "league-id") ?? GetSinglePositional(parsed, positionalIndex) ?? DefaultLeagueId;

    private static string? GetPlayerNameFromPositionals(ParsedTokens parsed)
    {
        if (parsed.Positionals.Count == 0)
            return null;

        var leagueIndex = PositionalLeagueIndex(parsed);
        var nameParts = parsed.Positionals
            .Where((_, index) => index != leagueIndex)
            .ToArray();

        return nameParts.Length == 0 ? null : string.Join(' ', nameParts);
    }

    private static int PositionalLeagueIndex(ParsedTokens parsed)
    {
        if (parsed.Positionals.Count < 2)
            return parsed.Positionals.Count == 1 && LooksLikeLeagueId(parsed.Positionals[0]) ? 0 : -1;

        return LooksLikeLeagueId(parsed.Positionals[^1]) ? parsed.Positionals.Count - 1 : -1;
    }

    private static bool TryRequiredPositiveInt(string? value, string name, out int result, out string? error)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"Missing required option --{name} <{name}>.";
            return false;
        }

        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            result = parsed;
            error = null;
            return true;
        }

        error = $"Invalid {name} '{value}'. Expected a positive number.";
        return false;
    }

    private static bool TryOptionalPositiveInt(string? value, string name, out int? result, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = null;
            error = null;
            return true;
        }

        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            result = parsed;
            error = null;
            return true;
        }

        result = null;
        error = $"Invalid {name} '{value}'. Expected a positive number.";
        return false;
    }

    private static bool LooksLikeLeagueId(string value)
        => value.Length >= 12 && value.All(char.IsDigit);

    private static bool LooksLikeSeason(string value)
        => value.Length == 4 && value.All(char.IsDigit);

    private static bool IsHelp(string value)
        => string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "/?", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelpCommand(string value)
        => string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetCommand(string value, out ReportCommandDefinition command)
    {
        var key = Aliases.TryGetValue(value, out var canonical) ? canonical : value;
        return Commands.TryGetValue(key, out command!);
    }

    private static string RenderCommandHelp(ReportCommandDefinition command)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{command.Name} - {command.Summary}");
        sb.AppendLine();
        sb.AppendLine("Usage:");
        sb.AppendLine($"  dotnet run --project {ProjectPath} -- {command.Usage}");
        sb.AppendLine();
        sb.AppendLine("Options:");
        foreach (var option in command.Options)
            sb.AppendLine($"  {option.Syntax,-28} {option.Description}");

        if (command.LegacyUsage is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Legacy positional form:");
            sb.AppendLine($"  {command.LegacyUsage}");
        }

        sb.AppendLine();
        sb.AppendLine("Examples:");
        foreach (var example in command.Examples)
            sb.AppendLine($"  dotnet run --project {ProjectPath} -- {example}");

        if (command.Outputs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Output:");
            foreach (var output in command.Outputs)
                sb.AppendLine($"  {output}");
        }

        if (command.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Notes:");
            foreach (var note in command.Notes)
                sb.AppendLine($"  {note}");
        }

        return sb.ToString();
    }

    private static IReadOnlyList<ReportCommandDefinition> BuildCommands()
    {
        var league = new ReportOptionHelp("--league-id, -l <id>", $"Sleeper league ID. Default: {DefaultLeagueId}");
        var season = new ReportOptionHelp("--season, -s <year>", "Season year, for example 2025.");
        var help = new ReportOptionHelp("--help, -h", "Show command help.");

        return
        [
            new(
                ReportCommand.Keepers,
                10,
                "keepers",
                "Analyze one team's keeper values and recommendations.",
                "keepers --username <username> [--league-id <id>]",
                [new("--username, -u <username>", "Sleeper username to analyze."), league, help],
                ["keepers <username> [league_id]"],
                ["keepers --username robfoulk", "keepers robfoulk"],
                ["Writes a console report only."],
                ["Uses Foundry for AI second opinions when configured; otherwise the deterministic report still runs."]),
            new(
                ReportCommand.Board,
                20,
                "board",
                "Show league-wide keeper candidates by team.",
                "board [--league-id <id>]",
                [league, help],
                ["board [league_id]"],
                ["board", $"board --league-id {DefaultLeagueId}"],
                ["Writes a console report only."],
                []),
            new(
                ReportCommand.Player,
                30,
                "player",
                "Run a player deep dive with multi-year trend and projection data.",
                "player --name <player name> [--league-id <id>]",
                [new("--name, -n <name>", "Player name or partial name. Quote multi-word names in shells."), league, help],
                ["player <name> [league_id]"],
                ["player --name \"Justin Jefferson\"", "player Justin Jefferson"],
                ["Writes a console report only."],
                ["The positional form now joins multiple name tokens unless the final token looks like a league ID."]),
            new(
                ReportCommand.Team,
                40,
                "team",
                "Run a full roster deep dive with keeper context and draft outlook.",
                "team --username <username> [--league-id <id>]",
                [new("--username, -u <username>", "Sleeper username to analyze."), league, help],
                ["team <username> [league_id]"],
                ["team --username robfoulk"],
                ["Writes a console report only."],
                ["Uses Foundry for AI draft outlooks when configured; otherwise deterministic player sections still run."]),
            new(
                ReportCommand.Matchup,
                50,
                "matchup",
                "Show the weekly matchup scoreboard.",
                "matchup [--week <week>] [--league-id <id>]",
                [new("--week, -w <week>", "Week number. Defaults to the current NFL week when omitted."), league, help],
                ["matchup [week] [league_id]"],
                ["matchup --week 10", "matchup"],
                ["Writes a console report only."],
                []),
            new(
                ReportCommand.Recap,
                60,
                "recap",
                "Build an AI-authored weekly league recap.",
                "recap --week <week> [--league-id <id>] [--season <year>]",
                [new("--week, -w <week>", "Week number. Required."), league, season, help],
                ["recap <week> [league_id] [season]"],
                ["recap --week 10 --season 2025", $"recap --week 1 --league-id {DefaultLeagueId} --season 2025"],
                ["Writes recaps/{season}/week-NN.md."],
                ["Valid league weeks are 1-17. If Foundry is not configured, writes a data-only markdown dump."]),
            new(
                ReportCommand.Season,
                70,
                "season",
                "Build the season-in-review recap and sidecar artifacts.",
                "season [--league-id <id>] [--season <year>]",
                [league, season, help],
                ["season [league_id] [season]"],
                ["season --season 2025", $"season --league-id {DefaultLeagueId} --season 2025"],
                ["Writes recaps/{season}/season.md, manifest.json, season sidecars, and chart SVGs."],
                ["The positional form 'season 2025' now means season 2025 for the default league."]),
            new(
                ReportCommand.RostersHistory,
                80,
                "rosters-history",
                "Capture kickoff-locked weekly roster snapshots.",
                "rosters-history --season <year> [--league-id <id>]",
                [season, league, help],
                ["rosters-history <season> [league_id]"],
                ["rosters-history --season 2025"],
                ["Writes datafiles/{season}/week-NN.json."],
                []),
            new(
                ReportCommand.AssetHistory,
                90,
                "asset-history",
                "Capture transactions and audit Week 1 asset movement.",
                "asset-history --season <year> [--league-id <id>]",
                [season, league, help],
                ["asset-history <season> [league_id]"],
                ["asset-history --season 2025"],
                ["Writes datafiles/{season}/transactions.json, asset-movement-audit.json, and asset-movement-summary.txt."],
                [])
        ];
    }
}

internal abstract record ReportCliResult(int ExitCode);

internal sealed record ReportCliHelpResult(string HelpText, int HelpExitCode) : ReportCliResult(HelpExitCode);

internal sealed record ReportCliErrorResult(string Message, string HelpText) : ReportCliResult(1);

internal sealed record ReportCliInvocationResult(ReportInvocation Invocation) : ReportCliResult(0);

internal sealed record ReportInvocation(ReportCommand Command, IReportCommandOptions Options);

internal interface IReportCommandOptions;

internal sealed record KeeperCommandOptions(string Username, string LeagueId) : IReportCommandOptions;

internal sealed record BoardCommandOptions(string LeagueId) : IReportCommandOptions;

internal sealed record PlayerCommandOptions(string Name, string LeagueId) : IReportCommandOptions;

internal sealed record TeamCommandOptions(string Username, string LeagueId) : IReportCommandOptions;

internal sealed record MatchupCommandOptions(int? Week, string LeagueId) : IReportCommandOptions;

internal sealed record WeeklyRecapCommandOptions(int Week, string LeagueId, int? Season) : IReportCommandOptions;

internal sealed record SeasonRecapCommandOptions(string LeagueId, int? Season) : IReportCommandOptions;

internal sealed record RostersHistoryCommandOptions(int Season, string LeagueId) : IReportCommandOptions;

internal sealed record AssetHistoryCommandOptions(int Season, string LeagueId) : IReportCommandOptions;

internal enum ReportCommand
{
    Keepers,
    Board,
    Player,
    Team,
    Matchup,
    Recap,
    Season,
    RostersHistory,
    AssetHistory
}

internal sealed record ReportCommandDefinition(
    ReportCommand Command,
    int SortOrder,
    string Name,
    string Summary,
    string Usage,
    IReadOnlyList<ReportOptionHelp> Options,
    IReadOnlyList<string> LegacyUsages,
    IReadOnlyList<string> Examples,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Notes)
{
    public string? LegacyUsage => LegacyUsages.Count == 0 ? null : string.Join(Environment.NewLine + "  ", LegacyUsages);

    public IReadOnlyDictionary<string, string> OptionAliases { get; } = Options
        .Where(option => !string.Equals(option.CanonicalName, "help", StringComparison.OrdinalIgnoreCase))
        .SelectMany(option => option.Aliases.Select(alias => (Alias: alias, Canonical: option.CanonicalName)))
        .ToDictionary(pair => pair.Alias, pair => pair.Canonical, StringComparer.OrdinalIgnoreCase);
}

internal sealed record ReportOptionHelp(string Syntax, string Description)
{
    public string CanonicalName => Aliases[0].TrimStart('-');

    public IReadOnlyList<string> Aliases { get; } = Syntax.Split(',', StringSplitOptions.TrimEntries)
        .Select(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
        .ToArray();
}

internal sealed record ParsedTokens(
    IReadOnlyDictionary<string, string> Options,
    IReadOnlyList<string> Positionals,
    IReadOnlyList<string> Errors);
