using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>Binance</b> (https://api.binance.com).
/// Endpoints: <c>/api/v3/ticker/24hr</c>, <c>/api/v3/depth</c>, <c>/api/v3/trades</c>,
/// <c>/api/v3/klines</c>, <c>/api/v3/exchangeInfo</c>. No API key is required.
/// </summary>
/// <remarks>Rate limit: 6,000 request weight per minute per IP; a ticker call costs 1–2 weight.</remarks>
public sealed class BinanceProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler (useful for tests).</summary>
    public BinanceProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public BinanceProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public BinanceProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Binance;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://api.binance.com");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);
        var wire = s.ToCompact();

        var node = await GetJsonAsync("/api/v3/ticker/24hr", Query(("symbol", wire)), "ticker", cancellationToken)
            .ConfigureAwait(false);

        return new Ticker(
            Exchange,
            s.ToString(),
            LastPrice: Dec(node["lastPrice"]),
            BidPrice: Dec(node["bidPrice"]),
            AskPrice: Dec(node["askPrice"]),
            High24h: Dec(node["highPrice"]),
            Low24h: Dec(node["lowPrice"]),
            Open24h: Dec(node["openPrice"]),
            Volume24h: Dec(node["volume"]),
            QuoteVolume24h: Dec(node["quoteVolume"]),
            Change24hPercent: Dec(node["priceChangePercent"]),
            Timestamp: FromUnix((long)Dec(node["closeTime"])));
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v3/ticker/24hr", null, "ticker", cancellationToken).ConfigureAwait(false);
        if (node is not JsonArray array)
        {
            return Array.Empty<Ticker>();
        }

        var result = new List<Ticker>(array.Count);
        foreach (var item in array)
        {
            if (item is null)
            {
                continue;
            }

            result.Add(new Ticker(
                Exchange,
                UnifiedSymbol.Parse(item["symbol"]?.GetValue<string>() ?? string.Empty).ToString(),
                Dec(item["lastPrice"]), Dec(item["bidPrice"]), Dec(item["askPrice"]),
                Dec(item["highPrice"]), Dec(item["lowPrice"]), Dec(item["openPrice"]),
                Dec(item["volume"]), Dec(item["quoteVolume"]), Dec(item["priceChangePercent"]),
                FromUnix((long)Dec(item["closeTime"]))));
        }

        return result;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v3/depth",
            Query(("symbol", s.ToCompact()), ("limit", depth)), "depth", cancellationToken).ConfigureAwait(false);

        return new OrderBook
        {
            Exchange = Exchange,
            Symbol = s.ToString(),
            Bids = MapLevels(node["bids"] as JsonArray),
            Asks = MapLevels(node["asks"] as JsonArray),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v3/trades",
            Query(("symbol", s.ToCompact()), ("limit", limit)), "trades", cancellationToken).ConfigureAwait(false);

        if (node is not JsonArray array)
        {
            return Array.Empty<Trade>();
        }

        var trades = new List<Trade>(array.Count);
        foreach (var item in array)
        {
            if (item is null)
            {
                continue;
            }

            trades.Add(new Trade(
                Exchange,
                s.ToString(),
                Dec(item["price"]),
                Dec(item["qty"]),
                item["isBuyerMaker"]?.GetValue<bool>() == true ? TradeSide.Sell : TradeSide.Buy,
                FromUnix((long)Dec(item["time"])),
                (long)Dec(item["id"])));
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

        var node = await GetJsonAsync("/api/v3/klines",
            Query(
                ("symbol", s.ToCompact()),
                ("interval", timeframe.ToShortString()),
                ("startTime", from?.ToUnixTimeMilliseconds()),
                ("endTime", to?.ToUnixTimeMilliseconds()),
                ("limit", limit)),
            "candles", cancellationToken).ConfigureAwait(false);

        if (node is not JsonArray array)
        {
            return Array.Empty<Candle>();
        }

        var candles = new List<Candle>(array.Count);
        foreach (var row in array)
        {
            if (row is not JsonArray k)
            {
                continue;
            }

            // [openTime, open, high, low, close, volume, closeTime, quoteVolume, ...]
            candles.Add(new Candle(
                FromUnix((long)DecAt(k, 0)),
                DecAt(k, 1), DecAt(k, 2), DecAt(k, 3), DecAt(k, 4),
                DecAt(k, 5), DecAt(k, 7)));
        }

        return candles;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v3/exchangeInfo", null, "info", cancellationToken).ConfigureAwait(false);
        if (node["symbols"] is not JsonArray array)
        {
            return Array.Empty<MarketInfo>();
        }

        var markets = new List<MarketInfo>(array.Count);
        foreach (var item in array)
        {
            if (item is null)
            {
                continue;
            }

            var baseAsset = item["baseAsset"]?.GetValue<string>() ?? string.Empty;
            var quoteAsset = item["quoteAsset"]?.GetValue<string>() ?? string.Empty;

            markets.Add(new MarketInfo
            {
                Exchange = Exchange,
                Symbol = $"{baseAsset.ToUpperInvariant()}/{quoteAsset.ToUpperInvariant()}",
                BaseAsset = baseAsset.ToUpperInvariant(),
                QuoteAsset = quoteAsset.ToUpperInvariant(),
                PricePrecision = item["quoteAssetPrecision"]?.GetValue<int>() ?? 8,
                QuantityPrecision = item["baseAssetPrecision"]?.GetValue<int>() ?? 8,
                MinQuantity = MinFromFilter(item["filters"] as JsonArray, "LOT_SIZE", "minQty"),
                MinNotional = MinFromFilter(item["filters"] as JsonArray, "NOTIONAL", "minNotional"),
                Status = item["status"]?.GetValue<string>() == "TRADING" ? MarketStatus.Active : MarketStatus.Halted,
            });
        }

        return markets;
    }

    private static IReadOnlyList<OrderBookLevel> MapLevels(JsonArray? array)
    {
        if (array is null)
        {
            return Array.Empty<OrderBookLevel>();
        }

        var levels = new List<OrderBookLevel>(array.Count);
        foreach (var level in array)
        {
            if (level is JsonArray pair)
            {
                levels.Add(new OrderBookLevel(DecAt(pair, 0), DecAt(pair, 1)));
            }
        }

        return levels;
    }

    private static decimal MinFromFilter(JsonArray? filters, string filterType, string field)
    {
        if (filters is null)
        {
            return 0m;
        }

        foreach (var filter in filters)
        {
            if (filter?["filterType"]?.GetValue<string>() == filterType)
            {
                return Dec(filter[field]);
            }
        }

        return 0m;
    }
}