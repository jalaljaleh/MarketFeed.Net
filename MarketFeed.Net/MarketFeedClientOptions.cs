using Microsoft.Extensions.Logging;

namespace MarketFeed.Net;

/// <summary>Controls how cached data may be served.</summary>
public enum CachePolicy
{
    /// <summary>Return a fresh cached value when available, otherwise fetch.</summary>
    Default,

    /// <summary>Only return a value that is younger than the cache TTL; never hit the network for stale data.</summary>
    FreshOnly,

    /// <summary>Ignore the cache entirely and always hit the network.</summary>
    Bypass,
}

/// <summary>
/// Configuration for <see cref="MarketFeed.Net.Core.MarketFeedClient"/> and every provider.
/// Follows the Microsoft Options pattern so it can be bound from configuration.
/// </summary>
public sealed class MarketFeedClientOptions
{
    private TimeSpan _cacheTtl = TimeSpan.FromSeconds(2);
    private TimeSpan _timeout = TimeSpan.FromSeconds(15);
    private TimeSpan _retryBaseDelay = TimeSpan.FromMilliseconds(400);
    private int _maxRetries = 3;
    private int _defaultDepth = 20;

    /// <summary>
    /// How long a market-data response may be reused from the in-memory cache.
    /// Short TTLs protect you from the per-IP rate limits of the public endpoints.
    /// </summary>
    public TimeSpan CacheTtl
    {
        get => _cacheTtl;
        set => _cacheTtl = value < TimeSpan.Zero ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    /// <summary>Per-request timeout. Default is 15 seconds.</summary>
    public TimeSpan Timeout
    {
        get => _timeout;
        set => _timeout = value <= TimeSpan.Zero ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    /// <summary>Maximum automatic retries for transient failures (5xx, network, 429). Default is 3.</summary>
    public int MaxRetries
    {
        get => _maxRetries;
        set => _maxRetries = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    /// <summary>Base delay of the exponential backoff strategy.</summary>
    public TimeSpan RetryBaseDelay
    {
        get => _retryBaseDelay;
        set => _retryBaseDelay = value <= TimeSpan.Zero ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    /// <summary>Default order book depth when the caller does not request a specific one.</summary>
    public int DefaultDepth
    {
        get => _defaultDepth;
        set => _defaultDepth = value < 1 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    /// <summary>
    /// Enables a client-side token-bucket limiter per provider so you stay under the
    /// documented limits of each exchange.
    /// </summary>
    public bool EnableClientRateLimiting { get; set; } = true;

    /// <summary>Minimum interval between two calls to the same provider group.</summary>
    public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Value of the <c>User-Agent</c> header. Some exchanges block the default .NET user agent.
    /// </summary>
    public string UserAgent { get; set; } = "MarketFeed.Net/1.0";

    /// <summary>
    /// When <c>true</c>, a provider failure never breaks aggregated calls such as
    /// <c>GetTickerBoardAsync</c>; the failing exchange is simply skipped.
    /// </summary>
    public bool IgnoreProviderFailures { get; set; } = true;

    /// <summary>Optional logger factory used for diagnostics.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Creates options for a lightweight demo with a very short cache.</summary>
    public static MarketFeedClientOptions ForDemo() => new() { CacheTtl = TimeSpan.FromSeconds(1) };

    /// <summary>Creates options tuned for high-frequency polling of many markets.</summary>
    public static MarketFeedClientOptions ForHighFrequency() => new()
    {
        CacheTtl = TimeSpan.FromMilliseconds(250),
        MinRequestInterval = TimeSpan.FromMilliseconds(20),
        MaxRetries = 1,
    };
}