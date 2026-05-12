using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.Risk;

public sealed class RiskOptions
{
    [ConfigurationKeyName("MAX_DAILY_LOSS_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MaxDailyLossUsdt { get; init; } = 20m;

    [ConfigurationKeyName("MAX_OPEN_ORDERS")]
    [Range(1, 1000)]
    public int MaxOpenOrders { get; init; } = 10;

    [ConfigurationKeyName("MAX_POSITION_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MaxPositionUsdt { get; init; } = 300m;

    [ConfigurationKeyName("MIN_ORDER_SIZE_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MinOrderSizeUsdt { get; init; } = 5m;
}
