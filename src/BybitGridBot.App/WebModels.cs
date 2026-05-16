using BybitGridBot.Domain;

namespace BybitGridBot.App;

public sealed class DashboardResponse
{
    public bool IsPartial { get; init; }

    public required IReadOnlyList<DashboardProfileItem> Profiles { get; init; }

    public required IReadOnlyList<DashboardConfigSummaryItem> ConfigSummaries { get; init; }

    public required IReadOnlyList<DashboardPairScoreItem> PairScores { get; init; }

    public required DashboardSettings Settings { get; init; }

    public required DashboardRuntime Runtime { get; init; }

    public required DashboardState State { get; init; }

    public required DashboardMarketRegime MarketRegime { get; init; }

    public required DashboardSignalAnalysis SignalAnalysis { get; init; }

    public required DashboardBtdDiagnostics BtdDiagnostics { get; init; }

    public required DashboardAutoRecommendation AutoRecommendation { get; init; }

    public DashboardNoTradeReason? LastNoTradeReason { get; init; }

    public required IReadOnlyList<DashboardNoTradeReason> NoTradeReasonHistory { get; init; }

    public required IReadOnlyList<DashboardOrderItem> Orders { get; init; }

    public required IReadOnlyList<DashboardOrderItem> ActiveOrders { get; init; }

    public required IReadOnlyList<DashboardStrategyPerformanceItem> PerformanceByStrategy { get; init; }

    public required IReadOnlyList<DashboardDailyStrategyPerformanceItem> DailyPerformanceByStrategy { get; init; }

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

    public decimal PairScore { get; init; }

    public required string PairScoreLabel { get; init; }

    public decimal SuggestedOrderSizeMultiplier { get; init; }

    public required IReadOnlyList<string> PairScoreReasons { get; init; }

    public bool CanTradeNow { get; init; }

    public required string ExecutionReadiness { get; init; }

    public required string WhyNoOrdersNow { get; init; }

    public required IReadOnlyList<string> ExecutionReadinessReasons { get; init; }

    public bool IsSelected { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class DashboardPairScoreItem
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public decimal Score { get; init; }

    public required string Label { get; init; }

    public decimal SuggestedOrderSizeMultiplier { get; init; }

    public decimal SpreadPercent { get; init; }

    public decimal VolatilityPercent { get; init; }

    public decimal VolumeRatio { get; init; }

    public decimal RecentWinRate { get; init; }

    public decimal CurrentDrawdownPercent { get; init; }

    public string? LastNoTradeReason { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
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

    public decimal ProfitProtectionCurrentProfitPercent { get; init; }

    public decimal ProfitProtectionPeakProfitPercent { get; init; }

    public decimal ProfitProtectionPeakPrice { get; init; }

    public decimal ProfitProtectionTrailingStopPrice { get; init; }

    public bool AggressiveModeEnabled { get; init; }

    public DateTimeOffset? AggressiveModeDisabledUntil { get; init; }

    public string? AggressiveModeDisabledReason { get; init; }

    public DateTimeOffset? AggressiveModeLastLossAt { get; init; }

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

public sealed class DashboardBtdDiagnostics
{
    public required string Phase { get; init; }

    public decimal EmaFast { get; init; }

    public decimal EmaSlow { get; init; }

    public bool BtcRiskOff { get; init; }

    public decimal PullbackPercent { get; init; }

    public decimal DistanceToEmaPercent { get; init; }

    public bool DipTriggered { get; init; }

    public bool IsAllowed { get; init; }

    public required string Reason { get; init; }
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

public sealed class DashboardNoTradeReason
{
    public required string Code { get; init; }

    public string? StrategyType { get; init; }

    public required string Reason { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public int MinutesAgo { get; init; }
}

public sealed class DashboardOrderItem
{
    public required string OrderLinkId { get; init; }

    public string? BybitOrderId { get; init; }

    public string? ParentOrderLinkId { get; init; }

    public string? OrderGroup { get; init; }

    public string? LadderRole { get; init; }

    public required string Symbol { get; init; }

    public required string Side { get; init; }

    public required string Source { get; init; }

    public decimal Price { get; init; }

    public decimal Quantity { get; init; }

    public decimal FilledQuantity { get; init; }

    public decimal NotionalUsdt { get; init; }

    public decimal RemainingNotionalUsdt { get; init; }

    public decimal RealizedPnl { get; init; }

    public decimal TradePnl { get; init; }

    public decimal NetCashFlow { get; init; }

    public decimal FeePaid { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? FilledAt { get; init; }
}

public sealed class DashboardStrategyPerformanceItem
{
    public required string Strategy { get; init; }

    public decimal GrossPnl { get; init; }

    public decimal FeesPaid { get; init; }

    public decimal NetPnl { get; init; }

    public int FilledTradesCount { get; init; }

    public int ClosedTradesCount { get; init; }

    public int ActiveOrdersCount { get; init; }

    public decimal WinRate { get; init; }

    public decimal AverageWin { get; init; }

    public decimal AverageLoss { get; init; }
}

public sealed class DashboardDailyStrategyPerformanceItem
{
    public required string PerformanceDate { get; init; }

    public required string Strategy { get; init; }

    public decimal FeesPaid { get; init; }

    public decimal NetPnl { get; init; }

    public int FilledTradesCount { get; init; }

    public int ClosedTradesCount { get; init; }

    public decimal WinRate { get; init; }
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

public sealed class MarketScanResponse
{
    public required IReadOnlyList<MarketScanItem> Items { get; init; }

    public required string Category { get; init; }

    public int ScannedCount { get; init; }

    public int CandidateCount { get; init; }

    public int FailedCount { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed class MarketScanItem
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public decimal Score { get; init; }

    public required string Label { get; init; }

    public required string RecommendedStrategy { get; init; }

    public decimal RecommendedOrderSizeUsdt { get; init; }

    public decimal StrategyFitScore { get; init; }

    public required string StrategyFitName { get; init; }

    public decimal GridFitScore { get; init; }

    public decimal BtdFitScore { get; init; }

    public decimal ComboFitScore { get; init; }

    public decimal ReversalFitScore { get; init; }

    public decimal LastPrice { get; init; }

    public decimal SpreadPercent { get; init; }

    public decimal AtrPercent { get; init; }

    public decimal VolatilityPercent { get; init; }

    public decimal MomentumPercent { get; init; }

    public decimal Volume6hUsdt { get; init; }

    public decimal Support { get; init; }

    public decimal Resistance { get; init; }

    public decimal MinOrderAmount { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    public required UpdateSettingsRequest Settings { get; init; }
}

public sealed class FuturesMarketScanResponse
{
    public required IReadOnlyList<FuturesMarketScanItem> Items { get; init; }

    public required string Category { get; init; }

    public int ScannedCount { get; init; }

    public int CandidateCount { get; init; }

    public int FailedCount { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed class FuturesMarketScanItem
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public decimal Score { get; init; }

    public required string Label { get; init; }

    public required string RecommendedStrategy { get; init; }

    public required string RecommendedDirection { get; init; }

    public decimal EntryNotionalUsdt { get; init; }

    public decimal LastPrice { get; init; }

    public decimal SpreadPercent { get; init; }

    public decimal AtrPercent { get; init; }

    public decimal VolatilityPercent { get; init; }

    public decimal MomentumPercent { get; init; }

    public decimal Volume6hUsdt { get; init; }

    public decimal Support { get; init; }

    public decimal Resistance { get; init; }

    public decimal GridFitScore { get; init; }

    public decimal TrendFitScore { get; init; }

    public decimal BreakoutFitScore { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    public required UpdateFuturesSettingsRequest Settings { get; init; }
}

public sealed class FuturesDashboardResponse
{
    public required IReadOnlyList<FuturesProfileItem> Profiles { get; init; }

    public required IReadOnlyList<FuturesConfigSummaryItem> ConfigSummaries { get; init; }

    public required FuturesSettingsView Settings { get; init; }

    public required FuturesPositionView Position { get; init; }

    public required FuturesPaperAccountView PaperAccount { get; init; }

    public required FuturesPnlStatsView PnlStats { get; init; }

    public required FuturesSoakStatusView TestnetSoak { get; init; }

    public required FuturesProtectionStatusView ProtectionStatus { get; init; }

    public required FuturesUserStreamStatusView UserStreamStatus { get; init; }

    public required FuturesRuntimeControlsView RuntimeControls { get; init; }

    public required FuturesAggressiveModeView AggressiveMode { get; init; }

    public required FuturesStrategyQualityView StrategyQuality { get; init; }

    public required FuturesAutoRecommendationView AutoRecommendation { get; init; }

    public required IReadOnlyList<string> StrategyActions { get; init; }

    public required IReadOnlyList<FuturesOrderView> ActiveOrders { get; init; }

    public required IReadOnlyList<FuturesOrderView> RecentOrders { get; init; }

    public required IReadOnlyList<FuturesRiskDecisionView> RiskDecisions { get; init; }

    public FuturesRiskDecisionView? LastPreflightResult { get; init; }

    public required string TradingMode { get; init; }

    public bool FuturesEnabled { get; init; }

    public string? PositionError { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed class FuturesProfileItem
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public bool IsSelected { get; init; }

    public bool Enabled { get; init; }
}

public sealed class FuturesConfigSummaryItem
{
    public required string Symbol { get; init; }

    public required string Category { get; init; }

    public required string StrategyType { get; init; }

    public required string Direction { get; init; }

    public bool Enabled { get; init; }

    public decimal Leverage { get; init; }

    public decimal MaxNotionalUsdt { get; init; }

    public decimal MaxMarginUsdt { get; init; }

    public decimal DailyRealizedPnl { get; init; }

    public decimal TotalRealizedPnl { get; init; }

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

    public bool AggressiveModeEnabled { get; init; }

    public required string AggressiveModeKind { get; init; }

    public decimal AggressiveEntryMultiplier { get; init; }

    public int AggressiveMaxOrdersPerHour { get; init; }

    public int AggressiveMinSecondsBetweenEntries { get; init; }

    public int AggressiveMaxConsecutiveLosses { get; init; }

    public bool Enabled { get; init; }
}

public sealed class FuturesAggressiveModeView
{
    public bool Enabled { get; init; }

    public bool Effective { get; init; }

    public required string ModeKind { get; init; }

    public bool PaperOnly { get; init; }

    public decimal EntryMultiplier { get; init; }

    public int EntriesLastHour { get; init; }

    public int MaxEntriesPerHour { get; init; }

    public int MinSecondsBetweenEntries { get; init; }

    public int ConsecutiveLosses { get; init; }

    public int MaxConsecutiveLosses { get; init; }

    public required string GuardStatus { get; init; }

    public required string LastBlockReason { get; init; }

    public required string LastNoTradeReason { get; init; }
}

public sealed class FuturesRuntimeControlsView
{
    public bool EnvEmergencyPauseEnabled { get; init; }

    public bool AutoApplyRecommendationEnabled { get; init; }

    public int AutoRecommendationMinApplyIntervalMinutes { get; init; }

    public bool ProfilePaused { get; init; }

    public string PauseReason { get; init; } = "-";

    public decimal DailyRealizedPnl { get; init; }

    public decimal MaxDailyLossUsdt { get; init; }

    public decimal MaxDailyLossEquityPercent { get; init; }

    public decimal PeakEquityUsdt { get; init; }

    public decimal CurrentDrawdownUsdt { get; init; }

    public decimal CurrentDrawdownPercent { get; init; }

    public decimal MaxDrawdownEquityPercent { get; init; }

    public int OpenPositionCount { get; init; }

    public string OpenPositionSymbols { get; init; } = "-";

    public string StalePositionSymbols { get; init; } = "-";

    public int MaxOpenPositions { get; init; }
}

public sealed class FuturesStrategyQualityView
{
    public required string StrategyType { get; init; }

    public required string Direction { get; init; }

    public decimal MaxEntryAtrPercent { get; init; }

    public bool BtcRiskOffEnabled { get; init; }

    public decimal BtcRiskOffMovePercent { get; init; }

    public int StopLossCooldownMinutes { get; init; }

    public int NoTradeReasonCount { get; init; }

    public int StrategyFilterBlockCount { get; init; }

    public int RiskBlockCount { get; init; }

    public string CurrentActiveBlockReason { get; init; } = "-";

    public string CurrentActiveBlockSource { get; init; } = "-";

    public DateTimeOffset? CurrentActiveBlockAt { get; init; }

    public string LastNoTradeReason { get; init; } = "-";

    public string LastHistoricalNoTradeReason { get; init; } = "-";
}

public sealed class FuturesOrderView
{
    public required string OrderLinkId { get; init; }

    public string? BybitOrderId { get; init; }

    public required string Symbol { get; init; }

    public required string Action { get; init; }

    public required string Side { get; init; }

    public decimal Price { get; init; }

    public decimal Quantity { get; init; }

    public decimal FilledQuantity { get; init; }

    public decimal AverageFillPrice { get; init; }

    public decimal RealizedPnl { get; init; }

    public decimal FeePaid { get; init; }

    public required string Status { get; init; }

    public bool ReduceOnly { get; init; }

    public int PositionIdx { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? FilledAt { get; init; }
}

public sealed class FuturesPaperAccountView
{
    public decimal InitialEquityUsdt { get; init; }

    public decimal CashUsdt { get; init; }

    public decimal CurrentEquityUsdt { get; init; }

    public decimal PeakEquityUsdt { get; init; }

    public decimal CurrentDrawdownUsdt { get; init; }

    public decimal CurrentDrawdownPercent { get; init; }

    public decimal TotalRealizedPnl { get; init; }

    public decimal DailyRealizedPnl { get; init; }

    public decimal UnrealizedPnl { get; init; }

    public decimal ReturnPercent { get; init; }
}

public sealed class FuturesPnlStatsView
{
    public decimal GrossProfit { get; init; }

    public decimal GrossLoss { get; init; }

    public decimal NetPnl { get; init; }

    public decimal FeesPaid { get; init; }

    public decimal FundingPaid { get; init; }

    public int FilledTradesCount { get; init; }

    public int WinningTradesCount { get; init; }

    public int LosingTradesCount { get; init; }

    public decimal WinRate { get; init; }

    public decimal ProfitFactor { get; init; }

    public decimal AverageWin { get; init; }

    public decimal AverageLoss { get; init; }
}

public sealed class FuturesSoakStatusView
{
    public bool IsTestnetMode { get; init; }

    public bool TestnetEnabled { get; init; }

    public bool UserStreamEnabled { get; init; }

    public bool HasOpenPosition { get; init; }

    public int ActiveOrderCount { get; init; }

    public int RecentOrderCount { get; init; }

    public int FillCount { get; init; }

    public int RiskDecisionCount { get; init; }

    public string LastRiskSource { get; init; } = "-";

    public string LastRiskReason { get; init; } = "-";
}

public sealed class FuturesProtectionStatusView
{
    public bool HasOpenPosition { get; init; }

    public decimal ExpectedStopLoss { get; init; }

    public decimal ExpectedTakeProfit { get; init; }

    public decimal CurrentStopLoss { get; init; }

    public decimal CurrentTakeProfit { get; init; }

    public required string Status { get; init; }

    public required string LastSource { get; init; }

    public required string LastReason { get; init; }

    public DateTimeOffset? LastCheckedAt { get; init; }
}

public sealed class FuturesUserStreamStatusView
{
    public bool Enabled { get; init; }

    public bool Connected { get; init; }

    public bool Stale { get; init; }

    public bool FallbackActive { get; init; }

    public string FallbackReason { get; init; } = "-";

    public DateTimeOffset? ConnectedAt { get; init; }

    public DateTimeOffset? LastConnectAttemptAt { get; init; }

    public DateTimeOffset? LastDisconnectedAt { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }

    public DateTimeOffset? LastEventAt { get; init; }

    public string LastEventType { get; init; } = "-";

    public string LastTopic { get; init; } = "-";

    public int DisconnectCount { get; init; }

    public int ConnectAttemptCount { get; init; }

    public string LastError { get; init; } = "-";
}

public sealed class FuturesRiskDecisionView
{
    public required string Source { get; init; }

    public string? OrderLinkId { get; init; }

    public string? Action { get; init; }

    public bool IsAllowed { get; init; }

    public required string Reason { get; init; }

    public required string Severity { get; init; }

    public required string SuggestedAction { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
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

public sealed class FuturesAutoRecommendationView
{
    public required string StrategyType { get; init; }

    public required string Reason { get; init; }

    public decimal Leverage { get; init; }

    public required string MarginMode { get; init; }

    public required string PositionMode { get; init; }

    public required string Direction { get; init; }

    public decimal MaxNotionalUsdt { get; init; }

    public decimal MaxMarginUsdt { get; init; }

    public decimal StopLossPercent { get; init; }

    public decimal TakeProfitPercent { get; init; }

    public decimal LiquidationBufferPercent { get; init; }

    public required string StrategyConfigJson { get; init; }

    public bool AutoApplyEnabled { get; init; }

    public bool CanApply { get; init; }

    public required string ApplyBlockReason { get; init; }

    public required string CompatibleStrategyForPosition { get; init; }

    public decimal LastPrice { get; init; }

    public decimal MovePercent { get; init; }

    public decimal AtrPercent { get; init; }

    public decimal DrawdownPercent { get; init; }

    public decimal Support { get; init; }

    public decimal Resistance { get; init; }
}

public sealed class UpdateFuturesSettingsRequest
{
    public bool Enabled { get; init; } = true;

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

    public bool AggressiveModeEnabled { get; init; }

    public string AggressiveModeKind { get; init; } = "normal";

    public decimal AggressiveEntryMultiplier { get; init; } = 1.5m;

    public int AggressiveMaxOrdersPerHour { get; init; } = 6;

    public int AggressiveMinSecondsBetweenEntries { get; init; } = 60;

    public int AggressiveMaxConsecutiveLosses { get; init; } = 2;
}
