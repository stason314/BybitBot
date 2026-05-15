namespace BybitGridBot.Domain;

public enum MarketPhase
{
    Unknown,
    Reversal,
    Uptrend,
    PullbackInUptrend,
    RangeBound,
    BreakoutUp,
    BreakoutDown,
    Dump,
    HighVolatility,
    Exhaustion
}
