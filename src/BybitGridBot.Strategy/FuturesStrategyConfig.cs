using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class FuturesStrategyConfig
{
    public FuturesStrategyType StrategyType { get; init; } = FuturesStrategyType.Pause;

    public FuturesDirection Direction { get; init; } = FuturesDirection.LongOnly;

    public decimal EntrySignalThreshold { get; init; } = 0.6m;

    public decimal ExitSignalThreshold { get; init; } = 0.4m;

    public decimal StopLossPercent { get; init; } = 2m;

    public decimal TakeProfitPercent { get; init; } = 6m;
}
