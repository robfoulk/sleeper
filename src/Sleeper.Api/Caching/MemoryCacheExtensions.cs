using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;

namespace Sleeper.Api.Caching;

internal static class MemoryCacheExtensions
{
    private static readonly ConditionalWeakTable<IMemoryCache, ConcurrentDictionary<string, CacheLock>> Locks = new();

    public static async Task<T> GetOrCreateIfNotNullAsync<T>(
        this IMemoryCache cache,
        string key,
        TimeSpan ttl,
        Func<Task<T>> factory,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (cache.TryGetValue(key, out T? cached) && cached is not null)
            return cached;

        var cacheLocks = Locks.GetValue(cache, _ => new ConcurrentDictionary<string, CacheLock>());
        var cacheLock = Acquire(cacheLocks, key);
        var entered = false;
        try
        {
            await cacheLock.Gate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;

            if (cache.TryGetValue(key, out cached) && cached is not null)
                return cached;

            var result = await factory().ConfigureAwait(false);
            if (result is not null)
                cache.Set(key, result, ttl);

            return result;
        }
        finally
        {
            if (entered)
                cacheLock.Gate.Release();
            Release(cacheLocks, key, cacheLock);
        }
    }

    private static CacheLock Acquire(ConcurrentDictionary<string, CacheLock> cacheLocks, string key)
    {
        while (true)
        {
            var cacheLock = cacheLocks.GetOrAdd(key, _ => new CacheLock());
            lock (cacheLock)
            {
                if (cacheLocks.TryGetValue(key, out var current) && ReferenceEquals(current, cacheLock))
                {
                    cacheLock.References++;
                    return cacheLock;
                }
            }
        }
    }

    private static void Release(
        ConcurrentDictionary<string, CacheLock> cacheLocks,
        string key,
        CacheLock cacheLock)
    {
        lock (cacheLock)
        {
            cacheLock.References--;
            if (cacheLock.References == 0 &&
                cacheLocks.TryGetValue(key, out var current) &&
                ReferenceEquals(current, cacheLock))
            {
                cacheLocks.TryRemove(key, out _);
            }
        }
    }

    private sealed class CacheLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References { get; set; }
    }
}