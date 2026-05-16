using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.App;

public static class FuturesAutoRecommendationSafety
{
    public static bool CanApply(
        FuturesAutoConfigRecommendation recommendation,
        FuturesPositionSnapshot position,
        bool shortsAllowed,
        out string reason)
    {
        if (recommendation.Direction != FuturesDirection.LongOnly && !shortsAllowed)
        {
            reason = $"Auto recommendation {recommendation.StrategyType}/{recommendation.Direction} requires paper mode or FUTURES_TESTNET_SHORTS_ENABLED=true.";
            return false;
        }

        if (position.Size > 0m && IsLong(position.Side) && IsShortOnlyStrategy(recommendation.StrategyType))
        {
            reason = $"Position side Buy is incompatible with auto recommendation {recommendation.StrategyType}. Close long first before switching short.";
            return false;
        }

        if (position.Size > 0m && IsShort(position.Side) && IsLongOnlyStrategy(recommendation.StrategyType))
        {
            reason = $"Position side Sell is incompatible with auto recommendation {recommendation.StrategyType}. Close short first before switching long.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool IsPositionIncompatible(FuturesBotSettings settings, FuturesPositionSnapshot position) =>
        position.Size > 0m &&
        (IsShort(position.Side) && IsLongOnlyStrategy(settings.StrategyType) ||
            IsLong(position.Side) && IsShortOnlyStrategy(settings.StrategyType));

    public static FuturesStrategyType ResolveCompatibleStrategy(FuturesStrategyType strategyType, string positionSide)
    {
        if (IsShort(positionSide) && IsLongOnlyStrategy(strategyType))
        {
            return strategyType switch
            {
                FuturesStrategyType.GridLongOnly => FuturesStrategyType.GridShortOnly,
                FuturesStrategyType.Breakout => FuturesStrategyType.BreakdownShort,
                _ => FuturesStrategyType.TrendFollowShortOnly
            };
        }

        if (IsLong(positionSide) && IsShortOnlyStrategy(strategyType))
        {
            return strategyType switch
            {
                FuturesStrategyType.GridShortOnly => FuturesStrategyType.GridLongOnly,
                FuturesStrategyType.BreakdownShort => FuturesStrategyType.Breakout,
                _ => FuturesStrategyType.TrendFollow
            };
        }

        return strategyType;
    }

    public static bool IsLongOnlyStrategy(FuturesStrategyType strategyType) =>
        strategyType is FuturesStrategyType.TrendFollow or FuturesStrategyType.Breakout or FuturesStrategyType.GridLongOnly;

    public static bool IsShortOnlyStrategy(FuturesStrategyType strategyType) =>
        strategyType is FuturesStrategyType.TrendFollowShortOnly or FuturesStrategyType.BreakdownShort or FuturesStrategyType.GridShortOnly;

    private static bool IsLong(string side) =>
        string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Long", StringComparison.OrdinalIgnoreCase);

    private static bool IsShort(string side) =>
        string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Short", StringComparison.OrdinalIgnoreCase);
}
