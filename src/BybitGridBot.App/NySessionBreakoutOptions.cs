using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.App;

public sealed class NySessionBreakoutOptions
{
    [ConfigurationKeyName("NY_SESSION_STRATEGY_ENABLED")]
    public bool Enabled { get; init; } = true;

    [ConfigurationKeyName("NY_SESSION_POOL_SIZE")]
    [Range(1, 100)]
    public int PoolSize { get; init; } = 20;

    [ConfigurationKeyName("NY_SESSION_SCAN_LIMIT")]
    [Range(20, 1000)]
    public int ScanLimit { get; init; } = 160;

    [ConfigurationKeyName("NY_SESSION_LOOP_SECONDS")]
    [Range(10, 3600)]
    public int LoopSeconds { get; init; } = 30;

    [ConfigurationKeyName("NY_SESSION_ENTRY_NOTIONAL_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal EntryNotionalUsdt { get; init; } = 25m;

    [ConfigurationKeyName("NY_SESSION_REWARD_RISK")]
    [Range(typeof(decimal), "0.00000001", "100")]
    public decimal RewardRisk { get; init; } = 2m;

    [ConfigurationKeyName("NY_SESSION_MIN_4H_RANGE_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinFourHourRangePercent { get; init; } = 0.2m;

    [ConfigurationKeyName("NY_SESSION_MAX_4H_RANGE_PERCENT")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal MaxFourHourRangePercent { get; init; } = 8m;

    [ConfigurationKeyName("NY_SESSION_NEAR_BOUNDARY_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal NearBoundaryPercent { get; init; } = 0.8m;

    [ConfigurationKeyName("NY_SESSION_ALLOW_LONGS")]
    public bool AllowLongs { get; init; } = true;

    [ConfigurationKeyName("NY_SESSION_ALLOW_SHORTS")]
    public bool AllowShorts { get; init; } = true;

    [ConfigurationKeyName("NY_SESSION_MAX_OPEN_POSITIONS")]
    [Range(1, 100)]
    public int MaxOpenPositions { get; init; } = 5;

    [ConfigurationKeyName("NY_SESSION_MIN_RECLAIM_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinReclaimPercent { get; init; } = 0.03m;

    [ConfigurationKeyName("NY_SESSION_HIGH_BREAKOUT_VOLUME_RATIO")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal HighBreakoutVolumeRatio { get; init; } = 2.2m;

    [ConfigurationKeyName("NY_SESSION_TRUE_BREAKOUT_ADX")]
    [Range(typeof(decimal), "0", "100")]
    public decimal TrueBreakoutAdx { get; init; } = 24m;

    [ConfigurationKeyName("NY_SESSION_BTC_TREND_ADX")]
    [Range(typeof(decimal), "0", "100")]
    public decimal BtcTrendAdx { get; init; } = 26m;

    [ConfigurationKeyName("NY_SESSION_BTC_TREND_MOVE_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal BtcTrendMovePercent { get; init; } = 0.8m;
}
