using BybitGridBot.Bybit;
using BybitGridBot.Domain;

namespace BybitGridBot.Tests;

public sealed class BybitOptionsTests
{
    [Fact]
    public void ResolvePublicBaseUrl_UsesMainnetForPaperModeByDefault()
    {
        var options = new BybitOptions
        {
            TradingMode = TradingMode.Paper,
            BaseUrl = "https://api-testnet.bybit.com"
        };

        Assert.Equal("https://api.bybit.com", options.ResolvePublicBaseUrl());
        Assert.Equal("https://api-testnet.bybit.com", options.ResolvePrivateBaseUrl());
    }

    [Fact]
    public void ResolvePublicBaseUrl_UsesTradingEndpointForTestnetByDefault()
    {
        var options = new BybitOptions
        {
            TradingMode = TradingMode.Testnet,
            BaseUrl = "https://api-testnet.bybit.com"
        };

        Assert.Equal("https://api-testnet.bybit.com", options.ResolvePublicBaseUrl());
        Assert.Equal("https://api-testnet.bybit.com", options.ResolvePrivateBaseUrl());
    }

    [Fact]
    public void ResolvePublicBaseUrl_UsesExplicitMarketDataOverride()
    {
        var options = new BybitOptions
        {
            TradingMode = TradingMode.Testnet,
            BaseUrl = "https://api-testnet.bybit.com",
            MarketDataBaseUrl = "https://api.bybit.com"
        };

        Assert.Equal("https://api.bybit.com", options.ResolvePublicBaseUrl());
    }
}
