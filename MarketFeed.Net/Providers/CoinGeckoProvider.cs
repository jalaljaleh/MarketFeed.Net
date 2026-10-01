using MarketFeed.Net.Abstractions;
using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace MarketFeed.Net.Providers;

/// <summary>
/// Asset metadata from <b>CoinGecko</b> (https://api.coingecko.com/api/v3) — free, no API key.
/// Gives you the name, logo, market-cap rank, price, market cap, 24h volume and supply of any coin.
/// </summary>
/// <remarks>Free tier is limited to roughly 5–15 calls per minute; results are cached by <see cref="MarketFeedClient"/>.</remarks>
public sealed class CoinGeckoProvider : RestProviderBase, IAssetInfoProvider
{
    /// <summary>Creates the provider with its own handler.</summary>
    public CoinGeckoProvider(HttpMessageHandler? handler = null, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(handler, options, logger) { }

    /// <summary>Creates the provider from an externally managed <see cref="HttpClient"/>.</summary>
    public CoinGeckoProvider(HttpClient httpClient, MarketFeedClientOptions? options = null, ILogger? logger = null)
        : base(httpClient, options, logger) { }

    /// <summary>Creates the provider with shared options.</summary>
    public CoinGeckoProvider(MarketFeedClientOptions options, ILogger? logger = null)
        : base((HttpMessageHandler?)null, options, logger) { }

    /// <inheritdoc />
    public override ExchangeId Exchange => ExchangeId.CoinGecko;

    /// <inheritdoc />
    public override Uri BaseAddress => new("https://api.coingecko.com");

    /// <summary>CoinGecko is a data aggregator and has no order book of its own.</summary>
    public override bool SupportsRealtime => false;

    /// <summary>Returns the metadata of a single asset, e.g. <c>bitcoin</c> or <c>tether</c>.</summary>
    public async Task<AssetInfo> GetAssetAsync(string assetId, CancellationToken cancellationToken = default)
    {
        var assets = await GetAssetsAsync(new[] { assetId }, cancellationToken).ConfigureAwait(false);

        return assets.FirstOrDefault()
            ?? throw new MarketDataException(Exchange, $"Asset '{assetId}' was not found on CoinGecko.");
    }

    /// <summary>Returns the metadata of several assets in a single request.</summary>
    public async Task<IReadOnlyList<AssetInfo>> GetAssetsAsync(IEnumerable<string> assetIds, CancellationToken cancellationToken = default)
    {
        var ids = string.Join(',', assetIds.Select(UnifiedSymbol.ToCoinGeckoId));

        var node = await GetJsonAsync("/api/v3/coins/markets",
            Query(
                ("vs_currency", "usd"),
                ("ids", ids),
                ("price_change_percentage", "24h")),
            "assets", cancellationToken).ConfigureAwait(false);

        var assets = new List<AssetInfo>();
        if (node is not JsonArray array)
        {
            return assets;
        }

        foreach (var item in array)
        {
            if (item?["id"]?.GetValue<string>() is not { } id)
            {
                continue;
            }

            assets.Add(new AssetInfo(
                Id: id,
                Symbol: item["symbol"]?.GetValue<string>() ?? string.Empty,
                Name: item["name"]?.GetValue<string>() ?? string.Empty,
                ImageUrl: item["image"]?.GetValue<string>(),
                Rank: item["market_cap_rank"]?.GetValue<int>(),
                PriceUsd: Dec(item["current_price"]),
                MarketCapUsd: Dec(item["market_cap"]),
                Volume24hUsd: Dec(item["total_volume"]),
                Change24hPercent: Dec(item["price_change_percentage_24h"]),
                CirculatingSupply: Dec(item["circulating_supply"])));
        }

        return assets;
    }

    /// <summary>Lists every asset id known to CoinGecko (useful for symbol resolution).</summary>
    public async Task<IReadOnlyList<(string Id, string Symbol, string Name)>> ListAssetsAsync(CancellationToken cancellationToken = default)
    {
        var node = await GetJsonAsync("/api/v3/coins/list", null, "assets", cancellationToken).ConfigureAwait(false);

        var list = new List<(string, string, string)>();
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is null)
                {
                    continue;
                }

                list.Add((
                    item["id"]?.GetValue<string>() ?? string.Empty,
                    item["symbol"]?.GetValue<string>() ?? string.Empty,
                    item["name"]?.GetValue<string>() ?? string.Empty));
            }
        }

        return list;
    }

    // ---- IMarketDataProvider members: not applicable for an aggregator -----------------

    /// <inheritdoc />
    public override Task<Ticker> GetTickerAsync(string symbol, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CoinGecko provides asset metadata, not exchange tickers. Use GetAssetAsync instead.");

    /// <inheritdoc />
    public override Task<IReadOnlyList<Ticker>> GetTickersAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CoinGecko provides asset metadata, not exchange tickers.");

    /// <inheritdoc />
    public override Task<OrderBook> GetOrderBookAsync(string symbol, int depth = 20, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CoinGecko does not expose an order book.");

    /// <inheritdoc />
    public override Task<IReadOnlyList<Trade>> GetTradesAsync(string symbol, int limit = 50, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CoinGecko does not expose a public trade feed.");

    /// <inheritdoc />
    public override Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string symbol, Timeframe timeframe, int limit = 100,
        DateTimeOffset? from = null, DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CoinGecko does not expose exchange candles.");

    /// <inheritdoc />
    public override Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MarketInfo>>(Array.Empty<MarketInfo>());
}