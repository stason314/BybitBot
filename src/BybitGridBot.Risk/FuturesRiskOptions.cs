using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.Risk;

public sealed class FuturesRiskOptions
{
    [ConfigurationKeyName("FUTURES_MAX_NOTIONAL_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MaxNotionalUsdt { get; init; } = 100m;

    [ConfigurationKeyName("FUTURES_MAX_MARGIN_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MaxMarginUsdt { get; init; } = 50m;

    [ConfigurationKeyName("FUTURES_MAX_LEVERAGE")]
    [Range(typeof(decimal), "1", "1000")]
    public decimal MaxLeverage { get; init; } = 2m;

    [ConfigurationKeyName("FUTURES_MIN_LIQUIDATION_BUFFER_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinLiquidationBufferPercent { get; init; } = 15m;

    [ConfigurationKeyName("FUTURES_MAX_FUNDING_COST_USDT")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal MaxFundingCostUsdt { get; init; } = 1m;

    [ConfigurationKeyName("FUTURES_STOP_LOSS_REQUIRED")]
    public bool StopLossRequired { get; init; } = true;
}
