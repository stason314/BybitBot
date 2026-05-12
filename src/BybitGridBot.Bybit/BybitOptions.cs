using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.Bybit;

public sealed class BybitOptions
{
    [ConfigurationKeyName("BYBIT_BASE_URL")]
    [Required]
    public string BaseUrl { get; init; } = "https://api-testnet.bybit.com";

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
}
