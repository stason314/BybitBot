using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.App;

public sealed class FuturesBacktestOptions
{
    [ConfigurationKeyName("FUTURES_BACKTEST_DAYS")]
    [Range(1, 365)]
    public int Days { get; init; } = 90;

    [ConfigurationKeyName("FUTURES_BACKTEST_SYMBOLS")]
    [Range(1, 200)]
    public int Symbols { get; init; } = 20;

    [ConfigurationKeyName("FUTURES_BACKTEST_MAX_CONCURRENCY")]
    [Range(1, 20)]
    public int MaxConcurrency { get; init; } = 2;

    [ConfigurationKeyName("FUTURES_BACKTEST_MODE")]
    public string Mode { get; init; } = "ScoreBasedRouter";

    [ConfigurationKeyName("FUTURES_BACKTEST_TURTLE_ALLOWED_WEEKDAYS")]
    public string TurtleAllowedWeekdays { get; init; } = string.Empty;

    [ConfigurationKeyName("FUTURES_BACKTEST_TURTLE_ALLOWED_NY_HOURS")]
    public string TurtleAllowedNyHours { get; init; } = string.Empty;

    [ConfigurationKeyName("FUTURES_BACKTEST_RUN_NY_BOUNCE_ROUTER")]
    public bool RunNyBounceRouter { get; init; } = true;

    [ConfigurationKeyName("FUTURES_BACKTEST_TURTLE_ALLOWED_DIRECTIONS")]
    public string TurtleAllowedDirections { get; init; } = string.Empty;

    [ConfigurationKeyName("FUTURES_BACKTEST_TURTLE_ALLOWED_SYSTEMS")]
    public string TurtleAllowedSystems { get; init; } = string.Empty;

    [ConfigurationKeyName("FUTURES_BACKTEST_TURTLE_RISK_PER_UNIT_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal TurtleRiskPerUnitPercent { get; init; } = 0m;

    [ConfigurationKeyName("FUTURES_BACKTEST_ENTRY_NOTIONAL_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal EntryNotionalUsdt { get; init; } = 100m;

    [ConfigurationKeyName("FUTURES_BACKTEST_TAKER_FEE_PERCENT")]
    [Range(typeof(decimal), "0", "10")]
    public decimal TakerFeePercent { get; init; } = 0.06m;

    [ConfigurationKeyName("FUTURES_BACKTEST_MAKER_FEE_PERCENT")]
    [Range(typeof(decimal), "0", "10")]
    public decimal MakerFeePercent { get; init; } = 0.01m;

    [ConfigurationKeyName("FUTURES_BACKTEST_SLIPPAGE_PERCENT")]
    [Range(typeof(decimal), "0", "10")]
    public decimal SlippagePercent { get; init; } = 0.05m;

    [ConfigurationKeyName("FUTURES_BACKTEST_FUNDING_PERCENT_PER_8H")]
    [Range(typeof(decimal), "0", "10")]
    public decimal FundingPercentPer8h { get; init; } = 0.01m;

    [ConfigurationKeyName("FUTURES_BACKTEST_INITIAL_EQUITY_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal InitialEquityUsdt { get; init; } = 1000m;

    [ConfigurationKeyName("FUTURES_BACKTEST_LEVERAGE")]
    [Range(typeof(decimal), "1", "1000")]
    public decimal Leverage { get; init; } = 2m;

    [ConfigurationKeyName("FUTURES_BACKTEST_MIN_LIQUIDATION_BUFFER_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinLiquidationBufferPercent { get; init; } = 15m;

    [ConfigurationKeyName("FUTURES_BACKTEST_MAX_TRADE_LOSS_EQUITY_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxTradeLossEquityPercent { get; init; } = 2m;

    [ConfigurationKeyName("FUTURES_BACKTEST_MAX_PROJECTED_DRAWDOWN_EQUITY_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MaxProjectedDrawdownEquityPercent { get; init; } = 30m;

    [ConfigurationKeyName("FUTURES_BACKTEST_CANDLE_CACHE_ENABLED")]
    public bool CandleCacheEnabled { get; init; } = true;

    [ConfigurationKeyName("FUTURES_BACKTEST_CANDLE_CACHE_PATH")]
    public string CandleCachePath { get; init; } = "/app/data/backtest-candles";

    [ConfigurationKeyName("FUTURES_BACKTEST_APPLIED_SETTINGS_PATH")]
    public string AppliedSettingsPath { get; init; } = "/app/data/backtest-settings.json";
}
