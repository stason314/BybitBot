using BybitGridBot.Domain;

namespace BybitGridBot.App;

public sealed class DashboardResponse
{
    public required IReadOnlyList<DashboardProfileItem> Profiles { get; init; }

    public required IReadOnlyList<DashboardConfigSummaryItem> ConfigSummaries { get; init; }

    public required DashboardSettings Settings { get; init; }

    public required DashboardRuntime Runtime { get; init; }

    public required DashboardState State { get; init; }

    public required DashboardMarketRegime MarketRegime { get; init; }

    public required DashboardSignalAnalysis SignalAnalysis { get; init; }

    public required DashboardAutoRecommendation AutoRecommendation { get; init; }

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

public sealed class DashboardConfigSummaryItem
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public required string StrategyName { get; init; }

    public required string StrategyMode { get; init; }

    public required string Status { get; init; }

    public decimal DailyRealizedPnl { get; init; }

    public decimal TotalRealizedPnl { get; init; }

    public bool IsSelected { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class DashboardSettings
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public required string StrategyMode { get; init; }

    public required string StrategyType { get; init; }

    public required string StrategyConfigJson { get; init; }

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

public sealed class DashboardMarketRegime
{
    public required string Regime { get; init; }

    public decimal Confidence { get; init; }

    public required string Recommendation { get; init; }

    public decimal Adx { get; init; }

    public decimal MovePercent { get; init; }

    public decimal RangePercent { get; init; }

    public decimal VolumeRatio { get; init; }

    public decimal? Support { get; init; }

    public decimal? Resistance { get; init; }
}

public sealed class DashboardSignalAnalysis
{
    public required string Signal { get; init; }

    public decimal Confidence { get; init; }

    public required string Reason { get; init; }

    public decimal EmaFast { get; init; }

    public decimal EmaSlow { get; init; }

    public decimal Rsi { get; init; }

    public decimal BollingerPosition { get; init; }

    public decimal VolumeRatio { get; init; }

    public decimal TrendStrength { get; init; }
}

public sealed class DashboardAutoRecommendation
{
    public required string StrategyType { get; init; }

    public required string Reason { get; init; }

    public decimal LowerPrice { get; init; }

    public decimal UpperPrice { get; init; }

    public decimal Step { get; init; }

    public decimal OrderSizeUsdt { get; init; }

    public decimal StopLowerPrice { get; init; }

    public decimal StopUpperPrice { get; init; }

    public required string StrategyConfigJson { get; init; }

    public required string AnalysisCandleInterval { get; init; }

    public int AnalysisLookbackCandles { get; init; }

    public decimal AtrPercent { get; init; }

    public decimal DrawdownPercent { get; init; }

    public decimal Support { get; init; }

    public decimal Resistance { get; init; }

    public bool CanApply { get; init; }

    public IReadOnlyList<string> ApplySafetyErrors { get; init; } = [];
}

public sealed class DashboardOrderItem
{
    public required string OrderLinkId { get; init; }

    public string? BybitOrderId { get; init; }

    public string? ParentOrderLinkId { get; init; }

    public required string Symbol { get; init; }

    public required string Side { get; init; }

    public required string Source { get; init; }

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

    public string StrategyMode { get; init; } = "manual";

    public string StrategyType { get; init; } = "grid";

    public string StrategyConfigJson { get; init; } = "{}";

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

public sealed class FuturesDashboardResponse
{
    public required IReadOnlyList<FuturesProfileItem> Profiles { get; init; }

    public required IReadOnlyList<FuturesConfigSummaryItem> ConfigSummaries { get; init; }

    public required FuturesSettingsView Settings { get; init; }

    public required FuturesPositionView Position { get; init; }

    public required IReadOnlyList<string> StrategyActions { get; init; }

    public string? PositionError { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed class FuturesProfileItem
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public bool IsSelected { get; init; }
}

public sealed class FuturesConfigSummaryItem
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public required string StrategyType { get; init; }

    public required string Direction { get; init; }

    public decimal Leverage { get; init; }

    public decimal MaxNotionalUsdt { get; init; }

    public decimal MaxMarginUsdt { get; init; }

    public bool IsSelected { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class FuturesSettingsView
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public required string StrategyType { get; init; }

    public required string StrategyConfigJson { get; init; }

    public decimal Leverage { get; init; }

    public required string MarginMode { get; init; }

    public required string PositionMode { get; init; }

    public required string Direction { get; init; }

    public decimal MaxNotionalUsdt { get; init; }

    public decimal MaxMarginUsdt { get; init; }

    public decimal StopLossPercent { get; init; }

    public decimal TakeProfitPercent { get; init; }

    public decimal LiquidationBufferPercent { get; init; }

    public bool ReduceOnlyEnabled { get; init; }
}

public sealed class FuturesPositionView
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public required string Side { get; init; }

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

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class UpdateFuturesSettingsRequest
{
    public string Symbol { get; init; } = string.Empty;

    public string Category { get; init; } = "linear";

    public string StrategyType { get; init; } = "pause";

    public string StrategyConfigJson { get; init; } = "{}";

    public decimal Leverage { get; init; } = 2m;

    public string MarginMode { get; init; } = "isolated";

    public string PositionMode { get; init; } = "oneway";

    public string Direction { get; init; } = "long-only";

    public decimal MaxNotionalUsdt { get; init; } = 100m;

    public decimal MaxMarginUsdt { get; init; } = 50m;

    public decimal StopLossPercent { get; init; } = 2m;

    public decimal TakeProfitPercent { get; init; } = 4m;

    public decimal LiquidationBufferPercent { get; init; } = 15m;

    public bool ReduceOnlyEnabled { get; init; } = true;
}
