namespace BybitGridBot.Domain;

public enum RotationPairStatus
{
    Active,
    Waiting,
    WaitingOrder,
    InPosition,
    Dormant,
    Closed,
    LockedPosition,
    Candidate,
    Cooldown,
    Disabled
}

public sealed record RotationStateRecord
{
    public bool RotationEnabled { get; init; }

    public int ActivePairPoolSize { get; init; }

    public int ScanIntervalMinutes { get; init; }

    public int MinPairLifetimeMinutes { get; init; }

    public decimal ReplacementScoreGap { get; init; }

    public bool AllowReplaceOnlyWhenFlat { get; init; }

    public int MaxActivePositions { get; init; }

    public RotationMode RotationMode { get; init; } = RotationMode.PaperOnly;

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? StoppedAt { get; init; }

    public DateTimeOffset? LastScanAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ActivePairSlotRecord
{
    public int SlotIndex { get; init; }

    public string? Symbol { get; init; }

    public string Category { get; init; } = "spot";

    public RotationPairStatus Status { get; init; } = RotationPairStatus.Waiting;

    public decimal Score { get; init; }

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset ActivatedAt { get; init; }

    public DateTimeOffset? CooldownUntil { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record PairRotationHistoryRecord
{
    public long RotationHistoryId { get; init; }

    public int SlotIndex { get; init; }

    public string? PreviousSymbol { get; init; }

    public string NewSymbol { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public decimal PreviousScore { get; init; }

    public decimal NewScore { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record StrategyPerformanceScoreRecord
{
    public string Symbol { get; init; } = string.Empty;

    public string StrategyType { get; init; } = string.Empty;

    public decimal Score { get; init; }

    public decimal NetPnl { get; init; }

    public decimal WinRate { get; init; }

    public int TradesCount { get; init; }

    public string MetricsJson { get; init; } = "{}";

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record PairStrategyScoreRecord
{
    public string Symbol { get; init; } = string.Empty;

    public string Category { get; init; } = "spot";

    public string StrategyType { get; init; } = string.Empty;

    public decimal Score { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string MetricsJson { get; init; } = "{}";

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record RotationDecisionRecord
{
    public long RotationDecisionId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string? Symbol { get; init; }

    public string? CandidateSymbol { get; init; }

    public int? SlotIndex { get; init; }

    public decimal CurrentScore { get; init; }

    public decimal CandidateScore { get; init; }

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
}
