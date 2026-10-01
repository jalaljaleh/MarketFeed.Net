using System.Collections.Concurrent;

namespace MarketFeed.Net.Core;

/// <summary>
/// A tiny thread-safe TTL cache used to avoid hammering the public endpoints of the exchanges.
/// It stores the cached task itself so that concurrent callers share a single in-flight request.
/// </summary>
public sealed class MarketDataCache
{
    private sealed record Entry(object Value, DateTimeOffset ExpiresAt)
    {
        public bool IsFresh(TimeProvider? clock = null) => (clock ?? TimeProvider.System).GetUtcNow() < ExpiresAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly TimeProvider _clock;

    /// <summary>Creates a cache.</summary>
    /// <param name="clock">Time source (injectable for unit tests).</param>
    public MarketDataCache(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Number of cached entries.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Returns the cached value when it is still fresh, otherwise runs <paramref name="factory"/>
    /// and stores the result for <paramref name="ttl"/>.
    /// </summary>
    public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory, CachePolicy policy = CachePolicy.Default, CancellationToken cancellationToken = default)
    {
        if (policy == CachePolicy.Bypass)
        {
            var fresh = await factory().ConfigureAwait(false);
            Set(key, fresh, ttl);
            return fresh;
        }

        if (_entries.TryGetValue(key, out var entry) && entry.Value is T cached)
        {
            if (entry.IsFresh(_clock) || policy == CachePolicy.Default)
            {
                return cached;
            }
        }

        var value = await factory().ConfigureAwait(false);
        Set(key, value, ttl);
        return value;
    }

    /// <summary>Stores a value manually (for example a WebSocket push).</summary>
    public void Set<T>(string key, T value, TimeSpan ttl) => _entries[key] = new Entry(value!, _clock.GetUtcNow() + ttl);

    /// <summary>Removes one entry.</summary>
    public void Remove(string key) => _entries.TryRemove(key, out _);

    /// <summary>Clears the whole cache.</summary>
    public void Clear() => _entries.Clear();
}