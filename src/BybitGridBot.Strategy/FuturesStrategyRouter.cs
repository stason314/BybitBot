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
        var strategyType = ResolvePositionAwareStrategy(context);
        if (!_strategies.TryGetValue(strategyType, out var strategy))
        {
            return FuturesStrategyDecision.Empty($"Unsupported futures strategy: {strategyType}.");
        }

        return strategy.Decide(context);
    }

    private static FuturesStrategyType ResolvePositionAwareStrategy(FuturesStrategyContext context)
    {
        if (context.Position.Size <= 0m)
        {
            return context.Settings.StrategyType;
        }

        if (IsShort(context.Position.Side) && IsLongOnlyStrategy(context.Settings.StrategyType))
        {
            return context.Settings.StrategyType switch
            {
                FuturesStrategyType.GridLongOnly => FuturesStrategyType.GridShortOnly,
                FuturesStrategyType.Breakout => FuturesStrategyType.BreakdownShort,
                _ => FuturesStrategyType.TrendFollowShortOnly
            };
        }

        if (IsLong(context.Position.Side) && IsShortOnlyStrategy(context.Settings.StrategyType))
        {
            return context.Settings.StrategyType switch
            {
                FuturesStrategyType.GridShortOnly => FuturesStrategyType.GridLongOnly,
                FuturesStrategyType.BreakdownShort => FuturesStrategyType.Breakout,
                _ => FuturesStrategyType.TrendFollow
            };
        }

        return context.Settings.StrategyType;
    }

    private static bool IsLongOnlyStrategy(FuturesStrategyType strategyType) =>
        strategyType is FuturesStrategyType.TrendFollow or FuturesStrategyType.Breakout or FuturesStrategyType.GridLongOnly;

    private static bool IsShortOnlyStrategy(FuturesStrategyType strategyType) =>
        strategyType is FuturesStrategyType.TrendFollowShortOnly or FuturesStrategyType.BreakdownShort or FuturesStrategyType.GridShortOnly;

    private static bool IsLong(string side) =>
        string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Long", StringComparison.OrdinalIgnoreCase);

    private static bool IsShort(string side) =>
        string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Short", StringComparison.OrdinalIgnoreCase);
}
