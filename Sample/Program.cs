using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketFeed.Net;
using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using MarketFeed.Net.WebSocket;

namespace MarketFeed.Demo;

/// <summary>
/// A console "price board" that shows one symbol across every supported exchange,
/// detects cross-exchange arbitrage and finally streams real-time prices.
/// </summary>
public static class Program
{
    private const string Symbol = "BTC/USDT";

    public static async Task Main()
    {
        var options = new MarketFeedClientOptions
        {
            CacheTtl = TimeSpan.FromSeconds(3),
            UserAgent = "TraderBot/MarketFeedDemo",
            EnableClientRateLimiting = true,
        };

        await using var feed = new MarketFeedClient(options);

        Console.WriteLine("=== 1) Cross-exchange price board ===");
        await PrintBoardAsync(feed);

        Console.WriteLine();
        Console.WriteLine("=== 2) Aggregated quote (best execution) ===");
        var quote = await feed.GetAggregatedQuoteAsync(Symbol);
        Console.WriteLine($"best bid : {quote.BestBid?.Price:N2} on {quote.BestBid?.Exchange}");
        Console.WriteLine($"best ask : {quote.BestAsk?.Price:N2} on {quote.BestAsk?.Exchange}");
        Console.WriteLine($"median   : {quote.MedianPrice:N2}");
        Console.WriteLine($"spread   : {quote.SpreadPercent:0.000}%");

        Console.WriteLine();
        Console.WriteLine("=== 3) Price statistics (dispersion check) ===");
        var stats = await feed.GetPriceStatisticsAsync(Symbol);
        Console.WriteLine($"min={stats.Min:N2} ({stats.MinExchange})  max={stats.Max:N2} ({stats.MaxExchange})");
        Console.WriteLine($"mean={stats.Mean:N2}  median={stats.Median:N2}  stddev={stats.StandardDeviation:N2}");
        Console.WriteLine($"dispersion={stats.DispersionPercent:0.000}%  sources={stats.SampleCount}");

        Console.WriteLine();
        Console.WriteLine("=== 4) Order book + trades + candles ===");
        var book = await feed.GetOrderBookAsync(ExchangeId.Binance, Symbol, depth: 10);
        Console.WriteLine($"book: best bid={book.BestBid?.Price:N2} best ask={book.BestAsk?.Price:N2} imbalance={book.Imbalance:0.00}");

        foreach (var trade in await feed.GetTradesAsync(ExchangeId.Binance, Symbol, 5))
        {
            Console.WriteLine($"  {trade.Timestamp:HH:mm:ss} {trade.Side,-4} {trade.Quantity} @ {trade.Price:N2}");
        }

        var candles = await feed.GetCandlesAsync(ExchangeId.Okx, Symbol, Timeframe.Minute5, limit: 5);
        foreach (var candle in candles)
        {
            Console.WriteLine($"  {candle.OpenTime:HH:mm} o={candle.Open} h={candle.High} l={candle.Low} c={candle.Close} v={candle.Volume}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 5) Asset metadata (CoinGecko) ===");
        var asset = await feed.GetAssetAsync("bitcoin");
        Console.WriteLine($"{asset.Name} ({asset.Symbol.ToUpperInvariant()}) rank=#{asset.Rank}");
        Console.WriteLine($"price=${asset.PriceUsd:N2}  cap=${asset.MarketCapUsd:N0}  24h vol=${asset.Volume24hUsd:N0}  change={asset.Change24hPercent:0.00}%");

        Console.WriteLine();
        Console.WriteLine("=== 6) Realtime stream (press Ctrl+C to quit) ===");
        await RunStreamAsync();
    }

    /// <summary>Prints the same symbol across every exchange, sorted by price.</summary>
    private static async Task PrintBoardAsync(MarketFeedClient feed)
    {
        var board = await feed.GetTickerBoardAsync(Symbol);

        Console.WriteLine($"{"Exchange",-10} {"Symbol",-12} {"Last",14} {"24h %",8} {"Volume",14}");
        Console.WriteLine(new string('-', 62));

        foreach (var ticker in board)
        {
            Console.WriteLine(
                $"{ticker.Exchange,-10} {ticker.Symbol,-12} {ticker.LastPrice,14:N2} " +
                $"{ticker.Change24hPercent,7:+0.00;-0.00}% {ticker.Volume24h,14:N2}");
        }

        if (board.Count == 0)
        {
            Console.WriteLine("(no data — check your network or the rate limits)");
        }
    }

    /// <summary>Streams Binance prices until the user presses Ctrl+C.</summary>
    private static async Task RunStreamAsync()
    {
        await using var stream = new MarketFeedStreamClient();

        stream.TickerUpdated += (_, e) =>
            Console.WriteLine($"{e.Ticker.Exchange,-9} {e.Ticker.Symbol,-11} {e.Ticker.LastPrice,14:N2}  " +
                              $"{(e.Ticker.Change24hPercent >= 0 ? "▲" : "▼")} {e.Ticker.Change24hPercent:0.00}%");

        stream.OrderBookUpdated += (_, e) =>
            Console.WriteLine($"{e.OrderBook.Symbol}  bid={e.OrderBook.BestBid?.Price:N2} ask={e.OrderBook.BestAsk?.Price:N2} imbalance={e.OrderBook.Imbalance:0.00}");

        stream.TradeExecuted += (_, e) =>
            Console.WriteLine($"  trade {e.Trade.Side} {e.Trade.Quantity} @ {e.Trade.Price:N2}");

        stream.ErrorOccurred += (_, e) => Console.WriteLine($"error: {e.Exception.Message}");

        await stream.ConnectAsync(ExchangeId.Binance);
        await stream.SubscribeTickerAsync(ExchangeId.Binance, Symbol);
        await stream.SubscribeOrderBookAsync(ExchangeId.Binance, Symbol, depth: 20);
        await stream.SubscribeTradesAsync(ExchangeId.Binance, Symbol);

        var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Set();
        };

        quit.Wait();
    }
}