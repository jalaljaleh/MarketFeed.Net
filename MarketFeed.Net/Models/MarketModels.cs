namespace MarketFeed.Net.Models;

/// <summary>
/// A ticker snapshot: the current price, best bid/ask and the rolling 24 hour statistics.
/// All monetary values are <see cref="decimal"/> so no precision is lost on price aggregation.
/// </summary>
/// <param name="Exchange">Source exchange.</param>
/// <param name="Symbol">Unified symbol, e.g. <c>BTC/USDT</c>.</param>
/// <param name="LastPrice">Last traded price.</param>
/// <param name="BidPrice">Best bid price.</param>
/// <param name="AskPrice">Best ask price.</param>
/// <param name="High24h">Highest price of the last 24 hours.</param>
/// <param name="Low24h">Lowest price of the last 24 hours.</param>
/// <param name="Open24h">Price 24 hours ago.</param>
/// <param name="Volume24h">Traded volume in the base asset.</param>
/// <param name="QuoteVolume24h">Traded volume in the quote asset.</param>
/// <param name="Change24hPercent">24 hour change in percent.</param>
/// <param name="Timestamp">When the snapshot was produced (UTC).</param>
public sealed record Ticker(
    ExchangeId Exchange,
    string Symbol,
    decimal LastPrice,
    decimal BidPrice,
    decimal AskPrice,
    decimal High24h,
    decimal Low24h,
    decimal Open24h,
    decimal Volume24h,
    decimal QuoteVolume24h,
    decimal Change24hPercent,
    DateTimeOffset Timestamp)
{
    /// <summary>Mid price between best bid and best ask.</summary>
    public decimal MidPrice => BidPrice > 0 && AskPrice > 0 ? (BidPrice + AskPrice) / 2m : LastPrice;

    /// <summary>Relative bid/ask spread in percent.</summary>
    public decimal SpreadPercent => BidPrice <= 0 ? 0m : (AskPrice - BidPrice) / BidPrice * 100m;
}

/// <summary>A single price level of an order book.</summary>
/// <param name="Price">Price of the level.</param>
/// <param name="Quantity">Cumulative quantity offered at that price.</param>
public sealed record OrderBookLevel(decimal Price, decimal Quantity)
{
    /// <summary>Notional value of the level.</summary>
    public decimal Notional => Price * Quantity;
}

/// <summary>Order book snapshot of a market.</summary>
public sealed class OrderBook
{
    /// <summary>Source exchange.</summary>
    public ExchangeId Exchange { get; set; }

    /// <summary>Unified symbol.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Ask (sell) levels sorted by price ascending.</summary>
    public IReadOnlyList<OrderBookLevel> Asks { get; set; } = Array.Empty<OrderBookLevel>();

    /// <summary>Bid (buy) levels sorted by price descending.</summary>
    public IReadOnlyList<OrderBookLevel> Bids { get; set; } = Array.Empty<OrderBookLevel>();

    /// <summary>Time of the snapshot (UTC).</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Best (highest) bid.</summary>
    public OrderBookLevel? BestBid => Bids.Count == 0 ? null : Bids[0];

    /// <summary>Best (lowest) ask.</summary>
    public OrderBookLevel? BestAsk => Asks.Count == 0 ? null : Asks[0];

    /// <summary>Absolute spread.</summary>
    public decimal Spread => BestBid is null || BestAsk is null ? 0m : BestAsk.Price - BestBid.Price;

    /// <summary>Mid price of the book.</summary>
    public decimal MidPrice => BestBid is null || BestAsk is null ? 0m : (BestBid.Price + BestAsk.Price) / 2m;

    /// <summary>
    /// Order book imbalance in the range 0..1. Values above 0.5 mean the bid side is heavier
    /// (buying pressure); below 0.5 means the ask side is heavier.
    /// </summary>
    public decimal Imbalance
    {
        get
        {
            decimal bidVolume = 0, askVolume = 0;
            foreach (var level in Bids) bidVolume += level.Quantity;
            foreach (var level in Asks) askVolume += level.Quantity;
            var total = bidVolume + askVolume;
            return total == 0 ? 0.5m : bidVolume / total;
        }
    }
}

/// <summary>A single public trade.</summary>
/// <param name="Exchange">Source exchange.</param>
/// <param name="Symbol">Unified symbol.</param>
/// <param name="Price">Execution price.</param>
/// <param name="Quantity">Executed quantity in the base asset.</param>
/// <param name="Side">Side of the aggressor.</param>
/// <param name="Timestamp">Execution time (UTC).</param>
/// <param name="TradeId">Exchange specific trade id, when available.</param>
public sealed record Trade(
    ExchangeId Exchange,
    string Symbol,
    decimal Price,
    decimal Quantity,
    TradeSide Side,
    DateTimeOffset Timestamp,
    long? TradeId = null)
{
    /// <summary>Notional value of the trade.</summary>
    public decimal Notional => Price * Quantity;
}

/// <summary>An OHLCV candle.</summary>
/// <param name="OpenTime">Opening time of the candle (UTC).</param>
/// <param name="Open">Open price.</param>
/// <param name="High">Highest price.</param>
/// <param name="Low">Lowest price.</param>
/// <param name="Close">Close price.</param>
/// <param name="Volume">Volume in the base asset.</param>
/// <param name="QuoteVolume">Volume in the quote asset, when provided.</param>
public sealed record Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal? QuoteVolume = null)
{
    /// <summary>Range of the candle (high - low).</summary>
    public decimal Range => High - Low;

    /// <summary>Close minus open.</summary>
    public decimal Change => Close - Open;

    /// <summary>Relative change of the candle in percent.</summary>
    public decimal ChangePercent => Open == 0 ? 0m : (Close - Open) / Open * 100m;

    /// <summary>Is this a bullish (green) candle?</summary>
    public bool IsBullish => Close >= Open;
}

/// <summary>Metadata of a tradable market.</summary>
public sealed class MarketInfo
{
    /// <summary>Source exchange.</summary>
    public ExchangeId Exchange { get; set; }

    /// <summary>Unified symbol, e.g. <c>BTC/USDT</c>.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Base asset, e.g. <c>BTC</c>.</summary>
    public string BaseAsset { get; set; } = string.Empty;

    /// <summary>Quote asset, e.g. <c>USDT</c>.</summary>
    public string QuoteAsset { get; set; } = string.Empty;

    /// <summary>Number of decimal digits allowed in the price.</summary>
    public int PricePrecision { get; set; }

    /// <summary>Number of decimal digits allowed in the quantity.</summary>
    public int QuantityPrecision { get; set; }

    /// <summary>Minimum order quantity.</summary>
    public decimal MinQuantity { get; set; }

    /// <summary>Minimum order notional (price * quantity).</summary>
    public decimal MinNotional { get; set; }

    /// <summary>Market status.</summary>
    public MarketStatus Status { get; set; } = MarketStatus.Active;

    /// <summary>Is the market currently tradable?</summary>
    public bool IsActive => Status == MarketStatus.Active;
}

/// <summary>
/// Metadata of a crypto asset (typically sourced from CoinGecko): name, rank, market cap and volume.
/// </summary>
/// <param name="Id">Asset id, e.g. <c>bitcoin</c>.</param>
/// <param name="Symbol">Ticker symbol, e.g. <c>btc</c>.</param>
/// <param name="Name">Human readable name, e.g. <c>Bitcoin</c>.</param>
/// <param name="ImageUrl">Logo URL.</param>
/// <param name="Rank">Market cap rank.</param>
/// <param name="PriceUsd">Price in USD.</param>
/// <param name="MarketCapUsd">Market capitalisation in USD.</param>
/// <param name="Volume24hUsd">24 hour traded volume in USD.</param>
/// <param name="Change24hPercent">24 hour price change in percent.</param>
/// <param name="CirculatingSupply">Circulating supply.</param>
public sealed record AssetInfo(
    string Id,
    string Symbol,
    string Name,
    string? ImageUrl,
    int? Rank,
    decimal? PriceUsd,
    decimal? MarketCapUsd,
    decimal? Volume24hUsd,
    decimal? Change24hPercent,
    decimal? CirculatingSupply);