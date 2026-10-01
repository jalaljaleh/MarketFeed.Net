using MarketFeed.Net.Models;
using System.Diagnostics;
using System.Timers;

namespace MarketFeed.Net.Abstractions;

/// <summary>
/// A source of free public market data (a crypto exchange). Every exchange implements this
/// interface, so consumers can switch or combine providers without changing their code.
/// </summary>
public interface IMarketDataProvider
{
    /// <summary>Identifier of the exchange.</summary>
    ExchangeId Exchange { get; }

    /// <summary>Gets a value indicating whether the exchange exposes a public WebSocket feed.</summary>
    bool SupportsRealtime { get; }

    /// <summary>Base address of the REST API.</summary>
    Uri BaseAddress { get; }

    /// <summary>
    /// Returns the ticker (last price, bid/ask, 24h change and volume) of one market.
    /// </summary>
    /// <param name="symbol">Unified symbol such as <c>BTC/USDT</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>Returns tickers of every market offered by the exchange.</summary>
    Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the order book (depth) of one market.</summary>
    /// <param name="symbol">Unified symbol.</param>
    /// <param name="depth">Number of levels per side.</param>
    Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent public trades of one market.</summary>
    /// <param name="limit">Maximum number of trades.</param>
    Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Returns OHLCV candles of one market.</summary>
    /// <param name="timeframe">Candle interval.</param>
    /// <param name="limit">Maximum number of candles.</param>
    /// <param name="from">Optional start of the range.</param>
    /// <param name="to">Optional end of the range.</param>
    Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string symbol,
        Timeframe timeframe,
        int limit = 100,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns market metadata (precision, minimum order size, status).</summary>
    Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Source of asset metadata (name, rank, market cap, supply).</summary>
public interface IAssetInfoProvider
{
    /// <summary>Returns metadata of a single asset by its id (for example <c>bitcoin</c>).</summary>
    Task<AssetInfo> GetAssetAsync(string assetId, CancellationToken cancellationToken = default);

    /// <summary>Returns metadata of many assets.</summary>
    Task<IReadOnlyList<AssetInfo>> GetAssetsAsync(IEnumerable<string> assetIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// A realtime (WebSocket) market data feed. Implementations translate the exchange specific
/// wire protocol into the unified models of this library.
/// </summary>
public interface IRealtimeMarketFeed
{
    /// <summary>Opens the connection to the exchange stream.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the connection.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Subscribes to ticker updates of a market.</summary>
    Task SubscribeTickerAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to order book updates of a market.</summary>
    Task SubscribeOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to the public trade stream of a market.</summary>
    Task SubscribeTradesAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>Unsubscribes from every channel of a market.</summary>
    Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken = default);
}