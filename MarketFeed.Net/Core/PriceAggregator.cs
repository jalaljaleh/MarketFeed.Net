using MarketFeed.Net.Models;

namespace MarketFeed.Net.Core;

/// <summary>
/// Pure functions that combine tickers of several exchanges into an aggregated view.
/// Useful for arbitrage scanners, best-execution routing and reference pricing.
/// </summary>
public static class PriceAggregator
{
    /// <summary>
    /// Builds an <see cref="AggregatedQuote"/> from a ticker board:
    /// best bid = highest bid, best ask = lowest ask, median of the last prices.
    /// </summary>
    /// <param name="symbol">Unified symbol.</param>
    /// <param name="tickers">Tickers collected from one or many exchanges.</param>
    public static AggregatedQuote Aggregate(string symbol, IReadOnlyList<Ticker> tickers)
    {
        var usable = tickers.Where(t => t.LastPrice > 0).ToList();

        var bestBid = usable
            .Where(t => t.BidPrice > 0)
            .OrderByDescending(t => t.BidPrice)
            .Select(t => new QuotePoint(t.Exchange, t.BidPrice, t.Volume24h))
            .FirstOrDefault();

        var bestAsk = usable
            .Where(t => t.AskPrice > 0)
            .OrderBy(t => t.AskPrice)
            .Select(t => new QuotePoint(t.Exchange, t.AskPrice, t.Volume24h))
            .FirstOrDefault();

        return new AggregatedQuote
        {
            Symbol = symbol,
            BestBid = bestBid,
            BestAsk = bestAsk,
            MedianPrice = Median(usable.Select(t => t.LastPrice)),
            Sources = usable,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Computes min/max/mean/median/standard deviation of the prices across exchanges.</summary>
    public static PriceStatistics Statistics(IReadOnlyList<Ticker> tickers)
    {
        var prices = tickers.Where(t => t.LastPrice > 0).ToList();
        if (prices.Count == 0)
        {
            return new PriceStatistics();
        }

        var values = prices.Select(t => t.LastPrice).ToList();
        var mean = values.Average();

        var min = prices.OrderBy(t => t.LastPrice).First();
        var max = prices.OrderByDescending(t => t.LastPrice).First();

        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;

        return new PriceStatistics
        {
            Min = min.LastPrice,
            MinExchange = min.Exchange,
            Max = max.LastPrice,
            MaxExchange = max.Exchange,
            Mean = mean,
            Median = Median(values),
            StandardDeviation = Sqrt(variance),
            SampleCount = values.Count,
        };
    }

    /// <summary>Median of a decimal sequence (works for both odd and even counts).</summary>
    public static decimal Median(IEnumerable<decimal> source)
    {
        var values = source.OrderBy(v => v).ToList();
        if (values.Count == 0)
        {
            return 0m;
        }

        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2m;
    }

    private static decimal Sqrt(decimal value)
    {
        if (value <= 0)
        {
            return 0m;
        }

        var guess = value;
        for (var i = 0; i < 24; i++)
        {
            guess = (guess + value / guess) / 2m;
        }

        return guess;
    }
}