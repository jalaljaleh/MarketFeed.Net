using MarketFeed.Net.Abstractions;
using MarketFeed.Net.Models;

namespace MarketFeed.Net.Core;

/// <summary>A best-price point across exchanges.</summary>
/// <param name="Exchange">Exchange offering the price.</param>
/// <param name="Price">Price level.</param>
/// <param name="Quantity">Quantity available at that level.</param>
public sealed record QuotePoint(ExchangeId Exchange, decimal Price, decimal Quantity);

/// <summary>Aggregated view of one symbol across every configured exchange.</summary>
public sealed class AggregatedQuote
{
    /// <summary>Unified symbol.</summary>
    public string Symbol { get; init; } = string.Empty;

    /// <summary>Highest bid across exchanges (the best price to sell at).</summary>
    public QuotePoint? BestBid { get; init; }

    /// <summary>Lowest ask across exchanges (the best price to buy at).</summary>
    public QuotePoint? BestAsk { get; init; }

    /// <summary>Median of the last prices.</summary>
    public decimal MedianPrice { get; init; }

    /// <summary>Spread between best bid and best ask.</summary>
    public decimal Spread => BestBid is null || BestAsk is null ? 0m : BestAsk.Price - BestBid.Price;

    /// <summary>Relative spread in percent.</summary>
    public decimal SpreadPercent => BestBid is null || BestAsk is null || BestBid.Price == 0
        ? 0m
        : (BestAsk.Price - BestBid.Price) / BestBid.Price * 100m;

    /// <summary>All the sources that contributed.</summary>
    public IReadOnlyList<Ticker> Sources { get; init; } = Array.Empty<Ticker>();

    /// <summary>Timestamp of the aggregation.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Descriptive statistics of a price series across exchanges.</summary>
public sealed class PriceStatistics
{
    /// <summary>Lowest price.</summary>
    public decimal Min { get; init; }

    /// <summary>Exchange offering <see cref="Min"/>.</summary>
    public ExchangeId MinExchange { get; init; }

    /// <summary>Highest price.</summary>
    public decimal Max { get; init; }

    /// <summary>Exchange offering <see cref="Max"/>.</summary>
    public ExchangeId MaxExchange { get; init; }

    /// <summary>Arithmetic mean.</summary>
    public decimal Mean { get; init; }

    /// <summary>Median price.</summary>
    public decimal Median { get; init; }

    /// <summary>Population standard deviation.</summary>
    public decimal StandardDeviation { get; init; }

    /// <summary>Dispersion of the prices in percent (standard deviation / mean).</summary>
    public decimal DispersionPercent => Mean == 0 ? 0m : StandardDeviation / Mean * 100m;

    /// <summary>Number of exchanges that reported a price.</summary>
    public int SampleCount { get; init; }
}

/// <summary>
/// The facade of the library. It exposes one API for every supported exchange, adds caching,
/// parallel fan-out and cross-exchange aggregation on top of the individual providers.
/// </summary>
/// <example>
/// <code>
/// await using var feed = new MarketFeedClient();
/// var ticker = await feed.GetTickerAsync(ExchangeId.Binance, "BTC/USDT");
/// var quote = await feed.GetAggregatedQuoteAsync("BTC/USDT");
/// </code>
/// </example>
public sealed class MarketFeedClient : IAsyncDisposable
{
    private readonly Dictionary<ExchangeId, IMarketDataProvider> _providers = new();
    private readonly MarketDataCache _cache = new();
    private readonly MarketFeedClientOptions _options;

    /// <summary>
    /// Creates the client. When <paramref name="providers"/> is omitted, all built-in providers
    /// are registered automatically.
    /// </summary>
    /// <param name="options">Configuration.</param>
    /// <param name="providers">Optional custom set of providers.</param>
    /// <param name="httpClient">Optional shared <see cref="HttpClient"/>.</param>
    public MarketFeedClient(
        MarketFeedClientOptions? options = null,
        IEnumerable<IMarketDataProvider>? providers = null,
        HttpClient? httpClient = null)
    {
        _options = options ?? new MarketFeedClientOptions();

        foreach (var provider in providers ?? CreateDefaultProviders(_options, httpClient))
        {
            _providers[provider.Exchange] = provider;
        }
    }

    /// <summary>Registered providers keyed by exchange.</summary>
    public IReadOnlyDictionary<ExchangeId, IMarketDataProvider> Providers => _providers;

    /// <summary>Shared cache instance (can be cleared between polling rounds).</summary>
    public MarketDataCache Cache => _cache;

    /// <summary>Returns the provider of a specific exchange.</summary>
    public IMarketDataProvider GetProvider(ExchangeId exchange)
        => _providers.TryGetValue(exchange, out var provider)
            ? provider
            : throw new MarketDataException(exchange, "Provider is not registered.");

    /// <summary>Returns the ticker of one market on one exchange (cached).</summary>
    public Task<Ticker> GetTickerAsync(ExchangeId exchange, string symbol, CachePolicy cachePolicy = CachePolicy.Default, CancellationToken cancellationToken = default)
        => WithCacheAsync($"{exchange}:ticker:{UnifiedSymbol.Parse(symbol)}", cachePolicy,
            () => GetProvider(exchange).GetTickerAsync(symbol, cancellationToken), cancellationToken);

    /// <summary>
    /// Returns the ticker of one market from <b>every</b> registered exchange in parallel.
    /// Exchanges that fail are skipped when <see cref="MarketFeedClientOptions.IgnoreProviderFailures"/> is <c>true</c>.
    /// </summary>
    public async Task<IReadOnlyList<Ticker>> GetTickerBoardAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var tasks = _providers.Values.Select(async provider =>
        {
            try
            {
                return await GetTickerAsync(provider.Exchange, symbol, cancellationToken:cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(t => t is not null).Select(t => t!).OrderByDescending(t => t.LastPrice).ToList();
    }

    /// <summary>Aggregates the ticker board into best bid/ask and the median price.</summary>
    public async Task<AggregatedQuote> GetAggregatedQuoteAsync(string symbol, CachePolicy cachePolicy = CachePolicy.Default, CancellationToken cancellationToken = default)
    {
        var board = await GetTickerBoardAsync(symbol, cancellationToken).ConfigureAwait(false);
        return PriceAggregator.Aggregate(symbol, board);
    }

    /// <summary>Computes descriptive statistics of the price of a symbol across exchanges.</summary>
    public async Task<PriceStatistics> GetPriceStatisticsAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var board = await GetTickerBoardAsync(symbol, cancellationToken).ConfigureAwait(false);
        return PriceAggregator.Statistics(board);
    }

    /// <summary>Returns the order book of a market on one exchange.</summary>
    public Task<OrderBook> GetOrderBookAsync(ExchangeId exchange, string symbol, int depth = 20, CachePolicy cachePolicy = CachePolicy.Default, CancellationToken cancellationToken = default)
        => WithCacheAsync($"{exchange}:book:{UnifiedSymbol.Parse(symbol)}:{depth}", cachePolicy,
            () => GetProvider(exchange).GetOrderBookAsync(symbol, depth, cancellationToken), cancellationToken);

    /// <summary>Returns the recent public trades of a market on one exchange.</summary>
    public Task<IReadOnlyList<Trade>> GetTradesAsync(ExchangeId exchange, string symbol, int limit = 50, CancellationToken cancellationToken = default)
        => GetProvider(exchange).GetTradesAsync(symbol, limit, cancellationToken);

    /// <summary>Returns OHLCV candles of a market on one exchange.</summary>
    public Task<IReadOnlyList<Candle>> GetCandlesAsync(
        ExchangeId exchange, string symbol, Timeframe timeframe, int limit = 100,
        DateTimeOffset? from = null, DateTimeOffset? to = null,
        CachePolicy cachePolicy = CachePolicy.Default, CancellationToken cancellationToken = default)
        => WithCacheAsync($"{exchange}:candles:{UnifiedSymbol.Parse(symbol)}:{timeframe}:{limit}", cachePolicy,
            () => GetProvider(exchange).GetCandlesAsync(symbol, timeframe, limit, from, to, cancellationToken), cancellationToken);

    /// <summary>Returns the market metadata of one exchange.</summary>
    public Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(ExchangeId exchange, CancellationToken cancellationToken = default)
        => GetProvider(exchange).GetMarketsAsync(cancellationToken);

    /// <summary>
    /// Returns the metadata of a crypto asset (name, rank, market cap) from the first registered
    /// <see cref="IAssetInfoProvider"/> (CoinGecko by default).
    /// </summary>
    public async Task<AssetInfo> GetAssetAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var provider = _providers.Values.OfType<IAssetInfoProvider>().FirstOrDefault()
            ?? throw new MarketDataException(ExchangeId.CoinGecko, "No asset metadata provider is registered.");

        return await provider.GetAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns metadata of many assets at once.</summary>
    public async Task<IReadOnlyList<AssetInfo>> GetAssetsAsync(IEnumerable<string> assetIds, CancellationToken cancellationToken = default)
    {
        var provider = _providers.Values.OfType<IAssetInfoProvider>().FirstOrDefault()
            ?? throw new MarketDataException(ExchangeId.CoinGecko, "No asset metadata provider is registered.");

        return await provider.GetAssetsAsync(assetIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Registers an additional provider at runtime.</summary>
    public void RegisterProvider(IMarketDataProvider provider) => _providers[provider.Exchange] = provider;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        foreach (var provider in _providers.Values.OfType<IDisposable>())
        {
            provider.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<T> WithCacheAsync<T>(string key, CachePolicy policy, Func<Task<T>> factory, CancellationToken cancellationToken)
        => await _cache.GetOrCreateAsync(key, _options.CacheTtl, factory, policy, cancellationToken).ConfigureAwait(false);

    private static IEnumerable<IMarketDataProvider> CreateDefaultProviders(MarketFeedClientOptions options, HttpClient? httpClient)
    {
        yield return new Providers.BinanceProvider(options);
        yield return new Providers.OkxProvider(options);
        yield return new Providers.BybitProvider(options);
        yield return new Providers.KrakenProvider(options);
        yield return new Providers.CoinbaseProvider(options);
        yield return new Providers.KucoinProvider(options);
        yield return new Providers.BitgetProvider(options);
        yield return new Providers.GateIoProvider(options);
        yield return new Providers.MexcProvider(options);
        yield return new Providers.BitstampProvider(options);
        yield return new Providers.BitfinexProvider(options);
        yield return new Providers.NobitexProvider(options);
        yield return new Providers.CoinGeckoProvider(options);
    }
}