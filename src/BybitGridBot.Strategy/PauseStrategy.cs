using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class PauseStrategy : ITradingStrategy
{
    public TradingStrategyType Type => TradingStrategyType.Pause;

    public string DisplayName => "Pause";

    public IReadOnlyList<TradeIntent> BuildTradeIntents(string reason) => [];

    public StrategyScore Score(string reason) => new()
    {
        StrategyType = StrategyType.Pause,
        Score = 100m,
        Confidence = 1m,
        RequiredCapitalPercent = 0m,
        IsAllowed = true,
        Reason = reason
    };
}
