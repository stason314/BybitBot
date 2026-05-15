namespace BybitGridBot.Domain;

public sealed class BotState
{
    public string Symbol { get; init; } = string.Empty;

    public TradingMode TradingMode { get; set; }

    public bool IsInitialized { get; set; }

    public bool IsPaused { get; set; }

    public string? PauseReason { get; set; }

    public decimal? LastObservedPrice { get; set; }

    public decimal BaseAssetQuantity { get; set; }

    public decimal QuoteAssetBalance { get; set; }

    public decimal AverageEntryPrice { get; set; }

    public string? PositionSide { get; set; }

    public bool ReduceOnly { get; set; }

    public int PositionIdx { get; set; }

    public decimal Leverage { get; set; }

    public string? MarginMode { get; set; }

    public decimal EntryPrice { get; set; }

    public decimal MarkPrice { get; set; }

    public decimal LiquidationPrice { get; set; }

    public decimal UnrealizedPnl { get; set; }

    public decimal TotalRealizedPnl { get; set; }

    public decimal DailyRealizedPnl { get; set; }

    public decimal PeakEquityUsdt { get; set; }

    public decimal CurrentDrawdownUsdt { get; set; }

    public decimal CurrentDrawdownPercent { get; set; }

    public decimal ProfitProtectionPeakPrice { get; set; }

    public decimal ProfitProtectionTrailingStopPrice { get; set; }

    public DateOnly DailyPnlDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
