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

    public decimal TotalRealizedPnl { get; set; }

    public decimal DailyRealizedPnl { get; set; }

    public DateOnly DailyPnlDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
