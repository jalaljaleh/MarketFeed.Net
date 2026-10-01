using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>OKX</b> (https://www.okx.com).
/// Endpoints: <c>/api/v5/market/ticker</c>, <c>tickers</c>, <c>books</c>, <c>trades</c>,
/// <c>candles</c> and <c>/api/v5/public/instruments</c>.
/// </summary>
/// <remarks>Rate limit: about 20 requests per 2 seconds per endpoint.</remarks>
public sealed class OkxProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public OkxProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public OkxProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public OkxProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Okx;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://www.okx.com");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v5/market/ticker",
            Query(("instId", s.ToDash())), "ticker", cancellationToken).ConfigureAwait(false);

        var item = node["data"]?[0] ?? throw new MarketDataException(Exchange, $"No ticker for {s}.");

        return MapTicker(item, s.ToString());
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v5/market/tickers",
            Query(("instType", "SPOT")), "ticker", cancellationToken).ConfigureAwait(false);

        var list = new List<Ticker>();
        if (node["data"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is null)
                {
                    continue;
                }

                var instId = item["instId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(instId))
                {
                    continue;
                }

                list.Add(MapTicker(item, UnifiedSymbol.Parse(instId).ToString()));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);
        var size = Math.Clamp(depth, 1, 400);

        var node = await GetJsonAsync("/api/v5/market/books",
            Query(("instId", s.ToDash()), ("sz", size)), "depth", cancellationToken).ConfigureAwait(false);

        var item = node["data"]?[0];

        return new OrderBook
        {
            Exchange = Exchange,
            Symbol = s.ToString(),
            Bids = MapLevels(item?["bids"] as JsonArray),
            Asks = MapLevels(item?["asks"] as JsonArray),
            Timestamp = FromUnix((long)Dec(item?["ts"])),
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v5/market/trades",
            Query(("instId", s.ToDash()), ("limit", Math.Clamp(limit, 1, 500))), "trades", cancellationToken).ConfigureAwait(false);

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
                    Dec(item["px"]),
                    Dec(item["sz"]),
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

        var node = await GetJsonAsync("/api/v5/market/candles",
            Query(
                ("instId", s.ToDash()),
                ("bar", timeframe.ToOkxBar()),
                ("after", from?.ToUnixTimeMilliseconds()),
                ("before", to?.ToUnixTimeMilliseconds()),
                ("limit", Math.Clamp(limit, 1, 300))),
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

                // [ts, o, h, l, c, vol, volCcy, volCcyQuote, confirm]
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
        var node = await GetJsonAsync("/api/v5/public/instruments",
            Query(("instType", "SPOT")), "info", cancellationToken).ConfigureAwait(false);

        var markets = new List<MarketInfo>();
        if (node["data"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is null)
                {
                    continue;
                }

                markets.Add(new MarketInfo
                {
                    Exchange = Exchange,
                    Symbol = UnifiedSymbol.Parse(item["instId"]?.GetValue<string>() ?? string.Empty).ToString(),
                    BaseAsset = item["baseCcy"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                    QuoteAsset = item["quoteCcy"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                    PricePrecision = PrecisionFromStep(item["tickSz"]),
                    QuantityPrecision = PrecisionFromStep(item["lotSz"]),
                    MinQuantity = Dec(item["minSz"]),
                    Status = item["state"]?.GetValue<string>() == "live" ? MarketStatus.Active : MarketStatus.Halted,
                });
            }
        }

        return markets;
    }

    private Ticker MapTicker(JsonNode item, string unified) => new(
        Exchange,
        unified,
        Dec(item["last"]),
        Dec(item["bidPx"]),
        Dec(item["askPx"]),
        Dec(item["high24h"]),
        Dec(item["low24h"]),
        Dec(item["open24h"]),
        Dec(item["vol24h"]),
        Dec(item["volCcy24h"]),
        ComputeChange(Dec(item["open24h"]), Dec(item["last"])),
        FromUnix((long)Dec(item["ts"])));

    private static decimal ComputeChange(decimal open, decimal last)
        => open == 0 ? 0m : (last - open) / open * 100m;

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