using FluentAssertions;
using Sleeper.RosterReport.Copilot;

namespace Sleeper.RosterReport.Tests;

public class CopilotRecapReplayRunnerTests
{
    [Fact]
    public void SeedReplayContext_CopiesOnlyPrecedingRecapsAndHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sleeper-replay-{Guid.NewGuid():N}");
        var canonical = Path.Combine(root, "canonical");
        var context = Path.Combine(root, "context");

        try
        {
            Directory.CreateDirectory(canonical);
            File.WriteAllText(Path.Combine(canonical, "week-01.md"), "week one");
            File.WriteAllText(Path.Combine(canonical, "week-02.md"), "week two");
            File.WriteAllText(Path.Combine(canonical, "week-03.md"), "week three");
            const string history = """
                {
                  "Season": 2025,
                  "Snapshots": [
                    { "Week": 1 },
                    { "Week": 3 }
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(canonical, "power-history.json"), history);
            File.WriteAllText(Path.Combine(canonical, "team-name-history.json"), history);

            CopilotRecapReplayRunner.SeedReplayContext(canonical, context, startWeek: 3);

            File.Exists(Path.Combine(context, "week-01.md")).Should().BeTrue();
            File.Exists(Path.Combine(context, "week-02.md")).Should().BeTrue();
            File.Exists(Path.Combine(context, "week-03.md")).Should().BeFalse();
            File.Exists(Path.Combine(context, "power-history.json")).Should().BeTrue();
            File.Exists(Path.Combine(context, "team-name-history.json")).Should().BeTrue();
            File.ReadAllText(Path.Combine(context, "power-history.json")).Should().Contain("\"Week\": 1");
            File.ReadAllText(Path.Combine(context, "power-history.json")).Should().NotContain("\"Week\": 3");
            File.ReadAllText(Path.Combine(context, "team-name-history.json")).Should().Contain("\"Week\": 1");
            File.ReadAllText(Path.Combine(context, "team-name-history.json")).Should().NotContain("\"Week\": 3");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
