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

    [ConfigurationKeyName("FUTURES_MAX_DAILY_LOSS_USDT")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal MaxDailyLossUsdt { get; init; } = 20m;

    [ConfigurationKeyName("FUTURES_MAX_DAILY_LOSS_EQUITY_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxDailyLossEquityPercent { get; init; } = 0m;

    [ConfigurationKeyName("FUTURES_MAX_DRAWDOWN_EQUITY_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxDrawdownEquityPercent { get; init; } = 0m;

    [ConfigurationKeyName("FUTURES_MAX_OPEN_POSITIONS")]
    [Range(0, 1000)]
    public int MaxOpenPositions { get; init; } = 1;

    [ConfigurationKeyName("FUTURES_EMERGENCY_PAUSE")]
    public bool EmergencyPause { get; init; } = false;

    [ConfigurationKeyName("FUTURES_STOP_LOSS_REQUIRED")]
    public bool StopLossRequired { get; init; } = true;
}
