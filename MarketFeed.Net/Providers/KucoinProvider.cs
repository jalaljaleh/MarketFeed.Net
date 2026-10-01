using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>KuCoin</b> (https://api.kucoin.com).
/// Endpoints: <c>/api/v1/market/stats</c>, <c>/api/v1/market/allTickers</c>,
/// <c>/api/v1/market/orderbook/level2_20</c>, <c>/api/v1/market/trades</c>,
/// <c>/api/v1/market/candles</c> and <c>/api/v1/symbols</c>.
/// </summary>
public sealed class KucoinProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public KucoinProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public KucoinProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public KucoinProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Kucoin;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://api.kucoin.com");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v1/market/stats",
            Query(("symbol", s.ToDash())), "ticker", cancellationToken).ConfigureAwait(false);

        var item = node["data"] ?? throw new MarketDataException(Exchange, $"No ticker for {s}.");
        var last = Dec(item["last"]);

        return new Ticker(
            Exchange,
            s.ToString(),
            last,
            Dec(item["buy"]),
            Dec(item["sell"]),
            Dec(item["high"]),
            Dec(item["low"]),
            last - Dec(item["changePrice"]),
            Dec(item["vol"]),
            Dec(item["volValue"]),
            Dec(item["changeRate"]) * 100m,
            FromUnix((long)Dec(item["ts"])));
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v1/market/allTickers", null, "ticker", cancellationToken).ConfigureAwait(false);
        var list = new List<Ticker>();

        if (node["data"]?["ticker"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item?["symbol"]?.GetValue<string>() is not { } symbol)
                {
                    continue;
                }

                var last = Dec(item["last"]);
                list.Add(new Ticker(
                    Exchange,
                    UnifiedSymbol.Parse(symbol).ToString(),
                    last,
                    Dec(item["buy"]),
                    Dec(item["sell"]),
                    Dec(item["high"]),
                    Dec(item["low"]),
                    last - Dec(item["changePrice"]),
                    Dec(item["vol"]),
                    Dec(item["volValue"]),
                    Dec(item["changeRate"]) * 100m,
                    DateTimeOffset.UtcNow));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);
        // KuCoin exposes fixed level2_20 and level2_100 snapshots.
        var path = depth <= 20 ? "/api/v1/market/orderbook/level2_20" : "/api/v1/market/orderbook/level2_100";

        var node = await GetJsonAsync(path, Query(("symbol", s.ToDash())), "depth", cancellationToken).ConfigureAwait(false);
        var data = node["data"];

        return new OrderBook
        {
            Exchange = Exchange,
            Symbol = s.ToString(),
            Bids = MapLevels(data?["bids"] as JsonArray, depth),
            Asks = MapLevels(data?["asks"] as JsonArray, depth),
            Timestamp = FromUnix((long)Dec(data?["ts"])),
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v1/market/trades",
            Query(("symbol", s.ToDash())), "trades", cancellationToken).ConfigureAwait(false);

        var trades = new List<Trade>();
        if (node["data"] is JsonArray array)
        {
            foreach (var item in array.Take(limit))
            {
                if (item is null)
                {
                    continue;
                }

                trades.Add(new Trade(
                    Exchange,
                    s.ToString(),
                    Dec(item["price"]),
                    Dec(item["size"]),
                    item["side"]?.GetValue<string>() == "sell" ? TradeSide.Sell : TradeSide.Buy,
                    FromUnix((long)Dec(item["time"])),
                    (long)Dec(item["sequence"])));
            }
        }

        return trades;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string symbol, Timeframe timeframe, int limit = 100,
        DateTimeOffset? from = null, DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v1/market/candles",
            Query(("symbol", s.ToDash()), ("type", timeframe.ToKucoinType())), "candles", cancellationToken).ConfigureAwait(false);

        var candles = new List<Candle>();
        if (node["data"] is JsonArray array)
        {
            foreach (var row in array.Take(limit))
            {
                if (row is not JsonArray k)
                {
                    continue;
                }

                // KuCoin order: [time, open, close, high, low, volume, turnover]
                candles.Add(new Candle(
                    FromUnix((long)DecAt(k, 0)),
                    DecAt(k, 1), DecAt(k, 3), DecAt(k, 4), DecAt(k, 2),
                    DecAt(k, 5), DecAt(k, 6)));
            }

            candles.Reverse();   // newest first in the raw payload
        }

        return candles;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v1/symbols", null, "info", cancellationToken).ConfigureAwait(false);

        var markets = new List<MarketInfo>();
        if (node["data"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item?["symbol"]?.GetValue<string>() is not { } symbol)
                {
                    continue;
                }

                markets.Add(new MarketInfo
                {
                    Exchange = Exchange,
                    Symbol = UnifiedSymbol.Parse(symbol).ToString(),
                    BaseAsset = item["baseCurrency"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                    QuoteAsset = item["quoteCurrency"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                    MinQuantity = Dec(item["baseMinSize"]),
                    MinNotional = Dec(item["quoteMinSize"]),
                    PricePrecision = PrecisionFromStep(item["priceIncrement"]),
                    QuantityPrecision = PrecisionFromStep(item["baseIncrement"]),
                    Status = item["enableTrading"]?.GetValue<bool>() == true ? MarketStatus.Active : MarketStatus.Halted,
                });
            }
        }

        return markets;
    }

    private static int PrecisionFromStep(JsonNode? step)
    {
        var value = step?.GetValue<string>() ?? "0.00000001";
        var dot = value.IndexOf('.');
        return dot < 0 ? 0 : value.Length - dot - 1;
    }

    private static IReadOnlyList<OrderBookLevel> MapLevels(JsonArray? array, int depth)
    {
        var levels = new List<OrderBookLevel>();
        if (array is null)
        {
            return levels;
        }

        foreach (var level in array.Take(depth))
        {
            if (level is JsonArray row)
            {
                levels.Add(new OrderBookLevel(DecAt(row, 0), DecAt(row, 1)));
            }
        }

        return levels;
    }
}