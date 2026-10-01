using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>Bitfinex</b> (https://api-pub.bitfinex.com/api/v2).
/// Endpoints: <c>/ticker/tBTCUSD</c>, <c>/book/…</c>, <c>/trades/…</c>, <c>/candles/…</c>.
/// Bitfinex responses are JSON <b>arrays</b>; this provider maps them into the unified models.
/// </summary>
public sealed class BitfinexProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public BitfinexProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public BitfinexProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public BitfinexProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Bitfinex;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://api-pub.bitfinex.com");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync($"/api/v2/ticker/{s.ToBitfinex()}", null, "ticker", cancellationToken).ConfigureAwait(false);
        if (node is not JsonArray t)
        {
            throw new MarketDataException(Exchange, $"No ticker for {s}.");
        }

        // [BID, BID_SIZE, ASK, ASK_SIZE, DAILY_CHANGE, DAILY_CHANGE_RELATIVE, LAST_PRICE, VOLUME, HIGH, LOW]
        var last = DecAt(t, 6);
        var open = last - DecAt(t, 4);

        return new Ticker(
            Exchange,
            s.ToString(),
            last,
            DecAt(t, 0),
            DecAt(t, 2),
            DecAt(t, 8),
            DecAt(t, 9),
            open,
            DecAt(t, 7),
            DecAt(t, 7) * last,
            DecAt(t, 5) * 100m,
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v2/conf/pub:list:pair:exchange", null, "info", cancellationToken).ConfigureAwait(false);

        var tickers = new List<Ticker>();
        if (node is JsonArray pairs)
        {
            foreach (var pair in pairs.Take(25))
            {
                if (pair?.GetValue<string>() is not { } name)
                {
                    continue;
                }

                try
                {
                    tickers.Add(await GetTickerAsync(UnifiedSymbol.Parse(name).ToString(), cancellationToken).ConfigureAwait(false));
                }
                catch (MarketDataException)
                {
                    // Skip pairs that Bitfinex does not quote right now.
                }
            }
        }

        return tickers;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync($"/api/v2/book/{s.ToBitfinex()}/P0",
            Query(("len", Math.Clamp(depth, 1, 250))), "depth", cancellationToken).ConfigureAwait(false);

        var bids = new List<OrderBookLevel>();
        var asks = new List<OrderBookLevel>();

        if (node is JsonArray rows)
        {
            foreach (var row in rows)
            {
                if (row is not JsonArray level)
                {
                    continue;
                }

                // [PRICE, COUNT, AMOUNT] — positive amount = bid, negative amount = ask
                var price = DecAt(level, 0);
                var amount = DecAt(level, 2);

                if (amount > 0)
                {
                    bids.Add(new OrderBookLevel(price, amount));
                }
                else
                {
                    asks.Add(new OrderBookLevel(price, Math.Abs(amount)));
                }
            }
        }

        return new OrderBook
        {
            Exchange = Exchange,
            Symbol = s.ToString(),
            Bids = bids.OrderByDescending(b => b.Price).Take(depth).ToList(),
            Asks = asks.OrderBy(a => a.Price).Take(depth).ToList(),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync($"/api/v2/trades/{s.ToBitfinex()}/hist",
            Query(("limit", Math.Clamp(limit, 1, 1000))), "trades", cancellationToken).ConfigureAwait(false);

        var trades = new List<Trade>();
        if (node is JsonArray array)
        {
            foreach (var row in array)
            {
                if (row is not JsonArray k)
                {
                    continue;
                }

                // [ID, MTS, AMOUNT, PRICE] — negative amount means a sell
                var amount = DecAt(k, 2);
                trades.Add(new Trade(
                    Exchange,
                    s.ToString(),
                    DecAt(k, 3),
                    Math.Abs(amount),
                    amount < 0 ? TradeSide.Sell : TradeSide.Buy,
                    FromUnix((long)DecAt(k, 1)),
                    (long)DecAt(k, 0)));
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
        var key = $"trade:{timeframe.ToBitfinexKey()}:{s.ToBitfinex()}";

        var node = await GetJsonAsync($"/api/v2/candles/{key}/hist",
            Query(
                ("limit", Math.Clamp(limit, 1, 5000)),
                ("start", from?.ToUnixTimeMilliseconds()),
                ("end", to?.ToUnixTimeMilliseconds())),
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

                // [MTS, OPEN, HIGH, LOW, CLOSE, VOLUME]
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
        var node = await GetJsonAsync("/api/v2/conf/pub:list:pair:exchange", null, "info", cancellationToken).ConfigureAwait(false);

        var markets = new List<MarketInfo>();
        if (node is not JsonArray pairs)
        {
            return markets;
        }

        foreach (var pair in pairs)
        {
            if (pair?.GetValue<string>() is not { } name)
            {
                continue;
            }

            try
            {
                var unified = UnifiedSymbol.Parse(name);
                markets.Add(new MarketInfo
                {
                    Exchange = Exchange,
                    Symbol = unified.ToString(),
                    BaseAsset = unified.Base,
                    QuoteAsset = unified.Quote,
                    PricePrecision = 8,
                    QuantityPrecision = 8,
                    Status = MarketStatus.Active,
                });
            }
            catch (FormatException)
            {
                // Bitfinex lists derivative symbols (tBTCF0:USTF0) that cannot be unified; skip them.
            }
        }

        return markets;
    }
}