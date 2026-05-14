namespace BybitGridBot.Strategy;

public sealed class BtdStrategyConfig : DcaStrategyConfig
{
    public int MaxBuys { get; init; } = 3;

    public int MinMinutesBetweenBuys { get; init; } = 10;
}
