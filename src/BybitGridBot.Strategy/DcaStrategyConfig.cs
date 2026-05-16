namespace BybitGridBot.Strategy;

public class DcaStrategyConfig
{
    public decimal? OrderSizeUsdt { get; init; }

    public int BuyIntervalMinutes { get; init; } = 30;

    public int MaxActiveBuyOrders { get; init; } = 1;

    public decimal TakeProfitPercent { get; init; } = 1m;

    public bool TakeProfitLadderEnabled { get; init; } = true;

    public decimal TakeProfitLadderFirstPercent { get; init; } = 0.45m;

    public decimal TakeProfitLadderFirstQuantityPercent { get; init; } = 50m;

    public decimal TakeProfitLadderSecondPercent { get; init; } = 0.9m;

    public decimal TakeProfitLadderSecondQuantityPercent { get; init; } = 30m;

    public decimal TakeProfitLadderFinalPercent { get; init; } = 1.5m;

    public decimal TakeProfitLadderFinalQuantityPercent { get; init; } = 20m;

    public decimal LimitOffsetPercent { get; init; } = 0.05m;

    public decimal DipPercent { get; init; } = 0m;

    public int DipLookbackCandles { get; init; } = 30;

    public string CandleInterval { get; init; } = "1";

    public decimal? MaxPositionUsdt { get; init; }
}
