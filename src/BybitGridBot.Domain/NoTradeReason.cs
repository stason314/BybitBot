namespace BybitGridBot.Domain;

public enum NoTradeReason
{
    None,
    NotEnoughData,
    UnknownMarketPhase,
    HighVolatility,
    DumpDetected,
    StrategyCooldown,
    ScoreTooLow,
    ConfidenceTooLow,
    RiskRejected,
    CapitalRejected,
    ExpectedProfitTooLow,
    BtcRiskOff,
    PriceOutsideRange,
    DailyLossLimitReached,
    MaxPositionReached,
    AggressiveCooldown,
    AggressiveStopLoss,
    AggressiveReenabled,
    UnparentedSellCleanup
}

public sealed class NoTradeReasonRecord
{
    public string Symbol { get; init; } = string.Empty;

    public string? StrategyType { get; init; }

    public NoTradeReason ReasonCode { get; init; } = NoTradeReason.None;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
