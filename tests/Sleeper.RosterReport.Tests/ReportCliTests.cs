using FluentAssertions;
using Sleeper.RosterReport.Cli;

namespace Sleeper.RosterReport.Tests;

public class ReportCliTests
{
    [Fact]
    public void Parse_InjuriesAndRecapShareExplicitWindow()
    {
        const string start = "2026-09-09T00:00:00-04:00";
        const string end = "2026-09-16T00:00:00-04:00";
        var result = ReportCli.Parse(["injuries", "--week", "1", "--season", "2026", "--start", start, "--end", end, "--format", "json"]);
        var options = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation.Options
            .Should().BeOfType<InjuriesCommandOptions>().Subject;
        options.Format.Should().Be("json");
        options.Window.Start.Offset.Should().Be(TimeSpan.FromHours(-4));
        var recap = ReportCli.Parse(["recap", "--week", "1", "--injury-start", start, "--injury-end", end]);
        recap.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation.Options
            .Should().BeOfType<WeeklyRecapCommandOptions>().Subject.InjuryWindow.Should().Be(options.Window);
    }

    [Theory]
    [InlineData("2026-09-09", "2026-09-16")]
    [InlineData("2026-09-09T00:00:00", "2026-09-16T00:00:00Z")]
    [InlineData("2026-09-09T00:00:00Z", "2026-09-09T00:00:00Z")]
    [InlineData("2026-09-09T00:00:00Z", "2026-11-09T00:00:00Z")]
    public void Parse_RejectsInvalidInjuryWindows(string start, string end)
    {
        ReportCli.Parse(["injuries", "--week", "1", "--season", "2026", "--start", start, "--end", end])
            .Should().BeOfType<ReportCliErrorResult>();
    }

    [Fact]
    public void Parse_InjuriesRequiresScopeAndRecapRequiresPairedWindow()
    {
        ReportCli.Parse(["injuries", "--week", "1"]).Should().BeOfType<ReportCliErrorResult>();
        ReportCli.Parse(["recap", "--week", "1", "--injury-start", "2026-09-09T00:00:00Z"])
            .Should().BeOfType<ReportCliErrorResult>();
        ReportCli.Parse(["injuries", "--help"]).Should().BeOfType<ReportCliHelpResult>()
            .Subject.HelpText.Should().Contain("--start").And.Contain("Read-only");
    }

    [Fact]
    public void Parse_ReturnsRootHelpWithSuccess_ForHelpFlag()
    {
        var result = ReportCli.Parse(["--help"]);

        var help = result.Should().BeOfType<ReportCliHelpResult>().Subject;
        help.ExitCode.Should().Be(0);
        help.HelpText.Should().Contain("Sleeper Fantasy Football Reports");
        help.HelpText.Should().Contain("Commands:");
        help.HelpText.Should().Contain("dotnet run --project src/Sleeper.RosterReport/Sleeper.RosterReport.csproj -- <command> [options]");
        help.HelpText.Should().Contain("recap");
    }

    [Fact]
    public void Parse_ReturnsRootHelpWithError_ForEmptyArgs()
    {
        var result = ReportCli.Parse([]);

        var help = result.Should().BeOfType<ReportCliHelpResult>().Subject;
        help.ExitCode.Should().Be(1);
        help.HelpText.Should().Contain("Usage:");
    }

    [Fact]
    public void Parse_ReturnsCommandHelp_ForSubcommandHelp()
    {
        var result = ReportCli.Parse(["recap", "--help"]);

        var help = result.Should().BeOfType<ReportCliHelpResult>().Subject;
        help.ExitCode.Should().Be(0);
        help.HelpText.Should().Contain("recap - Build an AI-authored weekly league recap.");
        help.HelpText.Should().Contain("recap --week <week> [--league-id <id>] [--season <year>]");
        help.HelpText.Should().Contain("Writes recaps/{season}/week-NN.md.");
    }

    [Fact]
    public void Parse_ReturnsCommandHelp_WhenHelpFlagAppearsAfterArguments()
    {
        var result = ReportCli.Parse(["season", "2025", "--help"]);

        var help = result.Should().BeOfType<ReportCliHelpResult>().Subject;
        help.ExitCode.Should().Be(0);
        help.HelpText.Should().Contain("season - Build the season-in-review recap and sidecar artifacts.");
    }

    [Fact]
    public void Parse_ParsesRecapCanonicalOptions()
    {
        var result = ReportCli.Parse(["recap", "--week", "10", "--season", "2025", "--league-id", "league-1"]);

        var invocation = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation;
        invocation.Command.Should().Be(ReportCommand.Recap);
        var options = invocation.Options.Should().BeOfType<WeeklyRecapCommandOptions>().Subject;
        options.Week.Should().Be(10);
        options.Season.Should().Be(2025);
        options.LeagueId.Should().Be("league-1");
    }

    [Fact]
    public void Parse_ParsesLegacyRecapPositionals()
    {
        var result = ReportCli.Parse(["recap", "10", "league-1", "2025"]);

        var invocation = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation;
        var options = invocation.Options.Should().BeOfType<WeeklyRecapCommandOptions>().Subject;
        options.Week.Should().Be(10);
        options.LeagueId.Should().Be("league-1");
        options.Season.Should().Be(2025);
    }

    [Fact]
    public void Parse_TreatsSeasonSingleYearAsSeasonNotLeagueId()
    {
        var result = ReportCli.Parse(["season", "2025"]);

        var invocation = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation;
        var options = invocation.Options.Should().BeOfType<SeasonRecapCommandOptions>().Subject;
        options.LeagueId.Should().Be(ReportCli.DefaultLeagueId);
        options.Season.Should().Be(2025);
    }

    [Fact]
    public void Parse_JoinsPlayerNamePositionals()
    {
        var result = ReportCli.Parse(["player", "Justin", "Jefferson"]);

        var invocation = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation;
        var options = invocation.Options.Should().BeOfType<PlayerCommandOptions>().Subject;
        options.Name.Should().Be("Justin Jefferson");
        options.LeagueId.Should().Be(ReportCli.DefaultLeagueId);
    }

    [Fact]
    public void Parse_MatchupLeagueIdOnlyLeavesWeekUnset()
    {
        var result = ReportCli.Parse(["matchup", ReportCli.DefaultLeagueId]);

        var invocation = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation;
        var options = invocation.Options.Should().BeOfType<MatchupCommandOptions>().Subject;
        options.Week.Should().BeNull();
        options.LeagueId.Should().Be(ReportCli.DefaultLeagueId);
    }

    [Fact]
    public void Parse_ReturnsCommandError_ForUnknownOption()
    {
        var result = ReportCli.Parse(["recap", "--weeks", "10"]);

        var error = result.Should().BeOfType<ReportCliErrorResult>().Subject;
        error.ExitCode.Should().Be(1);
        error.Message.Should().Contain("Unknown option '--weeks'");
        error.HelpText.Should().Contain("recap --week <week>");
    }

    [Fact]
    public void Parse_ParsesAssetHistoryOptions()
    {
        var result = ReportCli.Parse(["asset-history", "--season", "2024", "--league-id", "league-2024"]);

        var invocation = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation;
        invocation.Command.Should().Be(ReportCommand.AssetHistory);
        var options = invocation.Options.Should().BeOfType<AssetHistoryCommandOptions>().Subject;
        options.Season.Should().Be(2024);
        options.LeagueId.Should().Be("league-2024");
    }

    [Fact]
    public void Parse_UsesSafeDefaults_ForCopilotReplay()
    {
        var result = ReportCli.Parse(["copilot-replay"]);

        var invocation = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation;
        invocation.Command.Should().Be(ReportCommand.CopilotReplay);
        var options = invocation.Options.Should().BeOfType<CopilotReplayCommandOptions>().Subject;
        options.Season.Should().Be(2025);
        options.StartWeek.Should().Be(1);
        options.EndWeek.Should().Be(3);
        options.LeagueId.Should().Be("1180276953741729792");
        options.RunId.Should().BeNull();
    }

    [Fact]
    public void Parse_ParsesCopilotReplayOptions()
    {
        var result = ReportCli.Parse([
            "copilot-replay",
            "--season", "2024",
            "--start-week", "2",
            "--end-week", "4",
            "--league-id", "historical-league",
            "--run-id", "pilot-a"
        ]);

        var invocation = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation;
        var options = invocation.Options.Should().BeOfType<CopilotReplayCommandOptions>().Subject;
        options.Should().Be(new CopilotReplayCommandOptions("historical-league", 2024, 2, 4, "pilot-a"));
    }

    [Fact]
    public void Parse_ReadsExportOptions()
    {
        var result = ReportCli.Parse(["export", "--season", "2026", "--league-id", "abc123"]);

        var invocation = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation;
        invocation.Command.Should().Be(ReportCommand.Export);
        invocation.Options.Should().Be(new ExportCommandOptions(2026, "abc123"));
    }

    [Fact]
    public void Parse_ReadsExportPositionalForm()
    {
        var result = ReportCli.Parse(["export", "2026"]);

        var invocation = result.Should().BeOfType<ReportCliInvocationResult>().Subject.Invocation;
        var options = invocation.Options.Should().BeOfType<ExportCommandOptions>().Subject;
        options.Season.Should().Be(2026);
        options.LeagueId.Should().Be(ReportCli.DefaultLeagueId);
    }

    [Fact]
    public void Parse_FailsExport_WithoutSeason()
    {
        var result = ReportCli.Parse(["export"]);

        result.Should().BeOfType<ReportCliErrorResult>()
            .Which.Message.Should().Contain("season");
    }
}
