using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sleeper.Api.Models;

namespace Sleeper.Api.Tests;

public class CachedSleeperClientTests
{
    private static (CachedSleeperClient cached, ISleeperClient inner) Create()
    {
        var inner = Substitute.For<ISleeperClient>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var options = Options.Create(new SleeperClientOptions());
        return (new CachedSleeperClient(inner, cache, options), inner);
    }

    [Fact]
    public async Task GetUserAsync_CachesResult()
    {
        var (cached, inner) = Create();
        var user = new User("1", "test", "Test", null);
        inner.GetUserAsync("test", Arg.Any<CancellationToken>()).Returns(user);

        var first = await cached.GetUserAsync("test");
        var second = await cached.GetUserAsync("test");

        first.Should().BeSameAs(second);
        await inner.Received(1).GetUserAsync("test", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUserAsync_DoesNotCacheNullResult()
    {
        var (cached, inner) = Create();
        var user = new User("1", "missing", "Missing", null);
        inner.GetUserAsync("missing", Arg.Any<CancellationToken>()).Returns((User?)null, user);

        var first = await cached.GetUserAsync("missing");
        var second = await cached.GetUserAsync("missing");

        first.Should().BeNull();
        second.Should().BeSameAs(user);
        await inner.Received(2).GetUserAsync("missing", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUserAsync_ThrowsOperationCanceled_WhenCancellationRequestedBeforeCacheHit()
    {
        var (cached, inner) = Create();
        var user = new User("1", "test", "Test", null);
        inner.GetUserAsync("test", Arg.Any<CancellationToken>()).Returns(user);

        await cached.GetUserAsync("test");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = async () => await cached.GetUserAsync("test", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await inner.Received(1).GetUserAsync("test", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLeagueAsync_CachesResult()
    {
        var (cached, inner) = Create();
        var league = new League("lg1", "Test", "in_season", "nfl", "2025", null, 8, null, null, null, null, null, null);
        inner.GetLeagueAsync("lg1", Arg.Any<CancellationToken>()).Returns(league);

        var first = await cached.GetLeagueAsync("lg1");
        var second = await cached.GetLeagueAsync("lg1");

        first.Should().BeSameAs(second);
        await inner.Received(1).GetLeagueAsync("lg1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAllPlayersAsync_CachesLargePayload()
    {
        var (cached, inner) = Create();
        var players = new Dictionary<string, Player>
        {
            ["3086"] = new Player("3086", "Tom", "Brady", "QB", "NE", 40, "Active", 12, "Michigan", 14, ["QB"],
                null, "220", "6'4\"", "tombrady", "tom", "brady", 24, null, null, "nfl", null, null, null, null, null, null, null, null, null, null),
            ["SEA"] = new Player("SEA", "Seattle", "DEF", "DEF", "SEA", null, "Active", null, null, null, ["DEF"],
                null, null, null, "seattledef", "seattle", "def", null, null, null, "nfl", null, null, null, null, null, null, null, null, null, null)
        };
        inner.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>()).Returns(players);

        var first = await cached.GetAllPlayersAsync("nfl");
        var second = await cached.GetAllPlayersAsync("nfl");

        first.Should().BeSameAs(second);
        first.Should().ContainKey("SEA");
        first["SEA"].Position.Should().Be("DEF");
        await inner.Received(1).GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAllPlayersAsync_CoalescesConcurrentColdCacheRequests()
    {
        var (cached, inner) = Create();
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var players = new Dictionary<string, Player>();
        inner.GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                factoryStarted.TrySetResult();
                await releaseFactory.Task;
                return players;
            });

        var first = cached.GetAllPlayersAsync("nfl");
        await factoryStarted.Task;
        var second = cached.GetAllPlayersAsync("nfl");
        releaseFactory.SetResult();

        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(result => ReferenceEquals(result, players));
        await inner.Received(1).GetAllPlayersAsync("nfl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentKeysAreNotCached()
    {
        var (cached, inner) = Create();
        inner.GetUserAsync("alice", Arg.Any<CancellationToken>()).Returns(new User("1", "alice", "Alice", null));
        inner.GetUserAsync("bob", Arg.Any<CancellationToken>()).Returns(new User("2", "bob", "Bob", null));

        var alice = await cached.GetUserAsync("alice");
        var bob = await cached.GetUserAsync("bob");

        alice!.Username.Should().Be("alice");
        bob!.Username.Should().Be("bob");
        await inner.Received(1).GetUserAsync("alice", Arg.Any<CancellationToken>());
        await inner.Received(1).GetUserAsync("bob", Arg.Any<CancellationToken>());
    }
}
