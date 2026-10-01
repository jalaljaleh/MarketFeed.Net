using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Public market data of <b>Kraken</b> (https://api.kraken.com).
/// Endpoints: <c>/0/public/Ticker</c>, <c>Depth</c>, <c>Trades</c>, <c>OHLC</c> and <c>AssetPairs</c>.
/// Kraken names Bitcoin <c>XBT</c>; <see cref="UnifiedSymbol"/> handles that mapping automatically.
/// </summary>
/// <remarks>Rate limit: 15–20 calls per second on a decaying counter.</remarks>
public sealed class KrakenProvider : RestProviderBase
{
    /// <summary>Creates the provider with its own handler.</summary>
    public KrakenProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public KrakenProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public KrakenProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.Kraken;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://api.kraken.com");

    /// <inheritdoc />
    public override async Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/0/public/Ticker",
            Query(("pair", s.ToKraken())), "ticker", cancellationToken).ConfigureAwait(false);

        EnsureNoError(node);
        var item = FirstResult(node) ?? throw new MarketDataException(Exchange, $"No ticker for {s}.");

        return new Ticker(
            Exchange,
            s.ToString(),
            DecAt(item["c"] as JsonArray, 0),                 // last trade price
            DecAt(item["b"] as JsonArray, 0),                 // best bid
            DecAt(item["a"] as JsonArray, 0),                 // best ask
            DecAt(item["h"] as JsonArray, 1),                 // 24h high
            DecAt(item["l"] as JsonArray, 1),                 // 24h low
            Dec(item["o"]),                                   // 24h open
            DecAt(item["v"] as JsonArray, 1),                 // 24h volume
            DecAt(item["c"] as JsonArray, 0) * DecAt(item["v"] as JsonArray, 1),
            ComputeChange(Dec(item["o"]), DecAt(item["c"] as JsonArray, 0)),
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
    {
        // Kraken has no "all tickers" endpoint: fetch the pair list and query each pair in batches.
        var pairs = await GetMarketsAsync(cancellationToken).ConfigureAwait(false);
        var tickers = new List<Ticker>();

        foreach (var market in pairs.Take(50))
        {
            try
            {
                tickers.Add(await GetTickerAsync(market.Symbol, cancellationToken).ConfigureAwait(false));
            }
            catch (MarketDataException)
            {
                // Skip pairs that Kraken rejects; the caller still receives the rest.
            }
        }

        return tickers;
    }

    /// <inheritdoc />
    public override async Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/0/public/Depth",
            Query(("pair", s.ToKraken()), ("count", Math.Clamp(depth, 1, 500))), "depth", cancellationToken).ConfigureAwait(false);

        EnsureNoError(node);
        var item = FirstResult(node);

        return new OrderBook
        {
            Exchange = Exchange,
            Symbol = s.ToString(),
            Bids = MapLevels(item?["bids"] as JsonArray),
            Asks = MapLevels(item?["asks"] as JsonArray),
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default)
    {
        var s = RequireSymbol(symbol);

        var node = await GetJsonAsync("/0/public/Trades",
            Query(("pair", s.ToKraken())), "trades", cancellationToken).ConfigureAwait(false);

        EnsureNoError(node);
        var result = node["result"] as JsonObject;
        var trades = new List<Trade>();

        foreach (var property in result ?? new JsonObject())
        {
            if (property.Key == "last" || property.Value is not JsonArray array)
            {
                continue;
            }

            foreach (var row in array.Take(limit))
            {
                if (row is not JsonArray k)
                {
                    continue;
                }

                // [price, volume, time, side(buy/sell), type(market/limit), misc, id]
                trades.Add(new Trade(
                Exchange,
                s.ToString(),
                DecAt(k, 0),
                DecAt(k, 1),
                k.Count > 3 && k[3]?.GetValue<string>() == "s" ? TradeSide.Sell : TradeSide.Buy,
                FromUnix((long)DecAt(k, 2)),
                k.Count > 6 && long.TryParse(k[6]?.GetValue<string>(), out var id) ? id : null));
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

        var node = await GetJsonAsync("/0/public/OHLC",
            Query(
                ("pair", s.ToKraken()),
                ("interval", timeframe.ToKrakenInterval()),
                ("since", from?.ToUnixTimeSeconds())),
            "candles", cancellationToken).ConfigureAwait(false);

        EnsureNoError(node);
        var candles = new List<Candle>();

        foreach (var property in (node["result"] as JsonObject) ?? new JsonObject())
        {
            if (property.Key == "last" || property.Value is not JsonArray array)
            {
                continue;
            }

            foreach (var row in array.Take(limit))
            {
                if (row is not JsonArray k)
                {
                    continue;
                }

                // [time, open, high, low, close, vwap, volume, count]
                candles.Add(new Candle(
                    FromUnix((long)DecAt(k, 0)),
                    DecAt(k, 1), DecAt(k, 2), DecAt(k, 3), DecAt(k, 4),
                    DecAt(k, 6)));
            }
        }

        return candles;
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/0/public/AssetPairs", null, "info", cancellationToken).ConfigureAwait(false);
        EnsureNoError(node);

        var markets = new List<MarketInfo>();
        foreach (var property in (node["result"] as JsonObject) ?? new JsonObject())
        {
            var item = property.Value as JsonObject;
            if (item is null)
            {
                continue;
            }

            var wsName = item["wsname"]?.GetValue<string>();   // e.g. "XBT/USD"
            var unified = wsName is not null ? UnifiedSymbol.Parse(wsName) : default;

            markets.Add(new MarketInfo
            {
                Exchange = Exchange,
                Symbol = wsName ?? property.Key,
                BaseAsset = wsName is null ? item["base"]?.GetValue<string>() ?? string.Empty : unified.Base,
                QuoteAsset = wsName is null ? item["quote"]?.GetValue<string>() ?? string.Empty : unified.Quote,
                PricePrecision = item["pair_decimals"]?.GetValue<int>() ?? 8,
                QuantityPrecision = item["lot_decimals"]?.GetValue<int>() ?? 8,
                Status = item["status"]?.GetValue<string>() == "online" ? MarketStatus.Active : MarketStatus.Halted,
            });
        }

        return markets;
    }

    private static void EnsureNoError(JsonNode node)
    {
        if (node["error"] is JsonArray { Count: > 0 } errors)
        {
            throw new MarketDataException(ExchangeId.Kraken, string.Join("; ", errors.Select(e => e?.ToString())));
        }
    }

    private static JsonNode? FirstResult(JsonNode node)
    {
        if (node["result"] is not JsonObject result)
        {
            return null;
        }

        foreach (var property in result)
        {
            return property.Value;
        }

        return null;
    }

    private static decimal ComputeChange(decimal open, decimal last)
        => open == 0 ? 0m : (last - open) / open * 100m;

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