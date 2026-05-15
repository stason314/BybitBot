using System.ComponentModel.DataAnnotations;
using BybitGridBot.Domain;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.App;

public sealed class AppOptions
{
    [ConfigurationKeyName("TRADING_MODE")]
    public TradingMode TradingMode { get; init; } = TradingMode.Paper;

    [ConfigurationKeyName("SQLITE_PATH")]
    [Required]
    public string SqlitePath { get; init; } = "/app/data/bybit-grid-bot.db";

    [ConfigurationKeyName("LOG_LEVEL")]
    public string LogLevel { get; init; } = "Information";

    [ConfigurationKeyName("WEB_PORT")]
    public int WebPort { get; init; } = 8080;

    [ConfigurationKeyName("SIGNAL_TRADING_ENABLED")]
    public bool SignalTradingEnabled { get; init; } = false;
}
