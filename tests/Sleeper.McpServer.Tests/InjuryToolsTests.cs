using FluentAssertions;
using NSubstitute;
using Sleeper.Api.Injuries;
using Sleeper.McpServer.Tools;

namespace Sleeper.McpServer.Tests;

public class InjuryToolsTests
{
    [Fact]
    public async Task RecordInjuryObservation_RejectsMissingRequiredFields()
    {
        var store = Substitute.For<IInjuryStore>();

        var result = await InjuryTools.RecordInjuryObservation(store, " ", "reporter", "out");

        result.Should().Be("Error: Sleeper player ID is required.");
        await store.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Fact]
    public async Task RecordInjuryObservation_RejectsUnsupportedConfidence()
    {
        var store = Substitute.For<IInjuryStore>();

        var result = await InjuryTools.RecordInjuryObservation(
            store,
            "p1",
            "reporter",
            "out",
            confidence: "guess");

        result.Should().Be("Error: Confidence must be 'official', 'reporter', or 'inferred'.");
        await store.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }
}
