namespace BybitGridBot.Domain;

public sealed class FuturesOrderRecord
{
    public string OrderLinkId { get; init; } = string.Empty;

    public string? BybitOrderId { get; set; }

    public string Symbol { get; init; } = string.Empty;

    public string Category { get; init; } = "linear";

    public FuturesTradeAction Action { get; init; }

    public TradeSide Side { get; init; }

    public decimal Price { get; set; }

    public decimal Quantity { get; set; }

    public decimal FilledQuantity { get; set; }

    public decimal AverageFillPrice { get; set; }

    public decimal FeePaid { get; set; }

    public OrderStatus Status { get; set; }

    public TradingMode TradingMode { get; init; }

    public string PositionSide { get; set; } = "Long";

    public bool ReduceOnly { get; set; }

    public int PositionIdx { get; set; }

    public decimal Leverage { get; set; }

    public string MarginMode { get; set; } = "Isolated";

    public decimal StopLossPrice { get; set; }

    public decimal TakeProfitPrice { get; set; }

    public decimal RealizedPnl { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FilledAt { get; set; }

    public bool IsActive => Status is OrderStatus.New or OrderStatus.PartiallyFilled;
}

public sealed class FuturesFillRecord
{
    public long FillId { get; init; }

    public string OrderLinkId { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public FuturesTradeAction Action { get; init; }

    public TradeSide Side { get; init; }

    public decimal Quantity { get; init; }

    public decimal Price { get; init; }

    public decimal Fee { get; init; }

    public decimal RealizedPnl { get; init; }

    public decimal Funding { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class FuturesRiskDecisionRecord
{
    public long RiskDecisionId { get; init; }

    public string Symbol { get; init; } = string.Empty;

    public string Source { get; init; } = "Risk";

    public string? OrderLinkId { get; init; }

    public FuturesTradeAction? Action { get; init; }

    public bool IsAllowed { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string Severity { get; init; } = string.Empty;

    public string SuggestedAction { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
