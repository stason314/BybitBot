using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class FuturesStrategyRouter
{
    private readonly IReadOnlyDictionary<FuturesStrategyType, IFuturesStrategy> _strategies;

    public FuturesStrategyRouter(IEnumerable<IFuturesStrategy> strategies)
    {
        _strategies = strategies.ToDictionary(strategy => strategy.StrategyType);
    }

    public FuturesStrategyDecision Decide(FuturesStrategyContext context)
    {
        if (!_strategies.TryGetValue(context.Settings.StrategyType, out var strategy))
        {
            return FuturesStrategyDecision.Empty($"Unsupported futures strategy: {context.Settings.StrategyType}.");
        }

        return strategy.Decide(context);
    }
}
