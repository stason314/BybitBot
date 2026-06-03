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

    [ConfigurationKeyName("MIN_TRADES_FOR_STRATEGY_SYMBOL_GATING")]
    [Range(1, 10000)]
    public int MinTradesForStrategySymbolGating { get; init; } = 50;

    [ConfigurationKeyName("MIN_PROFIT_FACTOR_TO_ENABLE")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal MinProfitFactorToEnable { get; init; } = 1.15m;

    [ConfigurationKeyName("MIN_AVERAGE_R_TO_ENABLE")]
    [Range(typeof(decimal), "-100", "100")]
    public decimal MinAverageRToEnable { get; init; } = 0.05m;

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
    public bool UseTrailingAtrStop { get; init; } = true;

    [ConfigurationKeyName("TURTLE_USE_PYRAMIDING")]
    public bool UsePyramiding { get; init; } = false;

    [ConfigurationKeyName("TURTLE_MAX_UNITS")]
    [Range(1, 20)]
    public int MaxUnits { get; init; } = 2;

    [ConfigurationKeyName("TURTLE_ADD_ATR_INTERVAL")]
    [Range(typeof(decimal), "0.00000001", "100")]
    public decimal AddAtrInterval { get; init; } = 0.5m;

    [ConfigurationKeyName("TURTLE_RISK_PER_UNIT_PERCENT")]
    [Range(typeof(decimal), "0.00000001", "100")]
    public decimal RiskPerUnitPercent { get; init; } = 0.25m;
}
