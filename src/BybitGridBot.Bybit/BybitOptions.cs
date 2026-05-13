using System.ComponentModel.DataAnnotations;
using BybitGridBot.Domain;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.Bybit;

public sealed class BybitOptions
{
    [ConfigurationKeyName("BYBIT_BASE_URL")]
    public string BaseUrl { get; init; } = "https://api-testnet.bybit.com";

    [ConfigurationKeyName("BYBIT_MARKET_DATA_BASE_URL")]
    public string? MarketDataBaseUrl { get; init; }

    [ConfigurationKeyName("BYBIT_TRADING_BASE_URL")]
    public string? TradingBaseUrl { get; init; }

    [ConfigurationKeyName("TRADING_MODE")]
    public TradingMode TradingMode { get; init; } = TradingMode.Paper;

    [ConfigurationKeyName("BYBIT_API_KEY")]
    public string ApiKey { get; init; } = string.Empty;

    [ConfigurationKeyName("BYBIT_API_SECRET")]
    public string ApiSecret { get; init; } = string.Empty;

    [ConfigurationKeyName("BYBIT_RECV_WINDOW")]
    [Range(1, 60000)]
    public int RecvWindow { get; init; } = 5000;

    [ConfigurationKeyName("BYBIT_ACCOUNT_TYPE")]
    public string AccountType { get; init; } = "UNIFIED";

    [ConfigurationKeyName("BYBIT_RETRY_COUNT")]
    [Range(1, 10)]
    public int RetryCount { get; init; } = 3;

    public string ResolvePublicBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(MarketDataBaseUrl))
        {
            return MarketDataBaseUrl.TrimEnd('/');
        }

        return TradingMode switch
        {
            TradingMode.Paper => "https://api.bybit.com",
            TradingMode.Mainnet => "https://api.bybit.com",
            _ => ResolvePrivateBaseUrl()
        };
    }

    public string ResolvePrivateBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(TradingBaseUrl))
        {
            return TradingBaseUrl.TrimEnd('/');
        }

        return TradingMode switch
        {
            TradingMode.Mainnet => "https://api.bybit.com",
            _ => BaseUrl.TrimEnd('/')
        };
    }
}
