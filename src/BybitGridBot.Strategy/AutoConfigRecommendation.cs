using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class AutoConfigRecommendation
{
    public TradingStrategyType StrategyType { get; init; } = TradingStrategyType.Grid;

    public required string Reason { get; init; }

    public decimal LowerPrice { get; init; }

    public decimal UpperPrice { get; init; }

    public decimal Step { get; init; }

    public decimal OrderSizeUsdt { get; init; }

    public decimal StopLowerPrice { get; init; }

    public decimal StopUpperPrice { get; init; }

    public required string StrategyConfigJson { get; init; }

    public AutoConfigMetrics Metrics { get; init; } = new();
}

public sealed class AutoConfigMetrics
{
    public decimal AtrPercent { get; init; }

    public decimal DrawdownPercent { get; init; }

    public decimal Support { get; init; }

    public decimal Resistance { get; init; }

    public decimal LastPrice { get; init; }
}
