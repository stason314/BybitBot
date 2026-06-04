namespace BybitGridBot.App;

public sealed class FuturesBacktestRequest
{
    public int? Days { get; init; }

    public int? Symbols { get; init; }

    public decimal? EntryNotionalUsdt { get; init; }

    public decimal? TakerFeePercent { get; init; }

    public decimal? MakerFeePercent { get; init; }

    public decimal? SlippagePercent { get; init; }

    public decimal? FundingPercentPer8h { get; init; }
}

public sealed class FuturesBacktestStatusResponse
{
    public bool IsRunning { get; init; }

    public string Status { get; init; } = "Not started";

    public decimal ProgressPercent { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EstimatedCompletedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public FuturesBacktestResult? Result { get; init; }
}

public sealed class FuturesBacktestResult
{
    public string StrategyName { get; init; } = "NY 08:00 4H Sweep Reversal + Engulfing + Pinbar + 3-Bar Continuation + 3-Bar Reversal + Breakout Candle + Shrinking Candles";

    public DateTimeOffset PeriodStart { get; init; }

    public DateTimeOffset PeriodEnd { get; init; }

    public int SymbolsRequested { get; init; }

    public int SymbolsProcessed { get; init; }

    public int TradesCount { get; init; }

    public int FalseBreakoutCount { get; init; }

    public int TrueBreakoutBlockedCount { get; init; }

    public int OpenAtBacktestEndCount { get; init; }

    public decimal OpenAtBacktestEndUnrealizedPnl { get; init; }

    public FuturesBacktestMetrics Metrics { get; init; } = new();

    public FuturesBacktestMetrics OptimizationMetrics { get; init; } = new();

    public FuturesBacktestMetrics OutOfSampleMetrics { get; init; } = new();

    public FuturesBacktestMetrics FilteredOutOfSampleMetrics { get; init; } = new();

    public bool LiveUseEligibleStrategyGatesOnly { get; init; }

    public decimal LiveEligibleGateSizeMultiplier { get; init; }

    public decimal LiveIneligibleGateSizeMultiplier { get; init; }

    public int LiveAllowedStrategyGatesCount => EligibleStrategySymbolDirections.Count;

    public IReadOnlyList<string> EligibleSymbols { get; init; } = [];

    public IReadOnlyList<string> ExcludedSymbols { get; init; } = [];

    public IReadOnlyList<string> EligibleStrategySymbolDirections { get; init; } = [];

    public IReadOnlyList<string> ExcludedStrategySymbolDirections { get; init; } = [];

    public IReadOnlyList<FuturesBacktestSymbolPerformance> BestSymbols { get; init; } = [];

    public IReadOnlyList<FuturesBacktestSymbolPerformance> WorstSymbols { get; init; } = [];

    public IReadOnlyList<FuturesBacktestSidePerformance> LongShort { get; init; } = [];

    public IReadOnlyList<FuturesBacktestBucketPerformance> PatternPerformance { get; init; } = [];

    public IReadOnlyList<StrategyPerformanceSnapshot> StrategyPerformance { get; init; } = [];

    public IReadOnlyList<FuturesBacktestBucketPerformance> WeekdayPerformance { get; init; } = [];

    public IReadOnlyList<FuturesBacktestBucketPerformance> HourPerformance { get; init; } = [];

    public IReadOnlyList<FuturesBacktestTrade> RecentTrades { get; init; } = [];

    public IReadOnlyList<FuturesBacktestTrade> OpenAtBacktestEndTrades { get; init; } = [];
}

public sealed class FuturesBacktestMetrics
{
    public decimal NetPnl { get; init; }

    public decimal ClosedNetPnl { get; init; }

    public decimal OpenUnrealizedPnl { get; init; }

    public decimal MarkToMarketNetPnl { get; init; }

    public decimal MaxDrawdown { get; init; }

    public decimal MaxDrawdownPercent { get; init; }

    public decimal MarkToMarketMaxDrawdown { get; init; }

    public decimal MarkToMarketMaxDrawdownPercent { get; init; }

    public decimal WinRate { get; init; }

    public decimal ProfitFactor { get; init; }

    public decimal AverageR { get; init; }

    public decimal TradesPerDay { get; init; }
}

public sealed class FuturesBacktestSymbolPerformance
{
    public string Symbol { get; init; } = string.Empty;

    public int Trades { get; init; }

    public decimal NetPnl { get; init; }

    public decimal WinRate { get; init; }

    public decimal ProfitFactor { get; init; }

    public decimal AverageR { get; init; }
}

public sealed class FuturesBacktestSidePerformance
{
    public string Side { get; init; } = string.Empty;

    public int Trades { get; init; }

    public decimal NetPnl { get; init; }

    public decimal WinRate { get; init; }

    public decimal ProfitFactor { get; init; }

    public decimal AverageR { get; init; }
}

public sealed class FuturesBacktestBucketPerformance
{
    public string Bucket { get; init; } = string.Empty;

    public int Trades { get; init; }

    public decimal NetPnl { get; init; }

    public decimal WinRate { get; init; }

    public decimal ProfitFactor { get; init; }

    public decimal AverageR { get; init; }
}

public sealed class FuturesBacktestTrade
{
    public string Symbol { get; init; } = string.Empty;

    public string StrategyName { get; init; } = string.Empty;

    public string Side { get; init; } = string.Empty;

    public string Pattern { get; init; } = string.Empty;

    public DateTimeOffset EntryTime { get; init; }

    public DateTimeOffset ExitTime { get; init; }

    public decimal EntryPrice { get; init; }

    public decimal ExitPrice { get; init; }

    public decimal StopLoss { get; init; }

    public decimal TakeProfit { get; init; }

    public decimal GrossPnl { get; init; }

    public decimal Fees { get; init; }

    public decimal SlippageCost { get; init; }

    public decimal FundingCost { get; init; }

    public decimal NetPnl { get; init; }

    public decimal RMultiple { get; init; }

    public string ExitReason { get; init; } = string.Empty;
}
