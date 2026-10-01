using System.Text.RegularExpressions;
using MarketFeed.Net.Models;

namespace MarketFeed.Net.Core;

/// <summary>
/// A market symbol in a canonical form (<c>BASE/QUOTE</c>) together with the per-exchange
/// wire formats. This is the single place that knows that Kraken calls Bitcoin <c>XBT</c>
/// and Bitfinex prefixes symbols with <c>t</c>.
/// </summary>
public readonly partial record struct UnifiedSymbol
{
    /// <summary>Base asset, upper case (e.g. <c>BTC</c>).</summary>
    public string Base { get; }

    /// <summary>Quote asset, upper case (e.g. <c>USDT</c>).</summary>
    public string Quote { get; }

    /// <summary>Creates a symbol from its two legs.</summary>
    public UnifiedSymbol(string @base, string quote)
    {
        Base = (@base ?? throw new ArgumentNullException(nameof(@base))).Trim().ToUpperInvariant();
        Quote = (quote ?? throw new ArgumentNullException(nameof(quote))).Trim().ToUpperInvariant();
    }

    /// <summary>Canonical representation, e.g. <c>BTC/USDT</c>.</summary>
    public override string ToString() => $"{Base}/{Quote}";

    /// <summary>
    /// Parses any common symbol format: <c>BTC/USDT</c>, <c>BTC-USDT</c>, <c>BTC_USDT</c>,
    /// <c>BTCUSDT</c> and Kraken style <c>XBT/USD</c>.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the symbol cannot be understood.</exception>
    public static UnifiedSymbol Parse(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        var value = symbol.Trim().ToUpperInvariant();

        if (value.Contains('/') || value.Contains('-') || value.Contains('_'))
        {
            var parts = Split().Split(value);
            if (parts.Length == 2)
            {
                return new UnifiedSymbol(NormalizeAsset(parts[0]), NormalizeAsset(parts[1]));
            }
        }

        // Concatenated form (BTCUSDT): try the known quote assets from longest to shortest.
        foreach (var quote in KnownQuotes)
        {
            if (value.EndsWith(quote, StringComparison.Ordinal) && value.Length > quote.Length)
            {
                return new UnifiedSymbol(NormalizeAsset(value[..^quote.Length]), quote);
            }
        }

        throw new FormatException($"Cannot parse the market symbol '{symbol}'. Use the BASE/QUOTE form.");
    }

    /// <summary>Alias normalisation (Kraken uses XBT for BTC).</summary>
    public static string NormalizeAsset(string asset) => asset.Trim().ToUpperInvariant() switch
    {
        "XBT" => "BTC",
        "BCC" => "BCH",
        var other => other,
    };

    /// <summary>Wire format for Binance, Bybit, Bitget, MEXC: <c>BTCUSDT</c>.</summary>
    public string ToCompact() => Base + Quote;

    /// <summary>Wire format for OKX, KuCoin and Nobitex: <c>BTC-USDT</c>.</summary>
    public string ToDash() => $"{Base}-{Quote}";

    /// <summary>Wire format for Coinbase: <c>BTC-USD</c> (USDT is quoted as USD on Coinbase).</summary>
    public string ToCoinbase() => $"{Base}-{Quote switch { "USDT" => "USD", _ => Quote }}";

    /// <summary>Wire format for Kraken: <c>XBTUSD</c> / <c>XXBTZUSD</c> compatible pair name.</summary>
    public string ToKraken() => (Base, Quote) switch
    {
        ("BTC", "USD") => "XBTUSD",
        ("BTC", "EUR") => "XBTEUR",
        ("ETH", "USD") => "ETHUSD",
        _ => Base + Quote,
    };

    /// <summary>Wire format for Gate.io: <c>BTC_USDT</c>.</summary>
    public string ToGateIo() => $"{Base}_{Quote}";

    /// <summary>Wire format for Bitstamp: <c>btcusd</c>.</summary>
    public string ToBitstamp() => (Base + Quote switch { "USDT" => "USD", _ => Quote }).ToLowerInvariant();

    /// <summary>Wire format for Bitfinex: <c>tBTCUSD</c>.</summary>
    public string ToBitfinex() => "t" + Base + Quote switch { "USDT" => "USD", _ => Quote };

    /// <summary>Wire format for HTX: <c>btcusdt</c>.</summary>
    public string ToHtx() => (Base + Quote).ToLowerInvariant();

    /// <summary>Wire format for CoinGecko style asset ids: <c>bitcoin</c>.</summary>
    public static string ToCoinGeckoId(string assetId) => assetId.Trim().ToLowerInvariant();

    [GeneratedRegex("[/\\-_]")]
    private static partial Regex Split();

    private static readonly string[] KnownQuotes =
    {
        "USDT", "USDC", "BUSD", "TUSD", "FDUSD", "DAI", "USD", "EUR", "GBP", "JPY", "BTC", "ETH", "IRT", "RLS", "TRY", "ARS",
    };
}