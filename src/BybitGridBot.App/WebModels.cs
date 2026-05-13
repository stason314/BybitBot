using BybitGridBot.Domain;

namespace BybitGridBot.App;

public sealed class DashboardResponse
{
    public required IReadOnlyList<DashboardProfileItem> Profiles { get; init; }

    public required DashboardSettings Settings { get; init; }

    public required DashboardRuntime Runtime { get; init; }

    public required DashboardState State { get; init; }

    public required IReadOnlyList<DashboardOrderItem> Orders { get; init; }

    public required IReadOnlyList<DashboardOrderItem> ActiveOrders { get; init; }

    public required IReadOnlyList<decimal> GridLevels { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed class DashboardRuntime
{
    public DateTimeOffset StartedAt { get; init; }

    public TimeSpan ActiveTime { get; init; }
}

public sealed class DashboardProfileItem
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public bool IsSelected { get; init; }
}

public sealed class DashboardSettings
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public decimal LowerPrice { get; init; }

    public decimal UpperPrice { get; init; }

    public decimal Step { get; init; }

    public decimal OrderSizeUsdt { get; init; }

    public decimal StopLowerPrice { get; init; }

    public decimal StopUpperPrice { get; init; }
}

public sealed class DashboardState
{
    public required string TradingMode { get; init; }

    public required bool IsPaused { get; init; }

    public string? PauseReason { get; init; }

    public decimal? CurrentPrice { get; init; }

    public decimal TotalRealizedPnl { get; init; }

    public decimal DailyRealizedPnl { get; init; }

    public decimal UnrealizedPnl { get; init; }

    public decimal EstimatedTotalEquity { get; init; }

    public decimal BaseAssetQuantity { get; init; }

    public decimal QuoteAssetBalance { get; init; }

    public decimal AverageEntryPrice { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class DashboardOrderItem
{
    public required string OrderLinkId { get; init; }

    public string? BybitOrderId { get; init; }

    public required string Symbol { get; init; }

    public required string Side { get; init; }

    public decimal Price { get; init; }

    public decimal Quantity { get; init; }

    public decimal FilledQuantity { get; init; }

    public decimal RealizedPnl { get; init; }

    public decimal FeePaid { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? FilledAt { get; init; }
}

public sealed class UpdateSettingsRequest
{
    public string Symbol { get; init; } = string.Empty;

    public string Category { get; init; } = "spot";

    public decimal LowerPrice { get; init; }

    public decimal UpperPrice { get; init; }

    public decimal Step { get; init; }

    public decimal OrderSizeUsdt { get; init; }

    public decimal StopLowerPrice { get; init; }

    public decimal StopUpperPrice { get; init; }
}

public sealed class UpdateSettingsResponse
{
    public bool Success { get; init; }

    public string? Symbol { get; init; }

    public required string Message { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}
