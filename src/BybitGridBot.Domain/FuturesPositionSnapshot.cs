namespace BybitGridBot.Domain;

public sealed class FuturesPositionSnapshot
{
    public string Symbol { get; init; } = string.Empty;

    public string Category { get; init; } = "linear";

    public string Side { get; init; } = "None";

    public decimal Size { get; init; }

    public decimal EntryPrice { get; init; }

    public decimal MarkPrice { get; init; }

    public decimal LiquidationPrice { get; init; }

    public decimal PositionValueUsdt { get; init; }

    public decimal MarginUsedUsdt { get; init; }

    public decimal Leverage { get; init; }

    public decimal UnrealizedPnl { get; init; }

    public decimal RealizedPnl { get; init; }

    public decimal Funding { get; init; }

    public int PositionIdx { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
