using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>MEXC</b> (https://api.mexc.com). The API is Binance-compatible:
/// <c>/api/v3/ticker/24hr</c>, <c>/api/v3/depth</c>, <c>/api/v3/trades</c>, <c>/api/v3/klines</c>,
/// <c>/api/v3/exchangeInfo</c>.
/// </summary>
public sealed class MexcProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public MexcProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public MexcProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public MexcProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Mexc;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://api.mexc.com");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v3/ticker/24hr",
            Query(("symbol", s.ToCompact())), "ticker", cancellationToken).ConfigureAwait(false);

        return new Ticker(
            Exchange,
            s.ToString(),
            Dec(node["lastPrice"]),
            Dec(node["bidPrice"]),
            Dec(node["askPrice"]),
            Dec(node["highPrice"]),
            Dec(node["lowPrice"]),
            Dec(node["openPrice"]),
            Dec(node["volume"]),
            Dec(node["quoteVolume"]),
            Dec(node["priceChangePercent"]),
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v3/ticker/24hr", null, "ticker", cancellationToken).ConfigureAwait(false);
        var list = new List<Ticker>();

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item?["symbol"]?.GetValue<string>() is not { } symbol)
                {
                    continue;
                }

                list.Add(new Ticker(
                    Exchange,
                    UnifiedSymbol.Parse(symbol).ToString(),
                    Dec(item["lastPrice"]), Dec(item["bidPrice"]), Dec(item["askPrice"]),
                    Dec(item["highPrice"]), Dec(item["lowPrice"]), Dec(item["openPrice"]),
                    Dec(item["volume"]), Dec(item["quoteVolume"]), Dec(item["priceChangePercent"]),
                    DateTimeOffset.UtcNow));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v3/depth",
            Query(("symbol", s.ToCompact()), ("limit", Math.Clamp(depth, 5, 200))), "depth", cancellationToken).ConfigureAwait(false);

        return new OrderBook
        {
            Exchange = Exchange,
            Symbol = s.ToString(),
            Bids = MapLevels(node["bids"] as JsonArray, depth),
            Asks = MapLevels(node["asks"] as JsonArray, depth),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v3/trades",
            Query(("symbol", s.ToCompact()), ("limit", Math.Clamp(limit, 1, 1000))), "trades", cancellationToken).ConfigureAwait(false);

        var trades = new List<Trade>();
        if (node is JsonArray array)
        {
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
                ("limit", Math.Clamp(limit, 1, 1000))),
            "candles", cancellationToken).ConfigureAwait(false);

        var candles = new List<Candle>();
        if (node is JsonArray array)
        {
            foreach (var row in array)
            {
                if (row is not JsonArray k)
                {
                    continue;
                }

                candles.Add(new Candle(
                    FromUnix((long)DecAt(k, 0)),
                    DecAt(k, 1), DecAt(k, 2), DecAt(k, 3), DecAt(k, 4),
                    DecAt(k, 5), DecAt(k, 7)));
            }
        }

        return candles;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v3/exchangeInfo", null, "info", cancellationToken).ConfigureAwait(false);

        var markets = new List<MarketInfo>();
        if (node["symbols"] is not JsonArray array)
        {
            return markets;
        }

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
                BaseAsset = item["baseAsset"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                QuoteAsset = item["quoteAsset"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                QuantityPrecision = item["baseAssetPrecision"]?.GetValue<int>() ?? 8,
                PricePrecision = item["quoteAssetPrecision"]?.GetValue<int>() ?? 8,
                Status = item["status"]?.GetValue<string>() == "1" || item["status"]?.GetValue<string>() == "TRADING"
                    ? MarketStatus.Active
                    : MarketStatus.Halted,
            });
        }

        return markets;
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