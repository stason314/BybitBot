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

    [ConfigurationKeyName("PAIR_SCORE_CAPITAL_ALLOCATION_ENABLED")]
    public bool PairScoreCapitalAllocationEnabled { get; init; } = true;

    [ConfigurationKeyName("PAIR_SCORE_HOT_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal PairScoreHotMultiplier { get; init; } = 1.4m;

    [ConfigurationKeyName("PAIR_SCORE_GOOD_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal PairScoreGoodMultiplier { get; init; } = 1.15m;

    [ConfigurationKeyName("PAIR_SCORE_NEUTRAL_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal PairScoreNeutralMultiplier { get; init; } = 1m;

    [ConfigurationKeyName("PAIR_SCORE_AVOID_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal PairScoreAvoidMultiplier { get; init; } = 0.5m;

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

    [ConfigurationKeyName("MIN_STRATEGY_SCORE")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinStrategyScore { get; init; } = 65m;

    [ConfigurationKeyName("STRATEGY_MIN_CONFIDENCE")]
    [Range(typeof(decimal), "0", "1")]
    public decimal StrategyMinConfidence { get; init; } = 0.6m;

    [ConfigurationKeyName("MIN_PHASE_CONFIDENCE")]
    [Range(typeof(decimal), "0", "1")]
    public decimal MinPhaseConfidence { get; init; } = 0.6m;

    [ConfigurationKeyName("STRATEGY_SWITCH_COOLDOWN_MINUTES")]
    [Range(0, 10080)]
    public int StrategySwitchCooldownMinutes { get; init; } = 30;

    [ConfigurationKeyName("STRATEGY_CONFIRMATION_CANDLES")]
    [Range(1, 100)]
    public int StrategyConfirmationCandles { get; init; } = 2;

    [ConfigurationKeyName("PHASE_CONFIRMATION_CANDLES")]
    [Range(1, 100)]
    public int PhaseConfirmationCandles { get; init; } = 2;

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

    [ConfigurationKeyName("BREAKOUT_RISK_REWARD")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal BreakoutRiskReward { get; init; } = 2m;

    [ConfigurationKeyName("BREAKOUT_MAX_DISTANCE_FROM_EMA_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal BreakoutMaxDistanceFromEmaPercent { get; init; } = 5m;

    [ConfigurationKeyName("BREAKOUT_USE_RETEST")]
    public bool BreakoutUseRetest { get; init; } = true;

    [ConfigurationKeyName("BREAKOUT_CAPITAL_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal BreakoutCapitalPercent { get; init; } = 20m;

    [ConfigurationKeyName("TREND_EMA_FAST")]
    [Range(2, 500)]
    public int TrendEmaFast { get; init; } = 20;

    [ConfigurationKeyName("TREND_EMA_SLOW")]
    [Range(2, 500)]
    public int TrendEmaSlow { get; init; } = 50;

    [ConfigurationKeyName("EMA_FAST")]
    [Range(2, 500)]
    public int EmaFast { get; init; } = 20;

    [ConfigurationKeyName("EMA_SLOW")]
    [Range(2, 500)]
    public int EmaSlow { get; init; } = 50;

    [ConfigurationKeyName("TREND_MAX_DISTANCE_FROM_EMA_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal TrendMaxDistanceFromEmaPercent { get; init; } = 4m;

    [ConfigurationKeyName("TREND_TRAILING_STOP_ATR_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal TrendTrailingStopAtrMultiplier { get; init; } = 1.5m;

    [ConfigurationKeyName("TREND_PARTIAL_TAKE_PROFIT_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal TrendPartialTakeProfitPercent { get; init; } = 50m;

    [ConfigurationKeyName("TREND_PARTIAL_TAKE_PROFIT_AT_R")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal TrendPartialTakeProfitAtR { get; init; } = 1.5m;

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

    [ConfigurationKeyName("MIN_EXPECTED_PROFIT_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinExpectedProfitPercent { get; init; } = 0.7m;

    [ConfigurationKeyName("BOT_LOOP_INTERVAL_SECONDS")]
    [Range(1, 3600)]
    public int BotLoopIntervalSeconds { get; init; } = 10;

    [ConfigurationKeyName("PAPER_INITIAL_USDT")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal PaperInitialUsdt { get; init; } = 100m;

    [ConfigurationKeyName("PAPER_INITIAL_BASE_ASSET_QUANTITY")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal PaperInitialBaseAssetQuantity { get; init; } = 0m;

    [ConfigurationKeyName("PAPER_BOOTSTRAP_INVENTORY_ENABLED")]
    public bool PaperBootstrapInventoryEnabled { get; init; } = false;

    [ConfigurationKeyName("CANDLE_INTERVAL")]
    public string CandleInterval { get; init; } = "60";

    [ConfigurationKeyName("BIG_RED_CANDLE_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal BigRedCandlePercent { get; init; } = 4m;

    [ConfigurationKeyName("DUMP_MOVE_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal DumpMovePercent { get; init; } = 6m;

    [ConfigurationKeyName("DUMP_LOOKBACK_CANDLES")]
    [Range(1, 100)]
    public int DumpLookbackCandles { get; init; } = 3;

    [ConfigurationKeyName("DUMP_PAUSE_CANDLES")]
    [Range(1, 1000)]
    public int DumpPauseCandles { get; init; } = 4;

    [ConfigurationKeyName("CANCEL_GRID_BUYS_ON_DUMP")]
    public bool CancelGridBuysOnDump { get; init; } = true;

    [ConfigurationKeyName("GRID_DYNAMIC_STEP_ENABLED")]
    public bool GridDynamicStepEnabled { get; init; } = true;

    [ConfigurationKeyName("GRID_STEP_ATR_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal GridStepAtrMultiplier { get; init; } = 0.2m;

    [ConfigurationKeyName("GRID_MIN_STEP_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal GridMinStepPercent { get; init; } = 1m;

    [ConfigurationKeyName("GRID_MAX_STEP_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal GridMaxStepPercent { get; init; } = 3m;

    [ConfigurationKeyName("GRID_CANCEL_BUYS_ON_DUMP")]
    public bool GridCancelBuysOnDump { get; init; } = true;

    [ConfigurationKeyName("GRID_HANDOFF_TO_BREAKOUT")]
    public bool GridHandoffToBreakout { get; init; } = true;

    [ConfigurationKeyName("ENABLE_BREAKOUT_HANDOFF")]
    public bool EnableBreakoutHandoff { get; init; } = true;

    [ConfigurationKeyName("BTD_REQUIRE_UPTREND")]
    public bool BtdRequireUptrend { get; init; } = true;

    [ConfigurationKeyName("BTD_MAX_DISTANCE_FROM_EMA_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal BtdMaxDistanceFromEmaPercent { get; init; } = 3m;

    [ConfigurationKeyName("BTD_MIN_PULLBACK_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal BtdMinPullbackPercent { get; init; } = 2m;

    [ConfigurationKeyName("BTD_BLOCK_ON_BTC_RISK_OFF")]
    public bool BtdBlockOnBtcRiskOff { get; init; } = true;

    [ConfigurationKeyName("PROFIT_PROTECTION_ENABLED")]
    public bool ProfitProtectionEnabled { get; init; } = true;

    [ConfigurationKeyName("PROFIT_PROTECTION_TRIGGER_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal ProfitProtectionTriggerPercent { get; init; } = 0.8m;

    [ConfigurationKeyName("TRAILING_STOP_ENABLED")]
    public bool TrailingStopEnabled { get; init; } = true;

    [ConfigurationKeyName("TRAILING_STOP_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal TrailingStopPercent { get; init; } = 0.45m;

    [ConfigurationKeyName("PARTIAL_TAKE_PROFIT_ENABLED")]
    public bool PartialTakeProfitEnabled { get; init; } = true;

    [ConfigurationKeyName("PARTIAL_TAKE_PROFIT_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal PartialTakeProfitPercent { get; init; } = 50m;

    [ConfigurationKeyName("FAST_PROTECTIVE_EXIT_ENABLED")]
    public bool FastProtectiveExitEnabled { get; init; } = true;

    [ConfigurationKeyName("FAST_PROTECTIVE_EXIT_TRIGGER_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal FastProtectiveExitTriggerPercent { get; init; } = 0.8m;

    [ConfigurationKeyName("FAST_PROTECTIVE_EXIT_FLOOR_PERCENT")]
    [Range(typeof(decimal), "-100", "1000")]
    public decimal FastProtectiveExitFloorPercent { get; init; } = 0.2m;

    [ConfigurationKeyName("REDUCE_ONLY_FORCE_EXIT_ON_DRAWDOWN")]
    public bool ReduceOnlyForceExitOnDrawdown { get; init; } = true;

    [ConfigurationKeyName("REDUCE_ONLY_FORCE_EXIT_DRAWDOWN_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal ReduceOnlyForceExitDrawdownPercent { get; init; } = 2m;

    [ConfigurationKeyName("ENABLE_NO_TRADE_REASON_TRACKING")]
    public bool EnableNoTradeReasonTracking { get; init; } = true;

    [ConfigurationKeyName("ENABLE_STRATEGY_PERFORMANCE_ANALYTICS")]
    public bool EnableStrategyPerformanceAnalytics { get; init; } = true;

    [ConfigurationKeyName("AGGRESSIVE_MODE_ENABLED")]
    public bool AggressiveModeEnabled { get; init; } = true;

    [ConfigurationKeyName("AGGRESSIVE_MODE_COOLDOWN_MINUTES")]
    [Range(1, 1440)]
    public int AggressiveModeCooldownMinutes { get; init; } = 30;

    [ConfigurationKeyName("AGGRESSIVE_STOP_LOSS_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal AggressiveStopLossPercent { get; init; } = 2m;

    [ConfigurationKeyName("AUTO_RECOMMENDATION_APPLY_INTERVAL_MINUTES")]
    [Range(1, 1440)]
    public int AutoRecommendationApplyIntervalMinutes { get; init; } = 5;

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
