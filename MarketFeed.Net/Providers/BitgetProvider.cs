using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>Bitget</b> (https://api.bitget.com, API v2 spot).
/// Endpoints: <c>/api/v2/spot/market/tickers</c>, <c>orderbook</c>, <c>trades</c>, <c>candles</c>
/// and <c>/api/v2/spot/public/symbols</c>.
/// </summary>
public sealed class BitgetProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public BitgetProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public BitgetProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public BitgetProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Bitget;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://api.bitget.com");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v2/spot/market/tickers",
            Query(("symbol", s.ToCompact())), "ticker", cancellationToken).ConfigureAwait(false);

        var item = node["data"]?[0] ?? throw new MarketDataException(Exchange, $"No ticker for {s}.");

        return new Ticker(
            Exchange,
            s.ToString(),
            Dec(item["lastPr"]),
            Dec(item["buyOne"]),
            Dec(item["sellOne"]),
            Dec(item["high24h"]),
            Dec(item["low24h"]),
            Dec(item["openPr"]),
            Dec(item["baseVolume"]),
            Dec(item["quoteVolume"]),
            Dec(item["change24h"]) * 100m,
            FromUnix((long)Dec(item["ts"])));
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v2/spot/market/tickers", null, "ticker", cancellationToken).ConfigureAwait(false);
        var list = new List<Ticker>();

        if (node["data"] is JsonArray array)
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
                    Dec(item["lastPr"]), Dec(item["buyOne"]), Dec(item["sellOne"]),
                    Dec(item["high24h"]), Dec(item["low24h"]), Dec(item["openPr"]),
                    Dec(item["baseVolume"]), Dec(item["quoteVolume"]),
                    Dec(item["change24h"]) * 100m,
                    FromUnix((long)Dec(item["ts"]))));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v2/spot/market/orderbook",
            Query(("symbol", s.ToCompact()), ("limit", Math.Clamp(depth, 1, 150))), "depth", cancellationToken).ConfigureAwait(false);

        var data = node["data"]?[0] ?? node["data"];

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

        var node = await GetJsonAsync("/api/v2/spot/market/trades",
            Query(("symbol", s.ToCompact()), ("limit", Math.Clamp(limit, 1, 100))), "trades", cancellationToken).ConfigureAwait(false);

        var trades = new List<Trade>();
        if (node["data"] is JsonArray array)
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
                    Dec(item["size"]),
                    item["side"]?.GetValue<string>() == "sell" ? TradeSide.Sell : TradeSide.Buy,
                    FromUnix((long)Dec(item["ts"])),
                    item["tradeId"]?.GetValue<string>() is { } id && long.TryParse(id, out var parsed) ? parsed : null));
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

        var node = await GetJsonAsync("/api/v2/spot/market/candles",
            Query(
                ("symbol", s.ToCompact()),
                ("granularity", timeframe.ToKucoinType()),
                ("startTime", from?.ToUnixTimeMilliseconds()),
                ("endTime", to?.ToUnixTimeMilliseconds()),
                ("limit", Math.Clamp(limit, 1, 200))),
            "candles", cancellationToken).ConfigureAwait(false);

        var candles = new List<Candle>();
        if (node["data"] is JsonArray array)
        {
            foreach (var row in array)
            {
                if (row is not JsonArray k)
                {
                    continue;
                }

                // [ts, open, high, low, close, baseVol, quoteVol]
                candles.Add(new Candle(
                    FromUnix((long)DecAt(k, 0)),
                    DecAt(k, 1), DecAt(k, 2), DecAt(k, 3), DecAt(k, 4),
                    DecAt(k, 5), DecAt(k, 6)));
            }
        }

        return candles;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v2/spot/public/symbols", null, "info", cancellationToken).ConfigureAwait(false);

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
                    BaseAsset = item["baseCoin"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                    QuoteAsset = item["quoteCoin"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                    MinQuantity = Dec(item["minTradeAmount"]),
                    PricePrecision = item["pricePrecision"]?.GetValue<int>() ?? 8,
                    QuantityPrecision = item["quantityPrecision"]?.GetValue<int>() ?? 8,
                    Status = item["status"]?.GetValue<string>() == "online" ? MarketStatus.Active : MarketStatus.Halted,
                });
            }
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