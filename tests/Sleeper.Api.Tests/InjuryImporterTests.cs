using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sleeper.Api.Injuries;
using Sleeper.Api.NflData;
using Sleeper.Api.NflData.Models;

namespace Sleeper.Api.Tests;

public sealed class InjuryImporterTests
{
    [Fact]
    public async Task SleeperImport_PreservesDescriptionWithoutInferringOnset()
    {
        var player = System.Text.Json.JsonSerializer.Deserialize<Sleeper.Api.Models.Player>("""
            {"player_id":"p1","injury_status":"Out","injury_body_part":"Knee","injury_notes":"Follow-up pending","injury_start_date":"2026-09-13"}
            """)!;
        var sleeper = Substitute.For<ISleeperClient>();
        sleeper.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>()).Returns(new Dictionary<string, Sleeper.Api.Models.Player> { ["p1"] = player });
        var store = Substitute.For<IInjuryStore>();
        store.GetCohortAsync(Arg.Any<CancellationToken>()).Returns([
            new InjuryCohortPlayer("p1", 1, "Test Player", "TE", "LV", null, "test", DateTimeOffset.UtcNow)
        ]);
        IReadOnlyList<InjuryObservationInput>? captured = null;
        store.RecordBatchAsync(Arg.Do<IReadOnlyList<InjuryObservationInput>>(inputs => captured = inputs), Arg.Any<CancellationToken>())
            .Returns(new InjuryBatchWriteResult([], 1, 0));
        using var http = new HttpClient();
        var importer = new NflverseInjuryImporter(http, sleeper, Substitute.For<INflDataClient>(), store, Options.Create(new InjuryImportOptions()));

        await importer.ImportSleeperAsync();

        captured.Should().ContainSingle();
        captured![0].PrimaryInjury.Should().Be("Knee");
        captured[0].Notes.Should().Contain("Follow-up pending");
        captured[0].InjuryOccurredAt.Should().BeNull();
        captured[0].SourcePublishedAt.Should().BeNull();
    }

    [Fact]
    public async Task NflverseImport_ToleratesDuplicateAndSentinelMappings_AndWritesHistoryOnly()
    {
        const string csv = """
            season,season_type,game_type,team,week,gsis_id,position,full_name,first_name,last_name,report_primary_injury,report_secondary_injury,report_status,practice_primary_injury,practice_secondary_injury,practice_status
            2025,REG,REG,BUF,3,g1,RB,Test Player,Test,Player,Knee,,Questionable,Knee,,Limited Participation in Practice
            """;
        var nflData = Substitute.For<INflDataClient>();
        nflData.GetPlayerIdMappingsAsync(Arg.Any<CancellationToken>()).Returns([
            new PlayerIdMapping { GsisId = "g1", SleeperId = "p1" },
            new PlayerIdMapping { GsisId = "g1", SleeperId = "p1" },
            new PlayerIdMapping { GsisId = "NA", SleeperId = "NA" }
        ]);
        var store = Substitute.For<IInjuryStore>();
        store.GetCohortAsync(Arg.Any<CancellationToken>()).Returns([
            new InjuryCohortPlayer("p1", 1, "Test Player", "RB", "BUF", "g1", "test", DateTimeOffset.UtcNow)
        ]);
        IReadOnlyList<InjuryObservationInput>? captured = null;
        store.RecordBatchAsync(
                Arg.Do<IReadOnlyList<InjuryObservationInput>>(inputs => captured = inputs),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new InjuryBatchWriteResult([], 1, 0)));
        using var http = new HttpClient(new StringContentHandler(csv));
        var sleeper = Substitute.For<ISleeperClient>();
        var importer = new NflverseInjuryImporter(
            http,
            sleeper,
            nflData,
            store,
            Options.Create(new InjuryImportOptions()));

        var result = await importer.ImportNflverseAsync(2025);

        result.ImportedRows.Should().Be(1);
        captured.Should().ContainSingle();
        captured![0].SleeperId.Should().Be("p1");
        captured[0].Scope.Should().Be(InjuryObservationScope.Historical);
        captured[0].Season.Should().Be(2025);
        captured[0].Week.Should().Be(3);
    }

    private sealed class StringContentHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
    }
}
