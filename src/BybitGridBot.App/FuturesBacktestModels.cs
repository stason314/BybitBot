namespace BybitGridBot.App;

public enum FuturesBacktestMode
{
    ScoreBasedRouter,
    TurtleOnly
}

public sealed class FuturesBacktestRequest
{
    public int? Days { get; init; }

    public int? Symbols { get; init; }

    public string? Mode { get; init; }

    public string? TurtleAllowedWeekdays { get; init; }

    public string? TurtleAllowedNyHours { get; init; }

    public bool? RunNyBounceRouter { get; init; }

    public string? TurtleAllowedDirections { get; init; }

    public string? TurtleAllowedSystems { get; init; }

    public decimal? TurtleRiskPerUnitPercent { get; init; }

    public decimal? TurtleBreakoutAtrBufferMultiplier { get; init; }

    public decimal? TurtleMinAdx { get; init; }

    public decimal? TurtleVolumeMultiplier { get; init; }

    public bool? TurtleUseMarketRegimeFilter { get; init; }

    public string? TurtleMarketRegimeTimeframe { get; init; }

    public bool? EnableRollingWalkForwardGate { get; init; }

    public int? RollingWalkForwardOptimizationDays { get; init; }

    public int? RollingWalkForwardOutOfSampleDays { get; init; }

    public int? RollingWalkForwardStepDays { get; init; }

    public int? RollingWalkForwardMinWindows { get; init; }

    public decimal? RollingWalkForwardMinPassPercent { get; init; }

    public decimal? EntryNotionalUsdt { get; init; }

    public decimal? TakerFeePercent { get; init; }

    public decimal? MakerFeePercent { get; init; }

    public decimal? SlippagePercent { get; init; }

    public decimal? FundingPercentPer8h { get; init; }

    public decimal? InitialEquityUsdt { get; init; }

    public decimal? Leverage { get; init; }

    public decimal? MinLiquidationBufferPercent { get; init; }

    public decimal? MaxTradeLossEquityPercent { get; init; }

    public decimal? MaxProjectedDrawdownEquityPercent { get; init; }
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

    public FuturesBacktestRequest AppliedSettings { get; init; } = new();
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

    public int HardRiskCapBlockedCount { get; init; }

    public int LiquidationCount { get; init; }

    public decimal MaxTradeLossEquityPercent { get; init; }

    public decimal MaxProjectedDrawdownEquityPercent { get; init; }

    public decimal Leverage { get; init; }

    public decimal MinLiquidationBufferPercent { get; init; }

    public bool RunNyBounceRouter { get; init; }

    public string TurtleAllowedDirections { get; init; } = string.Empty;

    public string TurtleAllowedSystems { get; init; } = string.Empty;

    public decimal TurtleRiskPerUnitPercent { get; init; }

    public decimal TurtleBreakoutAtrBufferMultiplier { get; init; }

    public decimal TurtleMinAdx { get; init; }

    public decimal TurtleVolumeMultiplier { get; init; }

    public bool TurtleUseMarketRegimeFilter { get; init; }

    public string TurtleMarketRegimeTimeframe { get; init; } = "240";

    public bool EnableRollingWalkForwardGate { get; init; }

    public int RollingWalkForwardOptimizationDays { get; init; }

    public int RollingWalkForwardOutOfSampleDays { get; init; }

    public int RollingWalkForwardStepDays { get; init; }

    public int RollingWalkForwardMinWindows { get; init; }

    public decimal RollingWalkForwardMinPassPercent { get; init; }

    public int OpenAtBacktestEndCount { get; init; }

    public decimal OpenAtBacktestEndUnrealizedPnl { get; init; }

    public decimal InitialEquityUsdt { get; init; }

    public string OptimizationWindowLabel { get; init; } = "Optimization";

    public string OutOfSampleWindowLabel { get; init; } = "Out-of-sample";

    public FuturesBacktestMetrics Metrics { get; init; } = new();

    public FuturesBacktestMetrics OptimizationMetrics { get; init; } = new();

    public FuturesBacktestMetrics OutOfSampleMetrics { get; init; } = new();

    public FuturesBacktestMetrics FilteredOutOfSampleMetrics { get; init; } = new();

    public bool LiveUseEligibleStrategyGatesOnly { get; init; }

    public decimal LiveEligibleGateSizeMultiplier { get; init; }

    public decimal LiveIneligibleGateSizeMultiplier { get; init; }

    public string LiveEligibleDirections { get; init; } = string.Empty;

    public int LiveAllowedStrategyGatesCount => EligibleStrategySymbolDirections.Count;

    public IReadOnlyList<string> EligibleSymbols { get; init; } = [];

    public IReadOnlyList<string> ExcludedSymbols { get; init; } = [];

    public IReadOnlyList<string> EligibleStrategySymbolDirections { get; init; } = [];

    public IReadOnlyList<string> ExcludedStrategySymbolDirections { get; init; } = [];

    public IReadOnlyList<string> OpenProfitableStrategySymbolDirections { get; init; } = [];

    public IReadOnlyList<string> MarkToMarketProfitableStrategySymbolDirections { get; init; } = [];

    public IReadOnlyList<string> WatchlistStrategySymbolDirections { get; init; } = [];

    public IReadOnlyList<FuturesBacktestGateDiagnostic> GateDiagnostics { get; init; } = [];

    public IReadOnlyList<FuturesBacktestGateWalkForwardPerformance> WalkForwardStrategyGates { get; init; } = [];

    public IReadOnlyList<FuturesBacktestRollingWalkForwardDiagnostic> RollingWalkForwardDiagnostics { get; init; } = [];

    public IReadOnlyList<FuturesBacktestSymbolPerformance> BestSymbols { get; init; } = [];

    public IReadOnlyList<FuturesBacktestSymbolPerformance> WorstSymbols { get; init; } = [];

    public IReadOnlyList<FuturesBacktestSidePerformance> LongShort { get; init; } = [];

    public IReadOnlyList<FuturesBacktestBucketPerformance> PatternPerformance { get; init; } = [];

    public IReadOnlyList<StrategyPerformanceSnapshot> StrategyPerformance { get; init; } = [];

    public IReadOnlyList<FuturesBacktestBucketPerformance> WeekdayPerformance { get; init; } = [];

    public IReadOnlyList<FuturesBacktestBucketPerformance> HourPerformance { get; init; } = [];

    public IReadOnlyList<FuturesBacktestTrade> RecentTrades { get; init; } = [];

    public IReadOnlyList<FuturesBacktestTrade> OpenAtBacktestEndTrades { get; init; } = [];

    public IReadOnlyList<FuturesBacktestTiming> Timings { get; init; } = [];
}

public sealed class FuturesBacktestTiming
{
    public string Stage { get; init; } = string.Empty;

    public int Count { get; init; }

    public decimal TotalMilliseconds { get; init; }

    public decimal AverageMilliseconds { get; init; }

    public decimal MaxMilliseconds { get; init; }
}

public sealed class FuturesBacktestMetrics
{
    public int TradesCount { get; init; }

    public decimal NetPnl { get; init; }

    public decimal ClosedNetPnl { get; init; }

    public decimal OpenUnrealizedPnl { get; init; }

    public decimal MarkToMarketNetPnl { get; init; }

    public decimal ForcedClosedNetPnl { get; init; }

    public decimal ForcedClosedExitCost { get; init; }

    public decimal PnlWithoutTop1 { get; init; }

    public decimal PnlWithoutTop2 { get; init; }

    public decimal MaxDrawdown { get; init; }

    public decimal MaxDrawdownPercent { get; init; }

    public decimal MarkToMarketMaxDrawdown { get; init; }

    public decimal MarkToMarketMaxDrawdownPercent { get; init; }

    public decimal ForcedClosedMaxDrawdown { get; init; }

    public decimal ForcedClosedMaxDrawdownPercent { get; init; }

    public decimal WinRate { get; init; }

    public decimal ProfitFactor { get; init; }

    public decimal AverageR { get; init; }

    public decimal TradesPerDay { get; init; }
}

public sealed class FuturesBacktestGateDiagnostic
{
    public string Key { get; init; } = string.Empty;

    public string StrategyName { get; init; } = string.Empty;

    public string System { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public string Direction { get; init; } = string.Empty;

    public bool IsLiveAllowed { get; init; }

    public string Reason { get; init; } = string.Empty;

    public int OptimizationTrades { get; init; }

    public decimal OptimizationNetPnl { get; init; }

    public decimal OptimizationProfitFactor { get; init; }

    public decimal OptimizationAverageR { get; init; }

    public int OosClosedTrades { get; init; }

    public decimal OosClosedNetPnl { get; init; }

    public decimal OosClosedProfitFactor { get; init; }

    public decimal OosClosedAverageR { get; init; }

    public decimal OosClosedMaxDrawdownPercent { get; init; }

    public decimal OosClosedLargestWinGrossProfitPercent { get; init; }

    public decimal OosClosedMedianR { get; init; }

    public int RollingWalkForwardWindows { get; init; }

    public int RollingWalkForwardPassedWindows { get; init; }

    public decimal RollingWalkForwardPassPercent { get; init; }

    public decimal RollingWalkForwardOosNetPnl { get; init; }

    public decimal RollingWalkForwardOosPnlWithoutTop1 { get; init; }

    public decimal RollingWalkForwardMedianOosAverageR { get; init; }

    public int OosOpenTrades { get; init; }

    public decimal OosOpenNetPnl { get; init; }

    public int OosMarkToMarketTrades { get; init; }

    public decimal OosMarkToMarketNetPnl { get; init; }

    public decimal OosMarkToMarketAverageR { get; init; }

    public int OosForcedClosedTrades { get; init; }

    public decimal OosForcedClosedNetPnl { get; init; }

    public decimal OosForcedClosedAverageR { get; init; }

    public decimal OosForcedClosedMaxDrawdownPercent { get; init; }
}

public sealed class FuturesBacktestGateWalkForwardPerformance
{
    public string Key { get; init; } = string.Empty;

    public string StrategyName { get; init; } = string.Empty;

    public string System { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public string Direction { get; init; } = string.Empty;

    public bool IsLiveAllowed { get; init; }

    public FuturesBacktestMetrics OptimizationMetrics { get; init; } = new();

    public FuturesBacktestMetrics OutOfSampleMetrics { get; init; } = new();
}

public sealed class FuturesBacktestRollingWalkForwardDiagnostic
{
    public string Key { get; init; } = string.Empty;

    public string StrategyName { get; init; } = string.Empty;

    public string System { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public string Direction { get; init; } = string.Empty;

    public bool IsLiveAllowed { get; init; }

    public int Windows { get; init; }

    public int PassedWindows { get; init; }

    public decimal PassPercent { get; init; }

    public int TotalOosTrades { get; init; }

    public decimal TotalOosNetPnl { get; init; }

    public decimal OosPnlWithoutTop1 { get; init; }

    public decimal MedianOosAverageR { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed class FuturesBacktestSymbolPerformance
{
    public string Symbol { get; init; } = string.Empty;

    public int Trades { get; init; }

    public decimal NetPnl { get; init; }

    public decimal WinRate { get; init; }

    public decimal ProfitFactor { get; init; }

    public decimal AverageR { get; init; }

    public decimal LargestWinGrossProfitPercent { get; init; }
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
