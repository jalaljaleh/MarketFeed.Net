using System.Text.Json;
using MarketFeed.Net.Core;
using MarketFeed.Net.Models;

namespace MarketFeed.Net.WebSocket;

/// <summary>Kind of a realtime channel.</summary>
public enum StreamChannel
{
    /// <summary>Price ticker updates.</summary>
    Ticker,

    /// <summary>Order book updates.</summary>
    OrderBook,

    /// <summary>Public trade prints.</summary>
    Trades,
}

/// <summary>A single subscription request of the user.</summary>
/// <param name="Channel">Channel kind.</param>
/// <param name="Symbol">Unified symbol.</param>
/// <param name="Depth">Order book depth (only used for <see cref="StreamChannel.OrderBook"/>).</param>
public sealed record StreamSubscription(StreamChannel Channel, UnifiedSymbol Symbol, int Depth = 20);

/// <summary>
/// Callbacks an adapter uses to deliver parsed data to the stream client.
/// </summary>
public sealed class StreamEventSink
{
    /// <summary>Raised when a ticker was parsed.</summary>
    public required Action<Ticker> OnTicker { get; init; }

    /// <summary>Raised when an order book update was parsed.</summary>
    public required Action<OrderBook> OnOrderBook { get; init; }

    /// <summary>Raised when a trade was parsed.</summary>
    public required Action<Trade> OnTrade { get; init; }
}

/// <summary>
/// Translates the wire protocol of one exchange (subscribe frames, ping/pong, payloads)
/// into the unified models of MarketFeed.Net.
/// </summary>
public interface IExchangeStreamAdapter
{
    /// <summary>Exchange this adapter belongs to.</summary>
    ExchangeId Exchange { get; }

    /// <summary>WebSocket endpoint.</summary>
    Uri Endpoint { get; }

    /// <summary>Builds the frame that subscribes to the given channels (or <c>null</c> when the URL carries them).</summary>
    string? BuildSubscribe(IReadOnlyList<StreamSubscription> subscriptions);

    /// <summary>Builds the frame that unsubscribes from the given channels.</summary>
    string? BuildUnsubscribe(IReadOnlyList<StreamSubscription> subscriptions);

    /// <summary>Returns the ping frame to send periodically, or <c>null</c> when the protocol has no ping.</summary>
    string? Ping { get; }

    /// <summary>How often <see cref="Ping"/> should be sent.</summary>
    TimeSpan PingInterval { get; }

    /// <summary>Returns <c>true</c> when the incoming frame is a server ping that needs an answer.</summary>
    bool IsServerPing(JsonElement message, out string pong);

    /// <summary>Parses one incoming frame and forwards every market data item to <paramref name="sink"/>.</summary>
    void Parse(JsonElement message, StreamEventSink sink);
}

/// <summary>Binance combined stream adapter (<c>wss://stream.binance.com:9443/stream?streams=…</c>).</summary>
public sealed class BinanceStreamAdapter : IExchangeStreamAdapter
{
    private readonly List<StreamSubscription> _subscriptions = new();

    /// <inheritdoc />
    public ExchangeId Exchange => ExchangeId.Binance;

    /// <inheritdoc />
    public Uri Endpoint
    {
        get
        {
            var streams = _subscriptions.Select(s => s.Channel switch
            {
                StreamChannel.Ticker => $"{s.Symbol.ToCompact().ToLowerInvariant()}@ticker",
                StreamChannel.OrderBook => $"{s.Symbol.ToCompact().ToLowerInvariant()}@depth{Math.Clamp(s.Depth, 5, 20)}@100ms",
                StreamChannel.Trades => $"{s.Symbol.ToCompact().ToLowerInvariant()}@trade",
                _ => string.Empty,
            }).Where(x => x.Length > 0);

            return new Uri($"wss://stream.binance.com:9443/stream?streams={string.Join('/', streams)}");
        }
    }

    /// <summary>Binance uses stream names in the URL, so no subscribe frame is needed.</summary>
    public string? BuildSubscribe(IReadOnlyList<StreamSubscription> subscriptions)
    {
        _subscriptions.Clear();
        _subscriptions.AddRange(subscriptions);
        return null;
    }

    /// <inheritdoc />
    public string? BuildUnsubscribe(IReadOnlyList<StreamSubscription> subscriptions) => null;

    /// <inheritdoc />
    public string? Ping => null;

    /// <inheritdoc />
    public TimeSpan PingInterval => TimeSpan.FromMinutes(3);

    /// <inheritdoc />
    public bool IsServerPing(JsonElement message, out string pong)
    {
        pong = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public void Parse(JsonElement message, StreamEventSink sink)
    {
        var data = message.TryGetProperty("data", out var d) ? d : message;
        if (data.TryGetProperty("e", out var eventType) == false)
        {
            // Partial depth payload has no event type.
            if (data.TryGetProperty("bids", out var bids))
            {
                sink.OnOrderBook(new OrderBook
                {
                    Exchange = ExchangeId.Binance,
                    Bids = Levels(bids),
                    Asks = Levels(data.GetProperty("asks")),
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            return;
        }

        switch (eventType.GetString())
        {
            case "24hrTicker":
                sink.OnTicker(new Ticker(
                    ExchangeId.Binance,
                    UnifiedSymbol.Parse(data.GetProperty("s").GetString() ?? string.Empty).ToString(),
                    Dec(data, "c"), Dec(data, "b"), Dec(data, "a"),
                    Dec(data, "h"), Dec(data, "l"), Dec(data, "o"),
                    Dec(data, "v"), Dec(data, "q"), Dec(data, "P"),
                    DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("E").GetInt64())));
                break;

            case "trade":
                sink.OnTrade(new Trade(
                    ExchangeId.Binance,
                    UnifiedSymbol.Parse(data.GetProperty("s").GetString() ?? string.Empty).ToString(),
                    Dec(data, "p"), Dec(data, "q"),
                    data.GetProperty("m").GetBoolean() ? TradeSide.Sell : TradeSide.Buy,
                    DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("T").GetInt64()),
                    data.GetProperty("t").GetInt64()));
                break;
        }
    }

    private static decimal Dec(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && decimal.TryParse(v.GetString() ?? v.GetRawText(), out var d) ? d : 0m;

    private static IReadOnlyList<OrderBookLevel> Levels(JsonElement array)
    {
        var levels = new List<OrderBookLevel>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
            {
                levels.Add(new OrderBookLevel(
                    decimal.Parse(item[0].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                    decimal.Parse(item[1].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return levels;
    }
}

/// <summary>OKX v5 public adapter (<c>wss://ws.okx.com:8443/ws/v5/public</c>).</summary>
public sealed class OkxStreamAdapter : IExchangeStreamAdapter
{
    /// <inheritdoc />
    public ExchangeId Exchange => ExchangeId.Okx;

    /// <inheritdoc />
    public Uri Endpoint => new("wss://ws.okx.com:8443/ws/v5/public");

    /// <inheritdoc />
    public string? BuildSubscribe(IReadOnlyList<StreamSubscription> subscriptions)
    {
        var args = subscriptions.Select(s => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["channel"] = s.Channel switch
            {
                StreamChannel.Ticker => "tickers",
                StreamChannel.OrderBook => s.Depth <= 5 ? "books5" : "books",
                StreamChannel.Trades => "trades",
                _ => "tickers",
            },
            ["instId"] = s.Symbol.ToDash(),
        }));

        return $"{{\"op\":\"subscribe\",\"args\":[{string.Join(',', args)}]}}";
    }

    /// <inheritdoc />
    public string? BuildUnsubscribe(IReadOnlyList<StreamSubscription> subscriptions) => BuildSubscribe(subscriptions)?.Replace("subscribe", "unsubscribe");

    /// <inheritdoc />
    public string? Ping => "ping";

    /// <inheritdoc />
    public TimeSpan PingInterval => TimeSpan.FromSeconds(25);

    /// <inheritdoc />
    public bool IsServerPing(JsonElement message, out string pong)
    {
        pong = "pong";
        return message.ValueKind == JsonValueKind.String && message.GetString() == "ping";
    }

    /// <inheritdoc />
    public void Parse(JsonElement message, StreamEventSink sink)
    {
        if (!message.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var channel = message.TryGetProperty("arg", out var arg) && arg.TryGetProperty("channel", out var c)
            ? c.GetString()
            : null;

        foreach (var item in data.EnumerateArray())
        {
            var symbol = UnifiedSymbol.Parse(item.TryGetProperty("instId", out var inst) ? inst.GetString() ?? "" : "").ToString();

            switch (channel)
            {
                case "tickers":
                    sink.OnTicker(new Ticker(
                        ExchangeId.Okx, symbol,
                        Num(item, "last"), Num(item, "bidPx"), Num(item, "askPx"),
                        Num(item, "high24h"), Num(item, "low24h"), Num(item, "open24h"),
                        Num(item, "vol24h"), Num(item, "volCcy24h"),
                        OpenChange(item), DateTimeOffset.UtcNow));
                    break;

                case "trades":
                    sink.OnTrade(new Trade(
                        ExchangeId.Okx, symbol,
                        Num(item, "px"), Num(item, "sz"),
                        item.TryGetProperty("side", out var side) && side.GetString() == "sell" ? TradeSide.Sell : TradeSide.Buy,
                        DateTimeOffset.FromUnixTimeMilliseconds(item.GetProperty("ts").GetInt64())));
                    break;

                case "books":
                case "books5":
                    sink.OnOrderBook(new OrderBook
                    {
                        Exchange = ExchangeId.Okx,
                        Symbol = symbol,
                        Bids = Levels(item.GetProperty("bids")),
                        Asks = Levels(item.GetProperty("asks")),
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                    break;
            }
        }
    }

    private static decimal OpenChange(JsonElement item)
    {
        var open = Num(item, "open24h");
        var last = Num(item, "last");
        return open == 0 ? 0m : (last - open) / open * 100m;
    }

    private static decimal Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && decimal.TryParse(v.GetString(), out var d) ? d : 0m;

    private static IReadOnlyList<OrderBookLevel> Levels(JsonElement array)
    {
        var levels = new List<OrderBookLevel>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
            {
                levels.Add(new OrderBookLevel(
                    decimal.Parse(item[0].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                    decimal.Parse(item[1].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return levels;
    }
}

/// <summary>Bybit v5 public adapter (<c>wss://stream.bybit.com/v5/public/spot</c>).</summary>
public sealed class BybitStreamAdapter : IExchangeStreamAdapter
{
    /// <inheritdoc />
    public ExchangeId Exchange => ExchangeId.Bybit;

    /// <inheritdoc />
    public Uri Endpoint => new("wss://stream.bybit.com/v5/public/spot");

    /// <inheritdoc />
    public string? BuildSubscribe(IReadOnlyList<StreamSubscription> subscriptions)
    {
        var args = subscriptions.Select(s => s.Channel switch
        {
            StreamChannel.Ticker => $"tickers.{s.Symbol.ToCompact()}",
            StreamChannel.OrderBook => $"orderbook.{(s.Depth <= 25 ? 25 : 50)}.{s.Symbol.ToCompact()}",
            StreamChannel.Trades => $"publicTrade.{s.Symbol.ToCompact()}",
            _ => string.Empty,
        }).Where(a => a.Length > 0);

        return $"{{\"op\":\"subscribe\",\"args\":[{string.Join(',', args.Select(a => $"\"{a}\""))}]}}";
    }

    /// <inheritdoc />
    public string? BuildUnsubscribe(IReadOnlyList<StreamSubscription> subscriptions)
        => BuildSubscribe(subscriptions)?.Replace("subscribe", "unsubscribe");

    /// <inheritdoc />
    public string? Ping => "{\"op\":\"ping\"}";

    /// <inheritdoc />
    public TimeSpan PingInterval => TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public bool IsServerPing(JsonElement message, out string pong)
    {
        pong = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public void Parse(JsonElement message, StreamEventSink sink)
    {
        if (!message.TryGetProperty("topic", out var topic))
        {
            return;
        }

        var name = topic.GetString() ?? string.Empty;
        var parts = name.Split('.');
        var data = message.GetProperty("data");

        if (name.StartsWith("tickers.", StringComparison.Ordinal))
        {
            var symbol = UnifiedSymbol.Parse(parts[^1]).ToString();
            sink.OnTicker(new Ticker(
                ExchangeId.Bybit, symbol,
                Num(data, "lastPrice"), Num(data, "bid1Price"), Num(data, "ask1Price"),
                Num(data, "highPrice24h"), Num(data, "lowPrice24h"), Num(data, "prevPrice24h"),
                Num(data, "volume24h"), Num(data, "turnover24h"),
                Num(data, "price24hPcnt") * 100m, DateTimeOffset.UtcNow));
        }
        else if (name.StartsWith("orderbook.", StringComparison.Ordinal))
        {
            sink.OnOrderBook(new OrderBook
            {
                Exchange = ExchangeId.Bybit,
                Symbol = UnifiedSymbol.Parse(data.GetProperty("s").GetString() ?? string.Empty).ToString(),
                Bids = Levels(data.GetProperty("b")),
                Asks = Levels(data.GetProperty("a")),
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
        else if (name.StartsWith("publicTrade.", StringComparison.Ordinal))
        {
            foreach (var item in data.EnumerateArray())
            {
                // Bybit publicTrade item: { T: time, S: Buy/Sell, v: size, p: price, i: id }
                sink.OnTrade(new Trade(
                    ExchangeId.Bybit,
                    UnifiedSymbol.Parse(parts[^1]).ToString(),
                    Num(item, "p"),
                    Num(item, "v"),
                    item.GetProperty("S").GetString() == "Sell" ? TradeSide.Sell : TradeSide.Buy,
                    DateTimeOffset.FromUnixTimeMilliseconds(item.GetProperty("T").GetInt64()),
                    item.TryGetProperty("i", out var id) && long.TryParse(id.GetString(), out var tradeId) ? tradeId : null));
            }
        }
    }

    private static decimal Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && decimal.TryParse(v.GetString(), out var d) ? d : 0m;

    private static IReadOnlyList<OrderBookLevel> Levels(JsonElement array)
    {
        var levels = new List<OrderBookLevel>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
            {
                levels.Add(new OrderBookLevel(
                    decimal.Parse(item[0].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture),
                    decimal.Parse(item[1].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return levels;
    }
}

/// <summary>
/// Generic adapter that only supplies an endpoint and a subscribe frame; useful for exchanges
/// whose payload shape you want to handle yourself through <c>MarketFeedStreamClient.RawMessage</c>.
/// </summary>
public sealed class GenericStreamAdapter : IExchangeStreamAdapter
{
    /// <summary>Creates a generic adapter.</summary>
    public GenericStreamAdapter(ExchangeId exchange, Uri endpoint, Func<IReadOnlyList<StreamSubscription>, string> buildSubscribe)
    {
        Exchange = exchange;
        Endpoint = endpoint;
        _buildSubscribe = buildSubscribe;
    }

    private readonly Func<IReadOnlyList<StreamSubscription>, string> _buildSubscribe;

    /// <inheritdoc />
    public ExchangeId Exchange { get; }

    /// <inheritdoc />
    public Uri Endpoint { get; }

    /// <inheritdoc />
    public string? BuildSubscribe(IReadOnlyList<StreamSubscription> subscriptions) => _buildSubscribe(subscriptions);

    /// <inheritdoc />
    public string? BuildUnsubscribe(IReadOnlyList<StreamSubscription> subscriptions) => null;

    /// <inheritdoc />
    public string? Ping => null;

    /// <inheritdoc />
    public TimeSpan PingInterval => TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public bool IsServerPing(JsonElement message, out string pong)
    {
        pong = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public void Parse(JsonElement message, StreamEventSink sink)
    {
        // Intentionally empty: raw payloads are surfaced through the RawMessage event.
    }
}