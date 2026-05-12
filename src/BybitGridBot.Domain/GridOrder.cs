namespace BybitGridBot.Domain;

public sealed class GridOrder
{
    public string OrderLinkId { get; init; } = string.Empty;

    public string? BybitOrderId { get; set; }

    public string Symbol { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public TradeSide Side { get; init; }

    public decimal Price { get; set; }

    public decimal Quantity { get; set; }

    public decimal FilledQuantity { get; set; }

    public decimal AverageFillPrice { get; set; }

    public decimal FeePaid { get; set; }

    public OrderStatus Status { get; set; }

    public TradingMode TradingMode { get; init; }

    public string? ParentOrderLinkId { get; init; }

    public decimal RealizedPnl { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? FilledAt { get; set; }

    public bool IsActive => Status is OrderStatus.New or OrderStatus.PartiallyFilled;
}
