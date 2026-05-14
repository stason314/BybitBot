namespace BybitGridBot.Strategy;

public class DcaStrategyConfig
{
    public decimal? OrderSizeUsdt { get; init; }

    public int BuyIntervalMinutes { get; init; } = 30;

    public int MaxActiveBuyOrders { get; init; } = 1;

    public decimal TakeProfitPercent { get; init; } = 1m;

    public decimal LimitOffsetPercent { get; init; } = 0.05m;

    public decimal DipPercent { get; init; } = 0m;

    public int DipLookbackCandles { get; init; } = 30;

    public string CandleInterval { get; init; } = "1";

    public decimal? MaxPositionUsdt { get; init; }
}
