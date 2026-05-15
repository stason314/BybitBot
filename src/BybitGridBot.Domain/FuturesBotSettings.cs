namespace BybitGridBot.Domain;

public sealed class FuturesBotSettings
{
    public bool Enabled { get; init; } = true;

    public string Symbol { get; init; } = "BTCUSDT";

    public string Category { get; init; } = "linear";

    public FuturesStrategyType StrategyType { get; init; } = FuturesStrategyType.Pause;

    public string StrategyConfigJson { get; init; } = "{}";

    public decimal Leverage { get; init; } = 2m;

    public FuturesMarginMode MarginMode { get; init; } = FuturesMarginMode.Isolated;

    public FuturesPositionMode PositionMode { get; init; } = FuturesPositionMode.OneWay;

    public FuturesDirection Direction { get; init; } = FuturesDirection.LongOnly;

    public decimal MaxNotionalUsdt { get; init; } = 100m;

    public decimal MaxMarginUsdt { get; init; } = 50m;

    public decimal StopLossPercent { get; init; } = 2m;

    public decimal TakeProfitPercent { get; init; } = 4m;

    public decimal LiquidationBufferPercent { get; init; } = 15m;

    public bool ReduceOnlyEnabled { get; init; } = true;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
