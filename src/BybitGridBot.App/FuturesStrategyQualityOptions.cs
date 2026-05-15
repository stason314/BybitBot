using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.App;

public sealed class FuturesStrategyQualityOptions
{
    [ConfigurationKeyName("FUTURES_MAX_ENTRY_ATR_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxEntryAtrPercent { get; init; } = 6m;

    [ConfigurationKeyName("FUTURES_BTC_RISK_OFF_ENABLED")]
    public bool BtcRiskOffEnabled { get; init; } = true;

    [ConfigurationKeyName("FUTURES_BTC_RISK_OFF_MOVE_PERCENT")]
    [Range(typeof(decimal), "-100", "100")]
    public decimal BtcRiskOffMovePercent { get; init; } = -1.5m;

    [ConfigurationKeyName("FUTURES_STOP_LOSS_COOLDOWN_MINUTES")]
    [Range(0, 1440)]
    public int StopLossCooldownMinutes { get; init; } = 30;
}
