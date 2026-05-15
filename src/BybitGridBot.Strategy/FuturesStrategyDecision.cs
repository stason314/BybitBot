using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class FuturesStrategyDecision
{
    public IReadOnlyList<FuturesTradeIntent> TradeIntents { get; init; } = [];

    public string? Reason { get; init; }

    public static FuturesStrategyDecision Empty(string? reason = null) => new()
    {
        Reason = reason
    };
}
