using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.App;

public sealed class FuturesOptions
{
    [ConfigurationKeyName("FUTURES_ENABLED")]
    public bool Enabled { get; init; } = false;

    [ConfigurationKeyName("FUTURES_TESTNET_ENABLED")]
    public bool TestnetEnabled { get; init; } = false;

    [ConfigurationKeyName("FUTURES_TESTNET_SHORTS_ENABLED")]
    public bool TestnetShortsEnabled { get; init; } = false;

    [ConfigurationKeyName("FUTURES_MAINNET_ENABLED")]
    public bool MainnetEnabled { get; init; } = false;

    [ConfigurationKeyName("FUTURES_MAINNET_ORDER_PLACEMENT_ENABLED")]
    public bool MainnetOrderPlacementEnabled { get; init; } = false;

    [ConfigurationKeyName("FUTURES_CATEGORY")]
    public string Category { get; init; } = "linear";

    [ConfigurationKeyName("FUTURES_USER_STREAM_ENABLED")]
    public bool UserStreamEnabled { get; init; } = true;

    [ConfigurationKeyName("LEVERAGE")]
    [Range(typeof(decimal), "1", "1000")]
    public decimal Leverage { get; init; } = 2m;

    [ConfigurationKeyName("FUTURES_MVP_MAX_LEVERAGE")]
    [Range(typeof(decimal), "1", "1000")]
    public decimal MvpMaxLeverage { get; init; } = 2m;

    [ConfigurationKeyName("FUTURES_MIN_SIZE_ORDER_COUNT")]
    [Range(0, 1000)]
    public int MinSizeOrderCount { get; init; } = 3;

    [ConfigurationKeyName("FUTURES_PAPER_INITIAL_EQUITY_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal PaperInitialEquityUsdt { get; init; } = 1000m;

    [ConfigurationKeyName("FUTURES_AGGRESSIVE_MODE_ENABLED")]
    public bool AggressiveModeEnabled { get; init; } = false;

    [ConfigurationKeyName("FUTURES_AUTO_APPLY_RECOMMENDATION")]
    public bool AutoApplyRecommendation { get; init; } = false;

    [ConfigurationKeyName("FUTURES_AGGRESSIVE_MODE_KIND")]
    public string AggressiveModeKind { get; init; } = "normal";

    [ConfigurationKeyName("FUTURES_AGGRESSIVE_ENTRY_MULTIPLIER")]
    [Range(typeof(decimal), "0.00000001", "1000")]
    public decimal AggressiveEntryMultiplier { get; init; } = 1.5m;

    [ConfigurationKeyName("MARGIN_MODE")]
    public string MarginMode { get; init; } = "isolated";

    [ConfigurationKeyName("POSITION_MODE")]
    public string PositionMode { get; init; } = "oneway";

    [ConfigurationKeyName("MAX_NOTIONAL_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MaxNotionalUsdt { get; init; } = 100m;

    [ConfigurationKeyName("MAX_MARGIN_USDT")]
    [Range(typeof(decimal), "0.00000001", "999999999")]
    public decimal MaxMarginUsdt { get; init; } = 50m;

    [ConfigurationKeyName("MIN_LIQUIDATION_BUFFER_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal MinLiquidationBufferPercent { get; init; } = 15m;

    [ConfigurationKeyName("STOP_LOSS_REQUIRED")]
    public bool StopLossRequired { get; init; } = true;
}
