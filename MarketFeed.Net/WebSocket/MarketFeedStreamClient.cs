using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MarketFeed.Net.Core;
using MarketFeed.Net.Models;

namespace MarketFeed.Net.WebSocket;

/// <summary>Raised when a ticker changes.</summary>
public sealed class TickerEventArgs : EventArgs
{
    /// <summary>The updated ticker.</summary>
    public required Ticker Ticker { get; init; }
}

/// <summary>Raised when the order book changes.</summary>
public sealed class OrderBookEventArgs : EventArgs
{
    /// <summary>The updated order book.</summary>
    public required OrderBook OrderBook { get; init; }
}

/// <summary>Raised when a trade happens.</summary>
public sealed class TradeEventArgs : EventArgs
{
    /// <summary>The trade.</summary>
    public required Trade Trade { get; init; }
}

/// <summary>Raised when a connection changes state.</summary>
public sealed class ConnectionStateEventArgs : EventArgs
{
    /// <summary>Exchange of the connection.</summary>
    public required ExchangeId Exchange { get; init; }

    /// <summary>New state.</summary>
    public required WebSocketState State { get; init; }

    /// <summary>Optional reason.</summary>
    public string? Reason { get; init; }
}

/// <summary>Raised on any internal error (the connection keeps running).</summary>
public sealed class StreamErrorEventArgs : EventArgs
{
    /// <summary>Exchange that produced the error.</summary>
    public required ExchangeId Exchange { get; init; }

    /// <summary>The error.</summary>
    public required Exception Exception { get; init; }
}

/// <summary>
/// Unified realtime market data client. One instance can hold several exchange connections;
/// every payload is translated into the shared <see cref="Ticker"/> / <see cref="OrderBook"/> /
/// <see cref="Trade"/> models. Reconnect with exponential backoff and resubscription are automatic.
/// </summary>
public sealed class MarketFeedStreamClient : IAsyncDisposable
{
    private sealed class Connection
    {
        public required IExchangeStreamAdapter Adapter { get; init; }
        public ClientWebSocket Socket { get; set; } = new();
        public List<StreamSubscription> Subscriptions { get; } = new();
        public CancellationTokenSource Stopping { get; } = new();
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private readonly Dictionary<ExchangeId, Connection> _connections = new();
    private readonly MarketFeedClientOptions _options;

    /// <summary>Creates the stream client.</summary>
    public MarketFeedStreamClient(MarketFeedClientOptions? options = null)
    {
        _options = options ?? new MarketFeedClientOptions();
    }

    /// <summary>Raised on every ticker update of any exchange.</summary>
    public event EventHandler<TickerEventArgs>? TickerUpdated;

    /// <summary>Raised on every order book update of any exchange.</summary>
    public event EventHandler<OrderBookEventArgs>? OrderBookUpdated;

    /// <summary>Raised on every public trade of any exchange.</summary>
    public event EventHandler<TradeEventArgs>? TradeExecuted;

    /// <summary>Raised when a connection opens, closes or reconnects.</summary>
    public event EventHandler<ConnectionStateEventArgs>? ConnectionStateChanged;

    /// <summary>Raised when a parse or transport error occurs.</summary>
    public event EventHandler<StreamErrorEventArgs>? ErrorOccurred;

    /// <summary>Raised for every raw frame (diagnostics and custom channels).</summary>
    public event EventHandler<string>? RawMessage;

    /// <summary>Opens a connection to an exchange and starts the receive loop.</summary>
    /// <param name="exchange">Target exchange.</param>
    /// <param name="adapter">Optional custom adapter (defaults to the built-in one).</param>
    public async Task ConnectAsync(ExchangeId exchange, IExchangeStreamAdapter? adapter = null, CancellationToken cancellationToken = default)
    {
        if (_connections.ContainsKey(exchange))
        {
            return;
        }

        var connection = new Connection { Adapter = adapter ?? CreateAdapter(exchange) };
        _connections[exchange] = connection;

        await connection.Socket.ConnectAsync(connection.Adapter.Endpoint, cancellationToken).ConfigureAwait(false);
        ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs { Exchange = exchange, State = WebSocketState.Open });

        _ = Task.Run(() => ReceiveLoopAsync(connection), CancellationToken.None);
        _ = Task.Run(() => PingLoopAsync(connection), CancellationToken.None);
    }

    /// <summary>Closes the connection of an exchange.</summary>
    public async Task DisconnectAsync(ExchangeId exchange, CancellationToken cancellationToken = default)
    {
        if (!_connections.Remove(exchange, out var connection))
        {
            return;
        }

        connection.Stopping.Cancel();

        try
        {
            await connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client shutdown", cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The socket may already be closed; nothing to do.
        }

        ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs { Exchange = exchange, State = WebSocketState.Closed });
    }

    /// <summary>Subscribes to the ticker of a market.</summary>
    public Task SubscribeTickerAsync(ExchangeId exchange, string symbol, CancellationToken cancellationToken = default)
        => SubscribeAsync(exchange, new StreamSubscription(StreamChannel.Ticker, UnifiedSymbol.Parse(symbol)), cancellationToken);

    /// <summary>Subscribes to the order book of a market.</summary>
    public Task SubscribeOrderBookAsync(ExchangeId exchange, string symbol, int depth = 20, CancellationToken cancellationToken = default)
        => SubscribeAsync(exchange, new StreamSubscription(StreamChannel.OrderBook, UnifiedSymbol.Parse(symbol), depth), cancellationToken);

    /// <summary>Subscribes to the public trade stream of a market.</summary>
    public Task SubscribeTradesAsync(ExchangeId exchange, string symbol, CancellationToken cancellationToken = default)
        => SubscribeAsync(exchange, new StreamSubscription(StreamChannel.Trades, UnifiedSymbol.Parse(symbol)), cancellationToken);

    /// <summary>Subscribes to an arbitrary channel definition.</summary>
    public async Task SubscribeAsync(ExchangeId exchange, StreamSubscription subscription, CancellationToken cancellationToken = default)
    {
        var connection = GetConnection(exchange);
        connection.Subscriptions.Add(subscription);

        if (connection.Socket.State != WebSocketState.Open)
        {
            return;   // the receive loop reconnects and resubscribes automatically
        }

        var frame = connection.Adapter.BuildSubscribe(new[] { subscription });
        if (!string.IsNullOrEmpty(frame))
        {
            await SendAsync(connection, frame, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Unsubscribes from every channel of a market on an exchange.</summary>
    public async Task UnsubscribeAsync(ExchangeId exchange, string symbol, CancellationToken cancellationToken = default)
    {
        var connection = GetConnection(exchange);
        var unified = UnifiedSymbol.Parse(symbol);
        var removed = connection.Subscriptions.Where(s => s.Symbol == unified).ToList();
        connection.Subscriptions.RemoveAll(s => s.Symbol == unified);

        if (removed.Count > 0 && connection.Socket.State == WebSocketState.Open)
        {
            var frame = connection.Adapter.BuildUnsubscribe(removed);
            if (!string.IsNullOrEmpty(frame))
            {
                await SendAsync(connection, frame, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Gets the active subscriptions of an exchange.</summary>
    public IReadOnlyList<StreamSubscription> GetSubscriptions(ExchangeId exchange)
        => _connections.TryGetValue(exchange, out var connection) ? connection.Subscriptions : Array.Empty<StreamSubscription>();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var exchange in _connections.Keys.ToList())
        {
            await DisconnectAsync(exchange).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------

    private Connection GetConnection(ExchangeId exchange)
        => _connections.TryGetValue(exchange, out var connection)
            ? connection
            : throw new MarketDataException(exchange, "The exchange is not connected. Call ConnectAsync first.");

    private static IExchangeStreamAdapter CreateAdapter(ExchangeId exchange) => exchange switch
    {
        ExchangeId.Binance => new BinanceStreamAdapter(),
        ExchangeId.Okx => new OkxStreamAdapter(),
        ExchangeId.Bybit => new BybitStreamAdapter(),
        _ => throw new NotSupportedException($"No built-in realtime adapter for {exchange}. Pass a custom IExchangeStreamAdapter."),
    };

    private async Task ReceiveLoopAsync(Connection connection)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (!connection.Stopping.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
            {
                var message = new StringBuilder();

                while (true)
                {
                    var result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), connection.Stopping.Token)
                        .ConfigureAwait(false);

                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                var text = message.ToString();
                if (text.Length == 0)
                {
                    continue;
                }

                RawMessage?.Invoke(this, text);
                await HandleAsync(connection, text).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new StreamErrorEventArgs { Exchange = connection.Adapter.Exchange, Exception = ex });
        }
        finally
        {
            if (!connection.Stopping.IsCancellationRequested)
            {
                await ReconnectAsync(connection).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(Connection connection, string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch
        {
            return;   // OKX "ping"/"pong" text frames and similar are handled by the adapter first.
        }

        using (document)
        {
            var root = document.RootElement;

            if (connection.Adapter.IsServerPing(root, out var pong))
            {
                await SendAsync(connection, pong, connection.Stopping.Token).ConfigureAwait(false);
                return;
            }

            var sink = new StreamEventSink
            {
                OnTicker = ticker => TickerUpdated?.Invoke(this, new TickerEventArgs { Ticker = ticker }),
                OnOrderBook = book => OrderBookUpdated?.Invoke(this, new OrderBookEventArgs { OrderBook = book }),
                OnTrade = trade => TradeExecuted?.Invoke(this, new TradeEventArgs { Trade = trade }),
            };

            try
            {
                connection.Adapter.Parse(root, sink);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new StreamErrorEventArgs { Exchange = connection.Adapter.Exchange, Exception = ex });
            }
        }
    }

    private async Task PingLoopAsync(Connection connection)
    {
        while (!connection.Stopping.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
        {
            await Task.Delay(connection.Adapter.PingInterval, connection.Stopping.Token).ConfigureAwait(false);

            if (connection.Adapter.Ping is { } ping)
            {
                await SendAsync(connection, ping, connection.Stopping.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task ReconnectAsync(Connection connection)
    {
        var delay = TimeSpan.FromSeconds(1);

        while (!connection.Stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, connection.Stopping.Token).ConfigureAwait(false);
                await connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Ignore close failures during reconnect.
            }

            try
            {
                var socket = new ClientWebSocket();
                await socket.ConnectAsync(connection.Adapter.Endpoint, connection.Stopping.Token).ConfigureAwait(false);

                connection.Socket = socket;

                ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs
                {
                    Exchange = connection.Adapter.Exchange,
                    State = WebSocketState.Open,
                    Reason = "reconnected",
                });

                // Resubscribe every channel that was active before the drop.
                if (connection.Subscriptions.Count > 0)
                {
                    var frame = connection.Adapter.BuildSubscribe(connection.Subscriptions);
                    if (!string.IsNullOrEmpty(frame))
                    {
                        await SendAsync(connection, frame, connection.Stopping.Token).ConfigureAwait(false);
                    }
                }

                _ = Task.Run(() => ReceiveLoopAsync(connection), CancellationToken.None);
                _ = Task.Run(() => PingLoopAsync(connection), CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new StreamErrorEventArgs { Exchange = connection.Adapter.Exchange, Exception = ex });
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 30_000));
            }
        }
    }

    private async Task SendAsync(Connection connection, string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);

        await connection.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            connection.SendLock.Release();
        }
    }
}