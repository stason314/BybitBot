namespace BybitGridBot.Strategy;

public sealed class SignalStrategyConfig
{
    public decimal? OrderSizeUsdt { get; init; }

    public int CooldownMinutes { get; init; } = 30;

    public decimal MinConfidence { get; init; } = 0.65m;

    public decimal? MaxPositionUsdt { get; init; }

    public decimal StopLossPercent { get; init; } = 2m;

    public decimal TakeProfitPercent { get; init; } = 3m;

    public decimal LimitOffsetPercent { get; init; } = 0m;

    public int LookbackCandles { get; init; } = 120;

    public string CandleInterval { get; init; } = "1";
}
