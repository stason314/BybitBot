using BybitGridBot.Domain;

namespace BybitGridBot.App;

public enum SignalSelectionMode
{
    LegacyPriority,
    ScoreBased
}

public enum NyRangeMode
{
    DynamicSessionRange,
    LockedSessionRange,
    PreSessionReferenceRange
}

public enum StrategySide
{
    None,
    Long,
    Short
}

public enum StrategyEntryType
{
    Market,
    FixedRetest,
    DonchianBreakout
}

public enum StrategyNoTradeReason
{
    None,
    NotEnoughData,
    LowScore,
    LowConfidence,
    ConflictingSignals,
    SweepVsTurtleConflict,
    RiskRejected,
    AlreadyHasPosition,
    AlreadyProcessedSignal,
    WalkForwardRejected,
    BtcFilterRejected,
    StopTooSmall,
    StopTooLarge,
    TrueBreakoutProtection,
    HighVolumeBreakout,
    SessionClosed,
    PauseStrategy
}

public enum BreakoutClassification
{
    FalseBreakout,
    TrueBreakout,
    Unclear
}

public sealed class StrategyTradeIntent
{
    public string StrategyName { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public StrategySide Side { get; init; }

    public StrategyEntryType EntryType { get; init; } = StrategyEntryType.Market;

    public decimal EntryPrice { get; init; }

    public decimal StopLoss { get; init; }

    public decimal? TakeProfit { get; init; }

    public decimal Quantity { get; init; }

    public decimal RiskUsdt { get; init; }

    public decimal ExpectedR { get; init; }

    public string TurtleSystem { get; init; } = string.Empty;

    public string TurtleSignalId { get; init; } = string.Empty;

    public decimal TurtleN { get; init; }

    public decimal TurtleBreakoutLevel { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed class PatternSignal
{
    public string PatternName { get; init; } = string.Empty;

    public StrategySide Side { get; init; }

    public decimal Strength { get; init; }

    public decimal Confidence { get; init; }

    public DateTimeOffset CandleTime { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed class StrategyCandidate
{
    public string StrategyName { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public StrategySide Side { get; init; }

    public decimal Score { get; init; }

    public decimal Confidence { get; init; }

    public string Reason { get; init; } = string.Empty;

    public StrategyTradeIntent? TradeIntent { get; init; }

    public IReadOnlyList<PatternSignal> PatternConfirmations { get; init; } = [];

    public StrategyNoTradeReason RejectionReason { get; init; } = StrategyNoTradeReason.None;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool HasTradeIntent => TradeIntent is not null && Side != StrategySide.None;
}

public sealed class StrategyDecision
{
    public string SelectedStrategy { get; init; } = "PauseStrategy";

    public StrategyCandidate? SelectedCandidate { get; init; }

    public IReadOnlyList<StrategyCandidate> AllCandidates { get; init; } = [];

    public IReadOnlyList<StrategyCandidate> RejectedCandidates { get; init; } = [];

    public StrategyNoTradeReason NoTradeReason { get; init; } = StrategyNoTradeReason.None;

    public string Reason { get; init; } = string.Empty;

    public bool IsTradeAllowed { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public BreakoutClassification BreakoutClassification { get; init; } = BreakoutClassification.Unclear;

    public StrategySide BreakoutSide { get; init; } = StrategySide.None;
}

public sealed class BreakoutClassifierResult
{
    public BreakoutClassification Classification { get; init; } = BreakoutClassification.Unclear;

    public StrategySide BreakoutSide { get; init; } = StrategySide.None;

    public decimal ScoreModifierForSweep { get; init; }

    public decimal ScoreModifierForTurtle { get; init; }

    public bool BlocksSweep { get; init; }

    public bool BoostsTurtle { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed class NySessionRange
{
    public decimal Upper { get; init; }

    public decimal Lower { get; init; }

    public NyRangeMode Mode { get; init; }

    public DateTimeOffset RangeStartUtc { get; init; }

    public DateTimeOffset RangeEndUtc { get; init; }
}

public sealed class NyStrategyContext
{
    public string Symbol { get; init; } = string.Empty;

    public IReadOnlyList<Candle> FiveMinuteCandles { get; init; } = [];

    public IReadOnlyList<Candle> FifteenMinuteCandles { get; init; } = [];

    public IReadOnlyList<Candle> TurtleCandles { get; init; } = [];

    public IReadOnlyList<Candle> BtcFifteenMinuteCandles { get; init; } = [];

    public NySessionRange Range { get; init; } = new();

    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    public decimal EntryNotionalUsdt { get; init; }

    public decimal RewardRisk { get; init; }
}
