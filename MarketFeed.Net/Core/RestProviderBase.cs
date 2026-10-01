using MarketFeed.Net.Abstractions;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Core;

/// <summary>
/// Base class shared by every exchange provider. It owns the <see cref="HttpClient"/>, the JSON
/// transport, retry with exponential backoff, a client-side rate limiter and the unified error model,
/// so that concrete providers only contain mapping logic.
/// </summary>
public abstract class RestProviderBase : IMarketDataProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger _logger;
    private readonly RateLimiter _rateLimiter;
    private readonly Random _jitter = new();

    /// <summary>Creates the provider with its own <see cref="HttpClient"/>.</summary>
    protected RestProviderBase(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
    {
        Options = options ?? new MarketFeedClientOptions();
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsHttpClient = true;
        _logger = logger ?? NullLogger.Instance;
        _rateLimiter = new RateLimiter(Options.EnableClientRateLimiting, Options.MinRequestInterval);
        _httpClient.BaseAddress ??= BaseAddress;
        _httpClient.Timeout = Options.Timeout;
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Options.UserAgent);
    }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/> (IHttpClientFactory).</summary>
    protected RestProviderBase(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
    {
        Options = options ?? new MarketFeedClientOptions();
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = false;
        _logger = logger ?? NullLogger.Instance;
        _rateLimiter = new RateLimiter(Options.EnableClientRateLimiting, Options.MinRequestInterval);
        _httpClient.BaseAddress ??= BaseAddress;
        _httpClient.Timeout = Options.Timeout;
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Options.UserAgent);
    }

    /// <inheritdoc />
    public abstract ExchangeId Exchange { get; }

    /// <inheritdoc />
    public abstract Uri BaseAddress { get; }

    /// <inheritdoc />
    public virtual bool SupportsRealtime => true;

    /// <summary>Effective options of this provider.</summary>
    public MarketFeedClientOptions Options { get; }

    /// <inheritdoc />
    public abstract Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string symbol, Timeframe timeframe, int limit = 100,
        DateTimeOffset? from = null, DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a GET request, validates the transport status and returns the parsed JSON root.
    /// Handles 429 (respecting the <c>Retry-After</c> header), 5xx and network failures with retries.
    /// </summary>
    /// <param name="path">Absolute path, e.g. <c>/api/v3/ticker/24hr</c>.</param>
    /// <param name="query">Optional query string (without the leading <c>?</c>).</param>
    /// <param name="group">Rate-limit group; providers can isolate endpoint families.</param>
    protected async Task<JsonNode> GetJsonAsync(string path, string? query = null, string group = "default", CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrEmpty(query) ? path : $"{path}?{query}";
        var attempt = 0;

        while (true)
        {
            attempt++;
            await _rateLimiter.WaitAsync($"{Exchange}:{group}", cancellationToken).ConfigureAwait(false);

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt > Options.MaxRetries)
                {
                    throw new MarketDataException(Exchange, $"Network failure while calling {url}.", ex);
                }

                await DelayAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode == 429)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? Options.RetryBaseDelay * attempt;
                    if (attempt <= Options.MaxRetries)
                    {
                        await Task.Delay(retryAfter, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new RateLimitException(Exchange, "Rate limit exceeded.", retryAfter);
                }

                if ((int)response.StatusCode >= 500 && attempt <= Options.MaxRetries)
                {
                    await DelayAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new MarketDataException(Exchange, $"HTTP {(int)response.StatusCode} from {url}: {Truncate(body)}");
                }

                try
                {
                    return JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body)
                        ?? throw new MarketDataException(Exchange, "Empty JSON response.");
                }
                catch (System.Text.Json.JsonException ex)
                {
                    throw new MarketDataException(Exchange, $"Invalid JSON from {url}.", ex);
                }
            }
        }
    }

    /// <summary>Builds a URL encoded query string, skipping <c>null</c> values.</summary>
    protected static string Query(params (string Key, object? Value)[] parameters)
    {
        var parts = new List<string>();

        foreach (var (key, value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            var rendered = value switch
            {
                bool flag => flag ? "true" : "false",
                decimal number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                double number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            };

            if (string.IsNullOrEmpty(rendered))
            {
                continue;
            }

            parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(rendered)}");
        }

        return string.Join('&', parts);
    }

    /// <summary>Reads a decimal from a JSON value that may be a string or a number.</summary>
    protected static decimal Dec(JsonNode? node, decimal fallback = 0m)
    {
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => decimal.Parse(
                    node.GetValue<string>() ?? "0",
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture),
                System.Text.Json.JsonValueKind.Number => node.GetValue<decimal>(),
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Reads an array element as decimal.</summary>
    protected static decimal DecAt(JsonArray? array, int index, decimal fallback = 0m)
        => array is not null && index < array.Count ? Dec(array[index], fallback) : fallback;

    /// <summary>Converts a unix timestamp (seconds or milliseconds) to a UTC <see cref="DateTimeOffset"/>.</summary>
    protected static DateTimeOffset FromUnix(long value)
        => value > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);

    /// <summary>Normalises a unified symbol for this exchange.</summary>
    protected static UnifiedSymbol Sym(string symbol) => UnifiedSymbol.Parse(symbol);

    /// <summary>Throws <see cref="SymbolNotFoundException"/> when the symbol cannot be parsed.</summary>
    protected static UnifiedSymbol RequireSymbol(string symbol)
    {
        try
        {
            return UnifiedSymbol.Parse(symbol);
        }
        catch (Exception ex)
        {
            throw new SymbolNotFoundException(symbol, ex);
        }
    }

    private async Task DelayAsync(int attempt, TimeSpan? fixedDelay, CancellationToken cancellationToken)
    {
        var delay = fixedDelay ?? TimeSpan.FromMilliseconds(Options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        await Task.Delay(delay + TimeSpan.FromMilliseconds(_jitter.Next(0, 120)), cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "…";

    /// <summary>Releases the owned <see cref="HttpClient"/>.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Exception thrown when a market-data request fails for an unknown reason.</summary>
public class MarketDataException : Exception
{
    /// <summary>Exchange that produced the failure.</summary>
    public ExchangeId Exchange { get; }

    /// <summary>Creates the exception.</summary>
    public MarketDataException(ExchangeId exchange, string message, Exception? inner = null)
        : base($"[{exchange}] {message}", inner) => Exchange = exchange;
}

/// <summary>Exception thrown when the exchange rate limit is exhausted.</summary>
public sealed class RateLimitException : MarketDataException
{
    /// <summary>How long the caller should wait before retrying.</summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>Creates the exception.</summary>
    public RateLimitException(ExchangeId exchange, string message, TimeSpan retryAfter)
        : base(exchange, message) => RetryAfter = retryAfter;
}

/// <summary>Exception thrown when a symbol is unknown or malformed.</summary>
public sealed class SymbolNotFoundException : MarketDataException
{
    /// <summary>Creates the exception.</summary>
    public SymbolNotFoundException(string symbol, Exception? inner = null)
        : base(ExchangeId.Binance, $"Unknown or malformed market symbol '{symbol}'.", inner)
    {
    }
}

/// <summary>Thread-safe token bucket that keeps each provider below its documented rate limit.</summary>
internal sealed class RateLimiter
{
    private readonly bool _enabled;
    private readonly TimeSpan _minInterval;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public RateLimiter(bool enabled, TimeSpan minInterval)
    {
        _enabled = enabled;
        _minInterval = minInterval;
    }

    /// <summary>Waits until a request of the given group may be sent.</summary>
    public async ValueTask WaitAsync(string group, CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return;
        }

        var gate = _gates.GetOrAdd(group, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(_minInterval, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}