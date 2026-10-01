using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>Bybit</b> (https://api.bybit.com, API v5).
/// Endpoints: <c>/v5/market/tickers</c>, <c>/v5/market/orderbook</c>, <c>/v5/market/recent-trade</c>,
/// <c>/v5/market/kline</c> and <c>/v5/market/instruments-info</c> (category = spot).
/// </summary>
/// <remarks>Rate limit: 10 requests per second for public endpoints.</remarks>
public sealed class BybitProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public BybitProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public BybitProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public BybitProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Bybit;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://api.bybit.com");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/v5/market/tickers",
            Query(("category", "spot"), ("symbol", s.ToCompact())), "ticker", cancellationToken).ConfigureAwait(false);

        var item = node["result"]?["list"]?[0] ?? throw new MarketDataException(Exchange, $"No ticker for {s}.");

        return MapTicker(item, s.ToString());
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/v5/market/tickers",
            Query(("category", "spot")), "ticker", cancellationToken).ConfigureAwait(false);

        var list = new List<Ticker>();
        if (node["result"]?["list"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item?["symbol"]?.GetValue<string>() is { } symbol)
                {
                    list.Add(MapTicker(item, UnifiedSymbol.Parse(symbol).ToString()));
                }
            }
        }

        return list;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);
        // Bybit spot supports 1..50 levels.
        var limit = Math.Clamp(depth, 1, 50);

        var node = await GetJsonAsync("/v5/market/orderbook",
            Query(("category", "spot"), ("symbol", s.ToCompact()), ("limit", limit)), "depth", cancellationToken).ConfigureAwait(false);

        var result = node["result"];

        return new OrderBook
        {
            Exchange = Exchange,
            Symbol = s.ToString(),
            Bids = MapLevels(result?["b"] as JsonArray),
            Asks = MapLevels(result?["a"] as JsonArray),
            Timestamp = FromUnix((long)Dec(result?["ts"])),
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/v5/market/recent-trade",
            Query(("category", "spot"), ("symbol", s.ToCompact()), ("limit", Math.Clamp(limit, 1, 60))), "trades", cancellationToken).ConfigureAwait(false);

        var trades = new List<Trade>();
        if (node["result"]?["list"] is JsonArray array)
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
                    item["side"]?.GetValue<string>() == "Sell" ? TradeSide.Sell : TradeSide.Buy,
                    FromUnix((long)Dec(item["time"])),
                    item["execId"]?.GetValue<string>() is { } id && long.TryParse(id, out var parsed) ? parsed : null));
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

        var node = await GetJsonAsync("/v5/market/kline",
            Query(
                ("category", "spot"),
                ("symbol", s.ToCompact()),
                ("interval", timeframe.ToBybitInterval()),
                ("start", from?.ToUnixTimeMilliseconds()),
                ("end", to?.ToUnixTimeMilliseconds()),
                ("limit", Math.Clamp(limit, 1, 1000))),
            "candles", cancellationToken).ConfigureAwait(false);

        var candles = new List<Candle>();
        if (node["result"]?["list"] is JsonArray array)
        {
            foreach (var row in array)
            {
                if (row is not JsonArray k)
                {
                    continue;
                }

                // Bybit returns the newest first: [startTime, open, high, low, close, volume, turnover]
                candles.Add(new Candle(
                    FromUnix((long)DecAt(k, 0)),
                    DecAt(k, 1), DecAt(k, 2), DecAt(k, 3), DecAt(k, 4),
                    DecAt(k, 5), DecAt(k, 6)));
            }

            candles.Reverse();
        }

        return candles;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/v5/market/instruments-info",
            Query(("category", "spot"), ("limit", 1000)), "info", cancellationToken).ConfigureAwait(false);

        var markets = new List<MarketInfo>();
        if (node["result"]?["list"] is JsonArray array)
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
                    QuantityPrecision = PrecisionFromStep(item["lotSizeFilter"]?["basePrecision"]),
                    MinQuantity = Dec(item["lotSizeFilter"]?["minOrderQty"]),
                    MinNotional = Dec(item["lotSizeFilter"]?["minNotional"]),
                    Status = item["status"]?.GetValue<string>() == "Trading" ? MarketStatus.Active : MarketStatus.Halted,
                });
            }
        }

        return markets;
    }

    private Ticker MapTicker(JsonNode item, string unified) => new(
        Exchange,
        unified,
        Dec(item["lastPrice"]),
        Dec(item["bid1Price"]),
        Dec(item["ask1Price"]),
        Dec(item["highPrice24h"]),
        Dec(item["lowPrice24h"]),
        Dec(item["prevPrice24h"]),
        Dec(item["volume24h"]),
        Dec(item["turnover24h"]),
        Dec(item["price24hPcnt"]) * 100m,   // Bybit reports a ratio (0.0241 = 2.41%)
        DateTimeOffset.UtcNow);

    private static int PrecisionFromStep(JsonNode? step)
    {
        var value = step?.GetValue<string>() ?? "0.00000001";
        var dot = value.IndexOf('.');
        return dot < 0 ? 0 : value.Length - dot - 1;
    }

    private static IReadOnlyList<OrderBookLevel> MapLevels(JsonArray? array)
    {
        var levels = new List<OrderBookLevel>();
        if (array is null)
        {
            return levels;
        }

        foreach (var level in array)
        {
            if (level is JsonArray row)
            {
                levels.Add(new OrderBookLevel(DecAt(row, 0), DecAt(row, 1)));
            }
        }

        return levels;
    }
}