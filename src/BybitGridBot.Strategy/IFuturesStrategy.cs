using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public interface IFuturesStrategy
{
    FuturesStrategyType StrategyType { get; }

    FuturesStrategyDecision Decide(FuturesStrategyContext context);
}
