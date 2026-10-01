using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>Gate.io</b> (https://api.gateio.ws/api/v4).
/// Endpoints: <c>/spot/tickers</c>, <c>/spot/order_book</c>, <c>/spot/trades</c>,
/// <c>/spot/candlesticks</c> and <c>/spot/currency_pairs</c>.
/// </summary>
public sealed class GateIoProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public GateIoProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public GateIoProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public GateIoProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.GateIo;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://api.gateio.ws");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v4/spot/tickers",
            Query(("currency_pair", s.ToGateIo())), "ticker", cancellationToken).ConfigureAwait(false);

        var item = (node is JsonArray { Count: > 0 } array ? array[0] : null)
            ?? throw new MarketDataException(Exchange, $"No ticker for {s}.");

        var last = Dec(item["last"]);

        return new Ticker(
            Exchange,
            s.ToString(),
            last,
            Dec(item["highest_bid"]),
            Dec(item["lowest_ask"]),
            Dec(item["high_24h"]),
            Dec(item["low_24h"]),
            Dec(item["last"]) / (1 + Dec(item["change_percentage"]) / 100m),
            Dec(item["base_volume"]),
            Dec(item["quote_volume"]),
            Dec(item["change_percentage"]),
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v4/spot/tickers", null, "ticker", cancellationToken).ConfigureAwait(false);
        var list = new List<Ticker>();

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item?["currency_pair"]?.GetValue<string>() is not { } pair)
                {
                    continue;
                }

                var last = Dec(item["last"]);
                list.Add(new Ticker(
                    Exchange,
                    UnifiedSymbol.Parse(pair).ToString(),
                    last,
                    Dec(item["highest_bid"]),
                    Dec(item["lowest_ask"]),
                    Dec(item["high_24h"]),
                    Dec(item["low_24h"]),
                    last / (1 + Dec(item["change_percentage"]) / 100m),
                    Dec(item["base_volume"]),
                    Dec(item["quote_volume"]),
                    Dec(item["change_percentage"]),
                    DateTimeOffset.UtcNow));
            }
        }

        return list;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v4/spot/order_book",
            Query(("currency_pair", s.ToGateIo()), ("limit", Math.Clamp(depth, 1, 100))), "depth", cancellationToken).ConfigureAwait(false);

        return new OrderBook
        {
            Exchange = Exchange,
            Symbol = s.ToString(),
            Bids = MapLevels(node["bids"] as JsonArray, depth),
            Asks = MapLevels(node["asks"] as JsonArray, depth),
            Timestamp = FromUnix((long)Dec(node["update"])),
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/api/v4/spot/trades",
            Query(("currency_pair", s.ToGateIo()), ("limit", Math.Clamp(limit, 1, 100))), "trades", cancellationToken).ConfigureAwait(false);

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
                    Dec(item["amount"]),
                    item["side"]?.GetValue<string>() == "sell" ? TradeSide.Sell : TradeSide.Buy,
                    FromUnix((long)Dec(item["create_time"])),
                    item["id"]?.GetValue<long>()));
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

        var node = await GetJsonAsync("/api/v4/spot/candlesticks",
            Query(
                ("currency_pair", s.ToGateIo()),
                ("interval", timeframe.ToShortString()),
                ("from", from?.ToUnixTimeSeconds()),
                ("to", to?.ToUnixTimeSeconds())),
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

                // [unix_ts, quote_volume, close, high, low, open, base_volume, window_closed]
                candles.Add(new Candle(
                    FromUnix((long)DecAt(k, 0)),
                    DecAt(k, 5), DecAt(k, 3), DecAt(k, 4), DecAt(k, 2),
                    DecAt(k, 6), DecAt(k, 1)));
            }
        }

        return candles;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v4/spot/currency_pairs", null, "info", cancellationToken).ConfigureAwait(false);

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
                BaseAsset = item["base"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                QuoteAsset = item["quote"]?.GetValue<string>()?.ToUpperInvariant() ?? string.Empty,
                PricePrecision = item["precision"]?.GetValue<int>() ?? 8,
                MinQuantity = Dec(item["min_base_amount"]),
                MinNotional = Dec(item["min_quote_amount"]),
                Status = item["trade_status"]?.GetValue<string>() == "tradable" ? MarketStatus.Active : MarketStatus.Halted,
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