using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>Coinbase Exchange</b> (https://api.exchange.coinbase.com).
/// Endpoints: <c>/products/{id}/ticker</c>, <c>/stats</c>, <c>/book</c>, <c>/trades</c>,
/// <c>/candles</c> and <c>/products</c>.
/// </summary>
/// <remarks>Rate limit: 10 requests per second for public endpoints.</remarks>
public sealed class CoinbaseProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public CoinbaseProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public CoinbaseProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public CoinbaseProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Coinbase;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://api.exchange.coinbase.com");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);
        var productId = s.ToCoinbase();

        var ticker = await GetJsonAsync($"/products/{productId}/ticker", null, "ticker", cancellationToken).ConfigureAwait(false);
        var stats = await GetJsonAsync($"/products/{productId}/stats", null, "ticker", cancellationToken).ConfigureAwait(false);

        var open = Dec(stats["open"]);
        var last = Dec(ticker["price"]);

        return new Ticker(
            Exchange,
            s.ToString(),
            last,
            Dec(ticker["bid"]),
            Dec(ticker["ask"]),
            Dec(stats["high"]),
            Dec(stats["low"]),
            open,
            Dec(stats["volume"]),
            Dec(stats["volume"]) * last,
            open == 0 ? 0m : (last - open) / open * 100m,
            ticker["time"]?.GetValue<string>() is { } time && DateTimeOffset.TryParse(time, out var ts) ? ts : DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        var products = await GetMarketsAsync(cancellationToken).ConfigureAwait(false);
        var tickers = new List<Ticker>();

        foreach (var market in products.Take(30))
        {
            try
            {
                tickers.Add(await GetTickerAsync(market.Symbol, cancellationToken).ConfigureAwait(false));
            }
            catch (MarketDataException)
            {
                // Skip products that are not currently tradable.
            }
        }

        return tickers;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);
        // Coinbase offers level 1 (top of book), 2 (aggregated) and 3 (full).
        var level = depth <= 5 ? 1 : 2;

        var node = await GetJsonAsync($"/products/{s.ToCoinbase()}/book",
            Query(("level", level)), "depth", cancellationToken).ConfigureAwait(false);

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

        var node = await GetJsonAsync($"/products/{s.ToCoinbase()}/trades",
            Query(("limit", Math.Clamp(limit, 1, 100))), "trades", cancellationToken).ConfigureAwait(false);

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
                    Dec(item["size"]),
                    item["side"]?.GetValue<string>() == "sell" ? TradeSide.Sell : TradeSide.Buy,
                    item["time"]?.GetValue<string>() is { } time && DateTimeOffset.TryParse(time, out var ts) ? ts : DateTimeOffset.UtcNow,
                    item["trade_id"]?.GetValue<long>()));
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
        var granularity = timeframe.ToCoinbaseGranularity();

        var node = await GetJsonAsync($"/products/{s.ToCoinbase()}/candles",
            Query(
                ("granularity", granularity),
                ("start", from?.UtcDateTime.ToString("o")),
                ("end", to?.UtcDateTime.ToString("o"))),
            "candles", cancellationToken).ConfigureAwait(false);

        var candles = new List<Candle>();
        if (node is JsonArray array)
        {
            foreach (var row in array.Take(limit))
            {
                if (row is not JsonArray k)
                {
                    continue;
                }

                // [time, low, high, open, close, volume]
                candles.Add(new Candle(
                    FromUnix((long)DecAt(k, 0)),
                    DecAt(k, 3), DecAt(k, 2), DecAt(k, 1), DecAt(k, 4),
                    DecAt(k, 5)));
            }

            candles.Reverse();   // Coinbase returns the newest first.
        }

        return candles;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/products", null, "info", cancellationToken).ConfigureAwait(false);

        var markets = new List<MarketInfo>();
        if (node is not JsonArray array)
        {
            return markets;
        }

        foreach (var item in array)
        {
            if (item?["id"]?.GetValue<string>() is not { } id)
            {
                continue;
            }

            markets.Add(new MarketInfo
            {
                Exchange = Exchange,
                Symbol = UnifiedSymbol.Parse(id).ToString(),
                BaseAsset = item["base_currency"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                QuoteAsset = item["quote_currency"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                QuantityPrecision = PrecisionFromIncrement(item["base_increment"]),
                PricePrecision = PrecisionFromIncrement(item["quote_increment"]),
                MinNotional = Dec(item["min_market_funds"]),
                Status = item["status"]?.GetValue<string>() == "online" ? MarketStatus.Active : MarketStatus.Halted,
            });
        }

        return markets;
    }

    private static int PrecisionFromIncrement(JsonNode? increment)
    {
        var value = increment?.GetValue<string>() ?? "0.00000001";
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