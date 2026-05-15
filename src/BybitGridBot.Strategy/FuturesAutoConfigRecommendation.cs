using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class FuturesAutoConfigRecommendation
{
    public FuturesStrategyType StrategyType { get; init; } = FuturesStrategyType.Pause;

    public required string Reason { get; init; }

    public decimal Leverage { get; init; }

    public FuturesMarginMode MarginMode { get; init; } = FuturesMarginMode.Isolated;

    public FuturesPositionMode PositionMode { get; init; } = FuturesPositionMode.OneWay;

    public FuturesDirection Direction { get; init; } = FuturesDirection.LongOnly;

    public decimal MaxNotionalUsdt { get; init; }

    public decimal MaxMarginUsdt { get; init; }

    public decimal StopLossPercent { get; init; }

    public decimal TakeProfitPercent { get; init; }

    public decimal LiquidationBufferPercent { get; init; }

    public bool ReduceOnlyEnabled { get; init; } = true;

    public required string StrategyConfigJson { get; init; }

    public FuturesAutoConfigMetrics Metrics { get; init; } = new();
}

public sealed class FuturesAutoConfigMetrics
{
    public decimal LastPrice { get; init; }

    public decimal MovePercent { get; init; }

    public decimal AtrPercent { get; init; }

    public decimal DrawdownPercent { get; init; }

    public decimal Support { get; init; }

    public decimal Resistance { get; init; }
}
