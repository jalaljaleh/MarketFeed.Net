using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>Bitstamp</b> (https://www.bitstamp.net/api/v2).
/// Endpoints: <c>/api/v2/ticker/{pair}/</c>, <c>/order_book/{pair}/</c>, <c>/transactions/{pair}/</c>,
/// <c>/ohlc/{pair}/</c> and <c>/trading-pairs-info/</c>. Pairs are quoted in USD/EUR.
/// </summary>
public sealed class BitstampProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public BitstampProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public BitstampProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public BitstampProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Bitstamp;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://www.bitstamp.net");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync($"/api/v2/ticker/{s.ToBitstamp()}/", null, "ticker", cancellationToken).ConfigureAwait(false);

        var last = Dec(node["last"]);
        var open = Dec(node["open"]);

        return new Ticker(
            Exchange,
            s.ToString(),
            last,
            Dec(node["bid"]),
            Dec(node["ask"]),
            Dec(node["high"]),
            Dec(node["low"]),
            open,
            Dec(node["volume"]),
            last * Dec(node["volume"]),
            open == 0 ? 0m : (last - open) / open * 100m,
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        var markets = await GetMarketsAsync(cancellationToken).ConfigureAwait(false);
        var tickers = new List<Ticker>();

        foreach (var market in markets.Take(25))
        {
            try
            {
                tickers.Add(await GetTickerAsync(market.Symbol, cancellationToken).ConfigureAwait(false));
            }
            catch (MarketDataException)
            {
                // Ignore pairs that are currently unavailable.
            }
        }

        return tickers;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync($"/api/v2/order_book/{s.ToBitstamp()}/", null, "depth", cancellationToken).ConfigureAwait(false);

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

        var node = await GetJsonAsync($"/api/v2/transactions/{s.ToBitstamp()}/",
            Query(("time", "hour")), "trades", cancellationToken).ConfigureAwait(false);

        var trades = new List<Trade>();
        if (node is JsonArray array)
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
                    Dec(item["amount"]),
                    item["type"]?.GetValue<string>() == "1" ? TradeSide.Buy : TradeSide.Sell,
                    FromUnix((long)Dec(item["date"])),
                    item["tid"]?.GetValue<long>()));
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

        var node = await GetJsonAsync($"/api/v2/ohlc/{s.ToBitstamp()}/",
            Query(
                ("step", timeframe.ToBitstampStep()),
                ("limit", Math.Clamp(limit, 1, 1000)),
                ("start", from?.ToUnixTimeSeconds()),
                ("end", to?.ToUnixTimeSeconds())),
            "candles", cancellationToken).ConfigureAwait(false);

        var candles = new List<Candle>();
        if (node["data"]?["ohlc"] is JsonArray array)
        {
            foreach (var row in array)
            {
                if (row is not JsonArray k)
                {
                    continue;
                }

                // [timestamp, open, high, low, close, volume]
                candles.Add(new Candle(
                    FromUnix((long)DecAt(k, 0)),
                    DecAt(k, 1), DecAt(k, 2), DecAt(k, 3), DecAt(k, 4),
                    DecAt(k, 5)));
            }
        }

        return candles;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v2/trading-pairs-info/", null, "info", cancellationToken).ConfigureAwait(false);

        var markets = new List<MarketInfo>();
        if (node is not JsonArray array)
        {
            return markets;
        }

        foreach (var item in array)
        {
            if (item?["name"]?.GetValue<string>() is not { } name)
            {
                continue;
            }

            // "BTC/USD" -> BASE / QUOTE
            var parts = name.Split('/', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            markets.Add(new MarketInfo
            {
                Exchange = Exchange,
                Symbol = UnifiedSymbol.Parse(name).ToString(),
                BaseAsset = parts[0].Trim().ToUpperInvariant(),
                QuoteAsset = parts[1].Trim().ToUpperInvariant(),
                PricePrecision = item["currency_decimal"]?.GetValue<int>() ?? 2,
                QuantityPrecision = item["base_decimal"]?.GetValue<int>() ?? 8,
                MinNotional = Dec(item["minimum_order"]),
                Status = MarketStatus.Active,
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