using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>Nobitex</b> (https://apiv2.nobitex.ir) — the Iranian market quoted in IRR (Toman).
/// Endpoints: <c>/market/stats</c>, <c>/v3/orderbook/{SYMBOL}</c>, <c>/v2/trades/{SYMBOL}</c>,
/// <c>/market/udf/history</c>. No authentication is required for these endpoints.
/// </summary>
/// <remarks>
/// Quote currency is <c>IRT</c> (Rial). Prices are integers, e.g. <c>USDT/IRT</c> ≈ 1,100,000.
/// This provider makes the library usable together with the <c>Nobitex.Net</c> trading library.
/// </remarks>
public sealed class NobitexProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public NobitexProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public NobitexProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public NobitexProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Nobitex;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://apiv2.nobitex.ir");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/market/stats",
            Query(("srcCurrency", s.Base.ToLowerInvariant()), ("dstCurrency", s.Quote.ToLowerInvariant())),
            "ticker", cancellationToken).ConfigureAwait(false);

        var key = $"{s.Base.ToLowerInvariant()}-{s.Quote.ToLowerInvariant()}";
        var item = node["stats"]?[key] ?? throw new MarketDataException(Exchange, $"No ticker for {s}.");

        var bestBuy = Dec(item["bestBuy"]);
        var bestSell = Dec(item["bestSell"]);
        var open = Dec(item["dayOpen"]);

        return new Ticker(
            Exchange,
            s.ToString(),
            Dec(item["latest"]),
            bestBuy,
            bestSell,
            Dec(item["dayHigh"]),
            Dec(item["dayLow"]),
            open,
            Dec(item["volumeSrc"]),
            Dec(item["volumeDst"]),
            Dec(item["dayChange"]),
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        // One call returns the statistics of every Nobitex market.
        var node = await GetJsonAsync("/market/stats", null, "ticker", cancellationToken).ConfigureAwait(false);
        var list = new List<Ticker>();

        if (node["stats"] is JsonObject stats)
        {
            foreach (var property in stats)
            {
                var parts = property.Key.Split('-', 2);
                if (parts.Length != 2 || property.Value is not JsonObject item)
                {
                    continue;
                }

                var bestBuy = Dec(item["bestBuy"]);
                var bestSell = Dec(item["bestSell"]);

                list.Add(new Ticker(
                    Exchange,
                    $"{parts[0].ToUpperInvariant()}/{parts[1].ToUpperInvariant()}",
                    Dec(item["latest"]),
                    bestBuy,
                    bestSell,
                    Dec(item["dayHigh"]),
                    Dec(item["dayLow"]),
                    Dec(item["dayOpen"]),
                    Dec(item["volumeSrc"]),
                    Dec(item["volumeDst"]),
                    Dec(item["dayChange"]),
                    DateTimeOffset.UtcNow));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync($"/v3/orderbook/{NobitexSymbol(s)}", null, "depth", cancellationToken).ConfigureAwait(false);

        return new OrderBook
        {
            Exchange = Exchange,
            Symbol = s.ToString(),
            Bids = MapLevels(node["bids"] as JsonArray, depth),
            Asks = MapLevels(node["asks"] as JsonArray, depth),
            Timestamp = FromUnix((long)Dec(node["lastUpdate"])),
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync($"/v2/trades/{NobitexSymbol(s)}", null, "trades", cancellationToken).ConfigureAwait(false);

        var trades = new List<Trade>();
        if (node["trades"] is JsonArray array)
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
                    Dec(item["volume"]),
                    item["type"]?.GetValue<string>() == "sell" ? TradeSide.Sell : TradeSide.Buy,
                    FromUnix((long)Dec(item["time"]))));
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
        var toSeconds = to?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fromSeconds = from?.ToUnixTimeSeconds() ?? toSeconds - (long)(timeframe.ToTimeSpan().TotalSeconds * limit);

        var node = await GetJsonAsync("/market/udf/history",
            Query(
                ("symbol", NobitexSymbol(s)),
                ("resolution", timeframe.ToNobitexResolution()),
                ("from", fromSeconds),
                ("to", toSeconds)),
            "candles", cancellationToken).ConfigureAwait(false);

        // TradingView UDF arrays: t / o / h / l / c / v
        var times = node["t"] as JsonArray;
        var candles = new List<Candle>();

        if (times is not null)
        {
            for (var i = 0; i < times.Count && i < limit; i++)
            {
                candles.Add(new Candle(
                    FromUnix((long)DecAt(times, i)),
                    DecAt(node["o"] as JsonArray, i),
                    DecAt(node["h"] as JsonArray, i),
                    DecAt(node["l"] as JsonArray, i),
                    DecAt(node["c"] as JsonArray, i),
                    DecAt(node["v"] as JsonArray, i)));
            }
        }

        return candles;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
    {
        var tickers = await GetTickersAsync(cancellationToken).ConfigureAwait(false);

        return tickers.Select(t =>
        {
            var parts = t.Symbol.Split('/', 2);
            return new MarketInfo
            {
                Exchange = Exchange,
                Symbol = t.Symbol,
                BaseAsset = parts[0],
                QuoteAsset = parts.Length > 1 ? parts[1] : string.Empty,
                PricePrecision = parts.Length > 1 && parts[1] == "IRT" ? 0 : 2,
                QuantityPrecision = 8,
                Status = MarketStatus.Active,
            };
        }).ToList();
    }

    /// <summary>Nobitex uses compact symbols such as <c>BTCIRT</c> / <c>ETHUSDT</c>.</summary>
    private static string NobitexSymbol(UnifiedSymbol symbol)
        => symbol.Base + (symbol.Quote == "IRT" ? "IRT" : symbol.Quote);

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