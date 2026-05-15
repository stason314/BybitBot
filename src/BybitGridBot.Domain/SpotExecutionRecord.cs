namespace BybitGridBot.Domain;

public sealed class SpotExecutionRecord
{
    public long ExecutionId { get; init; }

    public string ExecId { get; init; } = string.Empty;

    public string OrderLinkId { get; init; } = string.Empty;

    public string? BybitOrderId { get; init; }

    public string Symbol { get; init; } = string.Empty;

    public string Category { get; init; } = "spot";

    public TradeSide Side { get; init; }

    public string ExecType { get; init; } = "Trade";

    public decimal Quantity { get; init; }

    public decimal Price { get; init; }

    public decimal Fee { get; init; }

    public decimal RealizedPnl { get; init; }

    public bool IsApplied { get; init; } = true;

    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
