namespace BybitGridBot.Domain;

public enum SignalType
{
    Buy,
    Sell,
    Hold,
    Avoid,
    EmaCross,
    RsiOversold,
    RsiOverbought,
    RangeSupportBounce,
    RangeResistanceReject,
    BreakoutUp,
    BreakoutDown,
    VolumeSpike,
    BtcRiskOff,
    HighVolatility
}
