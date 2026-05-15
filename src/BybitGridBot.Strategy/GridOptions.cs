using System.ComponentModel.DataAnnotations;
using BybitGridBot.Domain;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.Strategy;

public sealed class GridOptions
{
    [ConfigurationKeyName("BOT_TYPE")]
    public BotType BotType { get; init; } = BotType.Auto;

    [ConfigurationKeyName("SYMBOL")]
    [Required]
    public string Symbol { get; init; } = "BILLUSDT";

    [ConfigurationKeyName("CATEGORY")]
    [Required]
    public string Category { get; init; } = "spot";

    [ConfigurationKeyName("SPOT_ONLY")]
    public bool SpotOnly { get; init; } = true;

    [ConfigurationKeyName("GRID_LOWER_PRICE")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal LowerPrice { get; init; } = 0.10m;

    [ConfigurationKeyName("GRID_UPPER_PRICE")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal UpperPrice { get; init; } = 0.15m;

    [ConfigurationKeyName("GRID_STEP")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal Step { get; init; } = 0.01m;

    [ConfigurationKeyName("ORDER_SIZE_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal OrderSizeUsdt { get; init; } = 20m;

    [ConfigurationKeyName("MIN_ORDER_SIZE_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MinOrderSizeUsdt { get; init; } = 5m;

    [ConfigurationKeyName("MIN_NET_PROFIT_USDT")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal MinNetProfitUsdt { get; init; } = 0m;

    [ConfigurationKeyName("DYNAMIC_ORDER_SIZE_ENABLED")]
    public bool DynamicOrderSizeEnabled { get; init; } = false;

    [ConfigurationKeyName("DYNAMIC_LOWER_ORDER_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal DynamicLowerOrderMultiplier { get; init; } = 1.5m;

    [ConfigurationKeyName("DYNAMIC_UPPER_ORDER_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal DynamicUpperOrderMultiplier { get; init; } = 1m;

    [ConfigurationKeyName("DAILY_TAKE_PROFIT_USDT")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal DailyTakeProfitUsdt { get; init; } = 0m;

    [ConfigurationKeyName("DAILY_TAKE_PROFIT_ORDER_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal DailyTakeProfitOrderMultiplier { get; init; } = 0.5m;

    [ConfigurationKeyName("STOP_LOWER_PRICE")]
    public decimal StopLowerPrice { get; init; } = 0.09m;

    [ConfigurationKeyName("STOP_UPPER_PRICE")]
    public decimal StopUpperPrice { get; init; } = 0.16m;

    [ConfigurationKeyName("MARKET_FILTER_ENABLED")]
    public bool MarketFilterEnabled { get; init; } = true;

    [ConfigurationKeyName("ADX_MAX")]
    [Range(typeof(decimal), "0", "100")]
    public decimal AdxMax { get; init; } = 22m;

    [ConfigurationKeyName("RANGE_ADX_MAX")]
    [Range(typeof(decimal), "0", "100")]
    public decimal RangeAdxMax { get; init; } = 20m;

    [ConfigurationKeyName("BREAKOUT_ADX_MIN")]
    [Range(typeof(decimal), "0", "100")]
    public decimal BreakoutAdxMin { get; init; } = 25m;

    [ConfigurationKeyName("TREND_ADX_MIN")]
    [Range(typeof(decimal), "0", "100")]
    public decimal TrendAdxMin { get; init; } = 25m;

    [ConfigurationKeyName("ADX_PERIOD")]
    [Range(2, 200)]
    public int AdxPeriod { get; init; } = 14;

    [ConfigurationKeyName("ATR_PERIOD")]
    [Range(2, 200)]
    public int AtrPeriod { get; init; } = 14;

    [ConfigurationKeyName("VOLUME_SMA_PERIOD")]
    [Range(2, 200)]
    public int VolumeSmaPeriod { get; init; } = 20;

    [ConfigurationKeyName("VOLUME_SPIKE_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal VolumeSpikeMultiplier { get; init; } = 2m;

    [ConfigurationKeyName("ATR_SPIKE_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal AtrSpikeMultiplier { get; init; } = 2m;

    [ConfigurationKeyName("BTC_FILTER_ENABLED")]
    public bool BtcFilterEnabled { get; init; } = true;

    [ConfigurationKeyName("BTC_MAX_MOVE_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal BtcMaxMovePercent { get; init; } = 2.5m;

    [ConfigurationKeyName("BTC_LOOKBACK_CANDLES")]
    [Range(1, 100)]
    public int BtcLookbackCandles { get; init; } = 3;

    [ConfigurationKeyName("STRATEGY_MIN_SCORE")]
    [Range(typeof(decimal), "0", "100")]
    public decimal StrategyMinScore { get; init; } = 65m;

    [ConfigurationKeyName("STRATEGY_MIN_CONFIDENCE")]
    [Range(typeof(decimal), "0", "1")]
    public decimal StrategyMinConfidence { get; init; } = 0.6m;

    [ConfigurationKeyName("STRATEGY_SWITCH_COOLDOWN_MINUTES")]
    [Range(0, 10080)]
    public int StrategySwitchCooldownMinutes { get; init; } = 30;

    [ConfigurationKeyName("STRATEGY_CONFIRMATION_CANDLES")]
    [Range(1, 100)]
    public int StrategyConfirmationCandles { get; init; } = 2;

    [ConfigurationKeyName("GRID_CAPITAL_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal GridCapitalPercent { get; init; } = 40m;

    [ConfigurationKeyName("DCA_ORDER_SIZE_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal DcaOrderSizeUsdt { get; init; } = 10m;

    [ConfigurationKeyName("DCA_MAX_POSITION_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal DcaMaxPositionUsdt { get; init; } = 200m;

    [ConfigurationKeyName("DCA_ALLOW_DOWNTREND")]
    public bool DcaAllowDowntrend { get; init; } = false;

    [ConfigurationKeyName("DCA_CAPITAL_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal DcaCapitalPercent { get; init; } = 20m;

    [ConfigurationKeyName("BTD_DIP_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal BtdDipPercent { get; init; } = 3m;

    [ConfigurationKeyName("BTD_MIN_TREND_SCORE")]
    [Range(typeof(decimal), "0", "100")]
    public decimal BtdMinTrendScore { get; init; } = 65m;

    [ConfigurationKeyName("BTD_CAPITAL_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal BtdCapitalPercent { get; init; } = 20m;

    [ConfigurationKeyName("BREAKOUT_CONFIRMATION_CANDLES")]
    [Range(1, 100)]
    public int BreakoutConfirmationCandles { get; init; } = 2;

    [ConfigurationKeyName("BREAKOUT_VOLUME_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal BreakoutVolumeMultiplier { get; init; } = 1.8m;

    [ConfigurationKeyName("BREAKOUT_ATR_STOP_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal BreakoutAtrStopMultiplier { get; init; } = 1.5m;

    [ConfigurationKeyName("BREAKOUT_CAPITAL_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal BreakoutCapitalPercent { get; init; } = 20m;

    [ConfigurationKeyName("TREND_EMA_FAST")]
    [Range(2, 500)]
    public int TrendEmaFast { get; init; } = 20;

    [ConfigurationKeyName("TREND_EMA_SLOW")]
    [Range(2, 500)]
    public int TrendEmaSlow { get; init; } = 50;

    [ConfigurationKeyName("TREND_CAPITAL_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal TrendCapitalPercent { get; init; } = 20m;

    [ConfigurationKeyName("MIN_USDT_RESERVE_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinUsdtReservePercent { get; init; } = 20m;

    [ConfigurationKeyName("MAX_TOTAL_EXPOSURE_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxTotalExposurePercent { get; init; } = 80m;

    [ConfigurationKeyName("FEE_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal FeePercent { get; init; } = 0.1m;

    [ConfigurationKeyName("SLIPPAGE_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal SlippagePercent { get; init; } = 0.05m;

    [ConfigurationKeyName("BOT_LOOP_INTERVAL_SECONDS")]
    [Range(1, 3600)]
    public int BotLoopIntervalSeconds { get; init; } = 10;

    [ConfigurationKeyName("PAPER_INITIAL_USDT")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal PaperInitialUsdt { get; init; } = 1000m;

    [ConfigurationKeyName("PAPER_INITIAL_BASE_ASSET_QUANTITY")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal PaperInitialBaseAssetQuantity { get; init; } = 0m;

    [ConfigurationKeyName("PAPER_BOOTSTRAP_INVENTORY_ENABLED")]
    public bool PaperBootstrapInventoryEnabled { get; init; } = true;

    [ConfigurationKeyName("CANDLE_INTERVAL")]
    public string CandleInterval { get; init; } = "60";

    [ConfigurationKeyName("AUTO_RECENTER_ENABLED")]
    public bool AutoRecenterEnabled { get; init; } = false;

    [ConfigurationKeyName("AUTO_RECENTER_CANDLE_INTERVAL")]
    public string AutoRecenterCandleInterval { get; init; } = "1";

    [ConfigurationKeyName("AUTO_RECENTER_LOOKBACK_CANDLES")]
    [Range(5, 1000)]
    public int AutoRecenterLookbackCandles { get; init; } = 60;

    [ConfigurationKeyName("AUTO_RECENTER_PADDING_STEPS")]
    [Range(0, 1000)]
    public int AutoRecenterPaddingSteps { get; init; } = 2;

    [ConfigurationKeyName("AUTO_RECENTER_MIN_SHIFT_STEPS")]
    [Range(1, 1000)]
    public int AutoRecenterMinShiftSteps { get; init; } = 2;

    [ConfigurationKeyName("TRAILING_PROTECTION_ENABLED")]
    public bool TrailingProtectionEnabled { get; init; } = true;

    [ConfigurationKeyName("TRAILING_PROTECTION_CANDLE_INTERVAL")]
    public string TrailingProtectionCandleInterval { get; init; } = "1";

    [ConfigurationKeyName("TRAILING_PROTECTION_LOOKBACK_CANDLES")]
    [Range(5, 1000)]
    public int TrailingProtectionLookbackCandles { get; init; } = 120;

    [ConfigurationKeyName("TRAILING_PROTECTION_PUMP_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal TrailingProtectionPumpPercent { get; init; } = 3m;

    [ConfigurationKeyName("TRAILING_PROTECTION_PULLBACK_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal TrailingProtectionPullbackPercent { get; init; } = 1.2m;
}
