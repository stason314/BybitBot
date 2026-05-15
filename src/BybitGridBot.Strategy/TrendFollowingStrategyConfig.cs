namespace BybitGridBot.Strategy;

public sealed class TrendFollowingStrategyConfig
{
    public decimal? OrderSizeUsdt { get; init; }

    public decimal? TrendOrderSizeUsdt { get; init; }

    public int CooldownMinutes { get; init; } = 20;

    public int LookbackCandles { get; init; } = 120;

    public int BreakoutLookbackCandles { get; init; } = 60;

    public string CandleInterval { get; init; } = "1";

    public decimal MinTrendStrengthPercent { get; init; } = 0.08m;

    public decimal MinVolumeRatio { get; init; } = 1.2m;

    public decimal BreakoutBufferPercent { get; init; } = 0.1m;

    public decimal PullbackExitPercent { get; init; } = 0.8m;

    public decimal StopLossPercent { get; init; } = 2m;

    public decimal TakeProfitPercent { get; init; } = 3m;

    public decimal LimitOffsetPercent { get; init; } = 0m;

    public decimal? MaxPositionUsdt { get; init; }
}
