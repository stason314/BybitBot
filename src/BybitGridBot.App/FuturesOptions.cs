using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.App;

public sealed class FuturesOptions
{
    [ConfigurationKeyName("FUTURES_ENABLED")]
    public bool Enabled { get; init; } = false;

    [ConfigurationKeyName("FUTURES_CATEGORY")]
    public string Category { get; init; } = "linear";

    [ConfigurationKeyName("LEVERAGE")]
    [Range(typeof(decimal), "1", "1000")]
    public decimal Leverage { get; init; } = 2m;

    [ConfigurationKeyName("MARGIN_MODE")]
    public string MarginMode { get; init; } = "isolated";

    [ConfigurationKeyName("POSITION_MODE")]
    public string PositionMode { get; init; } = "oneway";

    [ConfigurationKeyName("MAX_NOTIONAL_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MaxNotionalUsdt { get; init; } = 100m;

    [ConfigurationKeyName("MAX_MARGIN_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MaxMarginUsdt { get; init; } = 50m;

    [ConfigurationKeyName("MIN_LIQUIDATION_BUFFER_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinLiquidationBufferPercent { get; init; } = 15m;

    [ConfigurationKeyName("STOP_LOSS_REQUIRED")]
    public bool StopLossRequired { get; init; } = true;
}
