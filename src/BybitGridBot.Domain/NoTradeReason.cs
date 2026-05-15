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
    DailyLossLimitReached
}
