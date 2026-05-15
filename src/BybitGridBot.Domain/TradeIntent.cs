namespace BybitGridBot.Domain;

public enum TradeIntentType
{
    Open,
    Close,
    Rebalance,
    Cancel
}

public enum OrderType
{
    Limit,
    Market
}

public sealed class TradeIntent
{
    public StrategyType StrategyType { get; init; }

    public TradeIntentType IntentType { get; init; } = TradeIntentType.Open;

    public string Symbol { get; init; } = string.Empty;

    public TradeSide Side { get; init; }

    public OrderType OrderType { get; init; } = OrderType.Limit;

    public decimal Price { get; init; }

    public decimal Quantity { get; init; }

    public string OrderLinkId { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public decimal Confidence { get; init; }

    public decimal ExpectedRisk { get; init; }

    public decimal ExpectedReward { get; init; }

    public decimal NotionalUsdt => Price > 0m ? Price * Quantity : 0m;
}
