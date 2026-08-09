using System.Reflection;
using FluentAssertions;
using Sleeper.McpServer.Tools;

namespace Sleeper.McpServer.Tests;

public class ToolContractTests
{
    [Fact]
    public void AsyncTools_AcceptOptionalCancellationToken()
    {
        var toolTypes = new[]
        {
            typeof(LeagueTools),
            typeof(KeeperTools),
            typeof(PlayerTools),
            typeof(RosterTools),
            typeof(InjuryTools)
        };

        var asyncTools = toolTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.ReturnType == typeof(Task<string>))
            .ToList();

        asyncTools.Should().NotBeEmpty();
        foreach (var method in asyncTools)
        {
            var cancellation = method.GetParameters().Last();
            cancellation.ParameterType.Should().Be(
                typeof(CancellationToken),
                $"{method.DeclaringType!.Name}.{method.Name} should propagate request cancellation");
            cancellation.HasDefaultValue.Should().BeTrue();
        }
    }
}
