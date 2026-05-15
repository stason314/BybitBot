using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.Risk;

public sealed class RiskOptions
{
    [ConfigurationKeyName("MAX_DAILY_LOSS_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MaxDailyLossUsdt { get; init; } = 20m;

    [ConfigurationKeyName("MAX_DAILY_LOSS_EQUITY_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxDailyLossEquityPercent { get; init; } = 0m;

    [ConfigurationKeyName("MAX_DRAWDOWN_EQUITY_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxDrawdownEquityPercent { get; init; } = 0m;

    [ConfigurationKeyName("EMERGENCY_PAUSE")]
    public bool EmergencyPause { get; init; } = false;

    [ConfigurationKeyName("MAX_OPEN_ORDERS")]
    [Range(1, 1000)]
    public int MaxOpenOrders { get; init; } = 10;

    [ConfigurationKeyName("MAX_ACCOUNT_ACTIVE_BUY_ORDERS")]
    [Range(0, 1000)]
    public int MaxAccountActiveBuyOrders { get; init; } = 0;

    [ConfigurationKeyName("MAX_POSITION_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MaxPositionUsdt { get; init; } = 300m;

    [ConfigurationKeyName("MAX_ACCOUNT_EXPOSURE_USDT")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal MaxAccountExposureUsdt { get; init; } = 0m;

    [ConfigurationKeyName("MIN_ORDER_SIZE_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MinOrderSizeUsdt { get; init; } = 5m;

    [ConfigurationKeyName("MIN_USDT_RESERVE_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinUsdtReservePercent { get; init; } = 20m;

    [ConfigurationKeyName("MAX_TOTAL_EXPOSURE_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxTotalExposurePercent { get; init; } = 80m;

    [ConfigurationKeyName("ALLOW_HIGH_VOLATILITY_TRADING")]
    public bool AllowHighVolatilityTrading { get; init; } = false;
}
