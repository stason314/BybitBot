using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.Strategy;

public sealed class GridOptions
{
    [ConfigurationKeyName("SYMBOL")]
    [Required]
    public string Symbol { get; init; } = "BILLUSDT";

    [ConfigurationKeyName("CATEGORY")]
    [Required]
    public string Category { get; init; } = "spot";

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

    [ConfigurationKeyName("STOP_LOWER_PRICE")]
    public decimal StopLowerPrice { get; init; } = 0.09m;

    [ConfigurationKeyName("STOP_UPPER_PRICE")]
    public decimal StopUpperPrice { get; init; } = 0.16m;

    [ConfigurationKeyName("MARKET_FILTER_ENABLED")]
    public bool MarketFilterEnabled { get; init; } = true;

    [ConfigurationKeyName("ADX_MAX")]
    [Range(typeof(decimal), "0", "100")]
    public decimal AdxMax { get; init; } = 22m;

    [ConfigurationKeyName("BTC_FILTER_ENABLED")]
    public bool BtcFilterEnabled { get; init; } = true;

    [ConfigurationKeyName("BTC_MAX_MOVE_PERCENT")]
    [Range(typeof(decimal), "0", "1000")]
    public decimal BtcMaxMovePercent { get; init; } = 2.5m;

    [ConfigurationKeyName("BTC_LOOKBACK_CANDLES")]
    [Range(1, 100)]
    public int BtcLookbackCandles { get; init; } = 3;

    [ConfigurationKeyName("FEE_PERCENT")]
    [Range(typeof(decimal), "0", "100")]
    public decimal FeePercent { get; init; } = 0.1m;

    [ConfigurationKeyName("BOT_LOOP_INTERVAL_SECONDS")]
    [Range(1, 3600)]
    public int BotLoopIntervalSeconds { get; init; } = 10;

    [ConfigurationKeyName("PAPER_INITIAL_USDT")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal PaperInitialUsdt { get; init; } = 1000m;

    [ConfigurationKeyName("PAPER_INITIAL_BASE_ASSET_QUANTITY")]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal PaperInitialBaseAssetQuantity { get; init; } = 0m;

    [ConfigurationKeyName("CANDLE_INTERVAL")]
    public string CandleInterval { get; init; } = "60";
}
