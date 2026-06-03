namespace BybitGridBot.App;

public sealed class NySessionDashboardResponse
{
    public string TradingMode { get; init; } = string.Empty;

    public bool FuturesEnabled { get; init; }

    public bool StrategyEnabled { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }

    public DateTimeOffset? LastScanAt { get; init; }

    public DateTimeOffset? NewYorkSessionStart { get; init; }

    public DateTimeOffset? FourHourRangeStart { get; init; }

    public DateTimeOffset? FourHourRangeEnd { get; init; }

    public decimal TotalPnl { get; init; }

    public decimal DailyPnl { get; init; }

    public decimal UnrealizedPnl { get; init; }

    public string Status { get; init; } = "Starting";

    public IReadOnlyList<NySessionPoolItem> Pool { get; init; } = [];

    public IReadOnlyList<NySessionOpenTradeItem> OpenTrades { get; init; } = [];

    public IReadOnlyList<NySessionEventItem> Events { get; init; } = [];
}

public sealed class NySessionPoolItem
{
    public int Slot { get; init; }

    public string Symbol { get; init; } = string.Empty;

    public decimal LastPrice { get; init; }

    public decimal FourHourHigh { get; init; }

    public decimal FourHourLow { get; init; }

    public decimal RangePercent { get; init; }

    public string State { get; init; } = string.Empty;

    public string Bias { get; init; } = string.Empty;

    public decimal DistanceToUpperPercent { get; init; }

    public decimal DistanceToLowerPercent { get; init; }

    public decimal Turnover24h { get; init; }

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class NySessionOpenTradeItem
{
    public string Symbol { get; init; } = string.Empty;

    public string Side { get; init; } = string.Empty;

    public decimal Size { get; init; }

    public decimal EntryPrice { get; init; }

    public decimal MarkPrice { get; init; }

    public decimal StopLoss { get; init; }

    public decimal TakeProfit { get; init; }

    public decimal UnrealizedPnl { get; init; }

    public decimal UnrealizedPnlPercent { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class NySessionEventItem
{
    public DateTimeOffset CreatedAt { get; init; }

    public string Symbol { get; init; } = string.Empty;

    public string Level { get; init; } = "info";

    public string Message { get; init; } = string.Empty;
}

public sealed class NySessionPoolReplaceRequest
{
    public int Slot { get; init; }

    public string CurrentSymbol { get; init; } = string.Empty;

    public string NewSymbol { get; init; } = string.Empty;
}

internal sealed class NySessionSignal
{
    public string Pattern { get; init; } = "Sweep Reversal";

    public string Side { get; init; } = string.Empty;

    public DateTimeOffset SignalCandleOpenTime { get; init; }

    public DateTimeOffset BreakoutCandleOpenTime { get; init; }

    public decimal Boundary { get; init; }

    public decimal RangeHigh { get; init; }

    public decimal RangeLow { get; init; }

    public decimal EntryPrice { get; init; }

    public decimal SweepExtreme { get; init; }

    public decimal StopLoss { get; init; }

    public decimal TakeProfit { get; init; }

    public decimal ReclaimPercent { get; init; }

    public decimal SweepDepthPercent { get; init; }

    public decimal StopDistancePercent { get; init; }

    public decimal MidlineRoomR { get; init; }

    public decimal BreakoutVolumeRatio { get; init; }

    public decimal BodyRatio { get; init; }

    public string Reason { get; init; } = string.Empty;
}

internal sealed class NySessionEntryFilterResult
{
    public bool IsAllowed { get; init; }

    public string Mode { get; init; } = "Sweep Reversal";

    public string Reason { get; init; } = string.Empty;
}
