using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.App;

public sealed class StrategyRoutingOptions
{
    [ConfigurationKeyName("SIGNAL_SELECTION_MODE")]
    public SignalSelectionMode SignalSelectionMode { get; init; } = SignalSelectionMode.ScoreBased;

    [ConfigurationKeyName("STRATEGY_MIN_SCORE")]
    [Range(typeof(decimal), "0", "100")]
    public decimal StrategyMinScore { get; init; } = 75m;

    [ConfigurationKeyName("STRATEGY_MIN_CONFIDENCE")]
    [Range(typeof(decimal), "0", "1")]
    public decimal StrategyMinConfidence { get; init; } = 0.65m;

    [ConfigurationKeyName("MIN_SCORE_DIFFERENCE")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinScoreDifference { get; init; } = 15m;

    [ConfigurationKeyName("ALLOW_CONFLICTED_SIGNALS")]
    public bool AllowConflictedSignals { get; init; } = false;

    [ConfigurationKeyName("NY_RANGE_MODE")]
    public NyRangeMode NyRangeMode { get; init; } = NyRangeMode.DynamicSessionRange;

    [ConfigurationKeyName("ENABLE_STRATEGY_SYMBOL_GATING")]
    public bool EnableStrategySymbolGating { get; init; } = true;

    [ConfigurationKeyName("LIVE_USE_ELIGIBLE_STRATEGY_GATES_ONLY")]
    public bool LiveUseEligibleStrategyGatesOnly { get; init; } = true;

    [ConfigurationKeyName("LIVE_ELIGIBLE_GATE_SIZE_MULTIPLIER")]
    [Range(typeof(decimal), "0", "100")]
    public decimal LiveEligibleGateSizeMultiplier { get; init; } = 0.5m;

    [ConfigurationKeyName("LIVE_INELIGIBLE_GATE_SIZE_MULTIPLIER")]
    [Range(typeof(decimal), "0", "100")]
    public decimal LiveIneligibleGateSizeMultiplier { get; init; } = 0m;

    [ConfigurationKeyName("NYSWEEP_LIVE_TRADING_ENABLED")]
    public bool NySweepLiveTradingEnabled { get; init; } = false;

    [ConfigurationKeyName("LIVE_ELIGIBLE_DIRECTIONS")]
    public string LiveEligibleDirections { get; init; } = "Long";

    [ConfigurationKeyName("NY_LIVE_ALLOWED_HOURS")]
    public string LiveAllowedHours { get; init; } = "10,11";

    [ConfigurationKeyName("MIN_TRADES_FOR_STRATEGY_SYMBOL_GATING")]
    [Range(1, 10000)]
    public int MinTradesForStrategySymbolGating { get; init; } = 5;

    [ConfigurationKeyName("MIN_PROFIT_FACTOR_TO_ENABLE")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal MinProfitFactorToEnable { get; init; } = 1.15m;

    [ConfigurationKeyName("MIN_AVERAGE_R_TO_ENABLE")]
    [Range(typeof(decimal), "-100", "100")]
    public decimal MinAverageRToEnable { get; init; } = 0.05m;

    [ConfigurationKeyName("MIN_OOS_TRADES_FOR_STRATEGY_SYMBOL_GATING")]
    [Range(1, 10000)]
    public int MinOosTradesForStrategySymbolGating { get; init; } = 5;

    [ConfigurationKeyName("MIN_ROBUSTNESS_WINDOWS_TO_ENABLE")]
    [Range(1, 12)]
    public int MinRobustnessWindowsToEnable { get; init; } = 2;

    [ConfigurationKeyName("ROBUSTNESS_WINDOW_DAYS")]
    [Range(1, 365)]
    public int RobustnessWindowDays { get; init; } = 15;

    [ConfigurationKeyName("MIN_ROBUSTNESS_TRADES_PER_WINDOW")]
    [Range(1, 10000)]
    public int MinRobustnessTradesPerWindow { get; init; } = 2;

    [ConfigurationKeyName("MIN_ROBUSTNESS_PROFIT_FACTOR_TO_ENABLE")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal MinRobustnessProfitFactorToEnable { get; init; } = 1m;

    [ConfigurationKeyName("MIN_ROBUSTNESS_AVERAGE_R_TO_ENABLE")]
    [Range(typeof(decimal), "-100", "100")]
    public decimal MinRobustnessAverageRToEnable { get; init; } = 0m;

    [ConfigurationKeyName("MIN_OOS_PROFIT_FACTOR_TO_ENABLE")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal MinOosProfitFactorToEnable { get; init; } = 1.05m;

    [ConfigurationKeyName("MIN_OOS_AVERAGE_R_TO_ENABLE")]
    [Range(typeof(decimal), "-100", "100")]
    public decimal MinOosAverageRToEnable { get; init; } = 0m;

    [ConfigurationKeyName("MIN_OOS_NET_PNL_TO_ENABLE")]
    [Range(typeof(decimal), "-999999999", "999999999")]
    public decimal MinOosNetPnlToEnable { get; init; } = 500m;

    [ConfigurationKeyName("MAX_OOS_DRAWDOWN_PERCENT_TO_ENABLE")]
    [Range(typeof(decimal), "0", "10000")]
    public decimal MaxOosDrawdownPercentToEnable { get; init; } = 30m;

    [ConfigurationKeyName("MAX_OOS_LARGEST_WIN_GROSS_PROFIT_PERCENT_TO_ENABLE")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxOosLargestWinGrossProfitPercentToEnable { get; init; } = 70m;

    [ConfigurationKeyName("TURTLE_MAX_OOS_LARGEST_WIN_GROSS_PROFIT_PERCENT_TO_ENABLE")]
    [Range(typeof(decimal), "0", "100")]
    public decimal TurtleMaxOosLargestWinGrossProfitPercentToEnable { get; init; } = 85m;

    [ConfigurationKeyName("MIN_OOS_MEDIAN_R_TO_ENABLE")]
    [Range(typeof(decimal), "-100", "100")]
    public decimal MinOosMedianRToEnable { get; init; } = 0m;

    [ConfigurationKeyName("DISABLE_AFTER_NEGATIVE_TRADES")]
    [Range(1, 10000)]
    public int DisableAfterNegativeTrades { get; init; } = 100;

    [ConfigurationKeyName("SHADOW_TRADING_ENABLED")]
    public bool ShadowTradingEnabled { get; init; } = true;

    [ConfigurationKeyName("SHADOW_TURTLE_ENABLED")]
    public bool ShadowTurtleEnabled { get; init; } = true;

    [ConfigurationKeyName("SHADOW_DISABLED_PATTERNS_ENABLED")]
    public bool ShadowDisabledPatternsEnabled { get; init; } = true;

    [ConfigurationKeyName("MAX_OPEN_SWEEP_POSITIONS")]
    [Range(1, 100)]
    public int MaxOpenSweepPositions { get; init; } = 3;

    [ConfigurationKeyName("MAX_OPEN_TURTLE_POSITIONS")]
    [Range(1, 100)]
    public int MaxOpenTurtlePositions { get; init; } = 3;

    [ConfigurationKeyName("MAX_RISK_PER_STRATEGY_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxRiskPerStrategyPercent { get; init; } = 2m;

    [ConfigurationKeyName("ALLOW_MULTIPLE_POSITIONS_PER_SYMBOL")]
    public bool AllowMultiplePositionsPerSymbol { get; init; } = false;
}

public sealed class TurtleTrendOptions
{
    [ConfigurationKeyName("TURTLE_ENABLED")]
    public bool Enabled { get; init; } = true;

    [ConfigurationKeyName("TURTLE_TIMEFRAME")]
    public string Timeframe { get; init; } = "60";

    [ConfigurationKeyName("TURTLE_ENTRY_FAST_PERIOD")]
    [Range(2, 500)]
    public int EntryFastPeriod { get; init; } = 20;

    [ConfigurationKeyName("TURTLE_ENTRY_SLOW_PERIOD")]
    [Range(2, 500)]
    public int EntrySlowPeriod { get; init; } = 55;

    [ConfigurationKeyName("TURTLE_EXIT_FAST_PERIOD")]
    [Range(2, 500)]
    public int ExitFastPeriod { get; init; } = 10;

    [ConfigurationKeyName("TURTLE_EXIT_SLOW_PERIOD")]
    [Range(2, 500)]
    public int ExitSlowPeriod { get; init; } = 20;

    [ConfigurationKeyName("TURTLE_ATR_PERIOD")]
    [Range(2, 500)]
    public int AtrPeriod { get; init; } = 20;

    [ConfigurationKeyName("TURTLE_STOP_ATR_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "100")]
    public decimal StopAtrMultiplier { get; init; } = 2m;

    [ConfigurationKeyName("TURTLE_MIN_ADX")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinAdx { get; init; } = 22m;

    [ConfigurationKeyName("TURTLE_REQUIRE_VOLUME_CONFIRMATION")]
    public bool RequireVolumeConfirmation { get; init; } = true;

    [ConfigurationKeyName("TURTLE_VOLUME_MULTIPLIER")]
    [Range(typeof(decimal), "0", "100")]
    public decimal VolumeMultiplier { get; init; } = 1.3m;

    [ConfigurationKeyName("TURTLE_USE_BTC_FILTER")]
    public bool UseBtcFilter { get; init; } = true;

    [ConfigurationKeyName("TURTLE_USE_FIXED_TP")]
    public bool UseFixedTakeProfit { get; init; } = false;

    [ConfigurationKeyName("TURTLE_USE_CHANNEL_EXIT")]
    public bool UseChannelExit { get; init; } = true;

    [ConfigurationKeyName("TURTLE_USE_TRAILING_ATR_STOP")]
    public bool UseTrailingAtrStop { get; init; } = false;

    [ConfigurationKeyName("TURTLE_USE_PROFIT_LOCK")]
    public bool UseProfitLock { get; init; } = false;

    [ConfigurationKeyName("TURTLE_BREAKEVEN_TRIGGER_R")]
    [Range(typeof(decimal), "0", "100")]
    public decimal BreakevenTriggerR { get; init; } = 1m;

    [ConfigurationKeyName("TURTLE_LOCK_TRIGGER_R")]
    [Range(typeof(decimal), "0", "100")]
    public decimal LockTriggerR { get; init; } = 2m;

    [ConfigurationKeyName("TURTLE_LOCK_PROFIT_R")]
    [Range(typeof(decimal), "0", "100")]
    public decimal LockProfitR { get; init; } = 0.8m;

    [ConfigurationKeyName("TURTLE_ATR_TRAIL_TRIGGER_R")]
    [Range(typeof(decimal), "0", "100")]
    public decimal AtrTrailTriggerR { get; init; } = 3m;

    [ConfigurationKeyName("TURTLE_ATR_TRAIL_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "100")]
    public decimal AtrTrailMultiplier { get; init; } = 2m;

    [ConfigurationKeyName("TURTLE_USE_PYRAMIDING")]
    public bool UsePyramiding { get; init; } = true;

    [ConfigurationKeyName("TURTLE_MAX_UNITS")]
    [Range(1, 20)]
    public int MaxUnits { get; init; } = 4;

    [ConfigurationKeyName("TURTLE_ADD_ATR_INTERVAL")]
    [Range(typeof(decimal), "0.00000001", "100")]
    public decimal AddAtrInterval { get; init; } = 0.5m;

    [ConfigurationKeyName("TURTLE_RISK_PER_UNIT_PERCENT")]
    [Range(typeof(decimal), "0.00000001", "100")]
    public decimal RiskPerUnitPercent { get; init; } = 1m;

    [ConfigurationKeyName("TURTLE_POINT_VALUE_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal PointValueUsdt { get; init; } = 1m;

    [ConfigurationKeyName("TURTLE_MAX_UNITS_CORRELATED")]
    [Range(1, 100)]
    public int MaxUnitsCorrelated { get; init; } = 6;

    [ConfigurationKeyName("TURTLE_MAX_UNITS_TIGHT")]
    [Range(1, 100)]
    public int MaxUnitsTight { get; init; } = 10;

    [ConfigurationKeyName("TURTLE_MAX_UNITS_DIRECTION")]
    [Range(1, 100)]
    public int MaxUnitsDirection { get; init; } = 12;
}
