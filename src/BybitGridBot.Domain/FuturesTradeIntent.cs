namespace BybitGridBot.Domain;

public sealed class FuturesTradeIntent
{
    public string Symbol { get; init; } = string.Empty;

    public string Category { get; init; } = "linear";

    public FuturesTradeAction Action { get; init; }

    public OrderType OrderType { get; init; } = OrderType.Limit;

    public decimal Price { get; init; }

    public decimal Quantity { get; init; }

    public decimal Leverage { get; init; } = 1m;

    public decimal? StopLossPrice { get; init; }

    public decimal? TakeProfitPrice { get; init; }

    public decimal? LiquidationPrice { get; init; }

    public decimal ExpectedFundingCostUsdt { get; init; }

    public int PositionIdx { get; init; }

    public string OrderLinkId { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public bool IsPositionIncreasing => Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort;

    public bool IsReduceOnly => Action is FuturesTradeAction.CloseLong or FuturesTradeAction.CloseShort or FuturesTradeAction.ReduceOnlyClose;

    public decimal NotionalUsdt => Price > 0m ? Price * Quantity : 0m;

    public decimal InitialMarginUsdt => Leverage > 0m ? NotionalUsdt / Leverage : decimal.MaxValue;
}
