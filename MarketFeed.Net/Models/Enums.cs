namespace MarketFeed.Net.Models;

/// <summary>Supported exchanges / data sources.</summary>
public enum ExchangeId
{
    /// <summary>Binance — api.binance.com</summary>
    Binance,

    /// <summary>OKX — www.okx.com</summary>
    Okx,

    /// <summary>Bybit — api.bybit.com</summary>
    Bybit,

    /// <summary>Kraken — api.kraken.com</summary>
    Kraken,

    /// <summary>Coinbase Exchange — api.exchange.coinbase.com</summary>
    Coinbase,

    /// <summary>KuCoin — api.kucoin.com</summary>
    Kucoin,

    /// <summary>Bitget — api.bitget.com</summary>
    Bitget,

    /// <summary>Gate.io — api.gateio.ws</summary>
    GateIo,

    /// <summary>MEXC — api.mexc.com</summary>
    Mexc,

    /// <summary>Bitstamp — www.bitstamp.net</summary>
    Bitstamp,

    /// <summary>Bitfinex — api-pub.bitfinex.com</summary>
    Bitfinex,

    /// <summary>HTX (Huobi) — api.huobi.pro</summary>
    Htx,

    /// <summary>Nobitex — apiv2.nobitex.ir (Iranian market, IRT quotes)</summary>
    Nobitex,

    /// <summary>CoinGecko — asset metadata aggregator</summary>
    CoinGecko,
}

/// <summary>Side of a trade or an order book imbalance calculation.</summary>
public enum TradeSide
{
    /// <summary>The aggressor was a buyer.</summary>
    Buy,

    /// <summary>The aggressor was a seller.</summary>
    Sell,

    /// <summary>Unknown direction.</summary>
    Unknown,
}

/// <summary>State of a market on an exchange.</summary>
public enum MarketStatus
{
    /// <summary>Trading is allowed.</summary>
    Active,

    /// <summary>Trading is halted.</summary>
    Halted,

    /// <summary>The market was delisted.</summary>
    Delisted,

    /// <summary>Unknown state.</summary>
    Unknown,
}

/// <summary>Candle intervals supported by the library.</summary>
public enum Timeframe
{
    /// <summary>1 minute.</summary>
    Minute1,

    /// <summary>3 minutes.</summary>
    Minute3,

    /// <summary>5 minutes.</summary>
    Minute5,

    /// <summary>15 minutes.</summary>
    Minute15,

    /// <summary>30 minutes.</summary>
    Minute30,

    /// <summary>1 hour.</summary>
    Hour1,

    /// <summary>2 hours.</summary>
    Hour2,

    /// <summary>4 hours.</summary>
    Hour4,

    /// <summary>6 hours.</summary>
    Hour6,

    /// <summary>12 hours.</summary>
    Hour12,

    /// <summary>1 day.</summary>
    Day1,

    /// <summary>3 days.</summary>
    Day3,

    /// <summary>1 week.</summary>
    Week1,

    /// <summary>1 month.</summary>
    Month1,
}

/// <summary>
/// Converts the unified <see cref="Timeframe"/> into the wire format of each exchange.
/// Keeping this in one place is what makes the providers so small.
/// </summary>
public static class TimeframeExtensions
{
    /// <summary>Total duration of the timeframe.</summary>
    public static TimeSpan ToTimeSpan(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute1 => TimeSpan.FromMinutes(1),
        Timeframe.Minute3 => TimeSpan.FromMinutes(3),
        Timeframe.Minute5 => TimeSpan.FromMinutes(5),
        Timeframe.Minute15 => TimeSpan.FromMinutes(15),
        Timeframe.Minute30 => TimeSpan.FromMinutes(30),
        Timeframe.Hour1 => TimeSpan.FromHours(1),
        Timeframe.Hour2 => TimeSpan.FromHours(2),
        Timeframe.Hour4 => TimeSpan.FromHours(4),
        Timeframe.Hour6 => TimeSpan.FromHours(6),
        Timeframe.Hour12 => TimeSpan.FromHours(12),
        Timeframe.Day1 => TimeSpan.FromDays(1),
        Timeframe.Day3 => TimeSpan.FromDays(3),
        Timeframe.Week1 => TimeSpan.FromDays(7),
        Timeframe.Month1 => TimeSpan.FromDays(30),
        _ => TimeSpan.FromMinutes(1),
    };

    /// <summary>Wire format used by Binance, MEXC and Gate.io (<c>1m</c>, <c>1h</c>, <c>1d</c>, <c>1w</c>).</summary>
    public static string ToShortString(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute1 => "1m",
        Timeframe.Minute3 => "3m",
        Timeframe.Minute5 => "5m",
        Timeframe.Minute15 => "15m",
        Timeframe.Minute30 => "30m",
        Timeframe.Hour1 => "1h",
        Timeframe.Hour2 => "2h",
        Timeframe.Hour4 => "4h",
        Timeframe.Hour6 => "6h",
        Timeframe.Hour12 => "12h",
        Timeframe.Day1 => "1d",
        Timeframe.Day3 => "3d",
        Timeframe.Week1 => "1w",
        Timeframe.Month1 => "1M",
        _ => "1m",
    };

    /// <summary>Wire format used by OKX (<c>1m</c>, <c>1H</c>, <c>1D</c>, <c>1W</c>, <c>1M</c>).</summary>
    public static string ToOkxBar(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.Hour1 => "1H",
        Timeframe.Hour2 => "2H",
        Timeframe.Hour4 => "4H",
        Timeframe.Hour6 => "6H",
        Timeframe.Hour12 => "12H",
        Timeframe.Day1 => "1D",
        Timeframe.Day3 => "3D",
        Timeframe.Week1 => "1W",
        Timeframe.Month1 => "1M",
        _ => timeframe.ToShortString(),
    };

    /// <summary>Wire format used by Bybit v5 (<c>1</c>, <c>60</c>, <c>D</c>, <c>W</c>, <c>M</c>).</summary>
    public static string ToBybitInterval(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute1 => "1",
        Timeframe.Minute3 => "3",
        Timeframe.Minute5 => "5",
        Timeframe.Minute15 => "15",
        Timeframe.Minute30 => "30",
        Timeframe.Hour1 => "60",
        Timeframe.Hour2 => "120",
        Timeframe.Hour4 => "240",
        Timeframe.Hour6 => "360",
        Timeframe.Hour12 => "720",
        Timeframe.Day1 => "D",
        Timeframe.Day3 => "D",
        Timeframe.Week1 => "W",
        Timeframe.Month1 => "M",
        _ => "1",
    };

    /// <summary>Wire format used by Kraken (minutes as a number).</summary>
    public static int ToKrakenInterval(this Timeframe timeframe) => (int)timeframe.ToTimeSpan().TotalMinutes;

    /// <summary>Granularity in seconds used by Coinbase (60, 300, 900, 3600, 21600, 86400).</summary>
    public static int ToCoinbaseGranularity(this Timeframe timeframe) => (int)timeframe.ToTimeSpan().TotalSeconds;

    /// <summary>Wire format used by KuCoin and Bitget (<c>1min</c>, <c>1hour</c>, <c>1day</c>).</summary>
    public static string ToKucoinType(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute1 => "1min",
        Timeframe.Minute3 => "3min",
        Timeframe.Minute5 => "5min",
        Timeframe.Minute15 => "15min",
        Timeframe.Minute30 => "30min",
        Timeframe.Hour1 => "1hour",
        Timeframe.Hour2 => "2hour",
        Timeframe.Hour4 => "4hour",
        Timeframe.Hour6 => "6hour",
        Timeframe.Hour12 => "12hour",
        Timeframe.Day1 => "1day",
        Timeframe.Day3 => "3day",
        Timeframe.Week1 => "1week",
        Timeframe.Month1 => "1month",
        _ => "1min",
    };

    /// <summary>Wire format used by Bitfinex (<c>trade:1m:tBTCUSD</c>).</summary>
    public static string ToBitfinexKey(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute1 => "1m",
        Timeframe.Minute5 => "5m",
        Timeframe.Minute15 => "15m",
        Timeframe.Minute30 => "30m",
        Timeframe.Hour1 => "1h",
        Timeframe.Hour2 => "2h",
        Timeframe.Hour4 => "4h",
        Timeframe.Hour6 => "6h",
        Timeframe.Hour12 => "12h",
        Timeframe.Day1 => "1D",
        Timeframe.Day3 => "1D",
        Timeframe.Week1 => "1W",
        Timeframe.Month1 => "1M",
        _ => "1m",
    };

    /// <summary>Wire format used by Bitstamp (seconds).</summary>
    public static int ToBitstampStep(this Timeframe timeframe) => (int)timeframe.ToTimeSpan().TotalSeconds;

    /// <summary>Wire format used by Nobitex TradingView UDF (<c>1</c>, <c>60</c>, <c>D</c>).</summary>
    public static string ToNobitexResolution(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute1 => "1",
        Timeframe.Minute5 => "5",
        Timeframe.Minute15 => "15",
        Timeframe.Minute30 => "30",
        Timeframe.Hour1 => "60",
        Timeframe.Hour4 => "240",
        Timeframe.Hour12 => "720",
        Timeframe.Day1 => "D",
        Timeframe.Day3 => "3D",
        _ => "60",
    };
}