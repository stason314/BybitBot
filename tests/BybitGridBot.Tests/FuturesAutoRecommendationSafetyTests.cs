using BybitGridBot.App;
using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class FuturesAutoRecommendationSafetyTests
{
    [Fact]
    public void CanApply_BlocksLongRecommendationForOpenShort()
    {
        var allowed = FuturesAutoRecommendationSafety.CanApply(
            Recommendation(FuturesStrategyType.TrendFollow, FuturesDirection.LongOnly),
            Position("Sell"),
            shortsAllowed: true,
            out var reason);

        Assert.False(allowed);
        Assert.Contains("Position side Sell", reason, StringComparison.Ordinal);
        Assert.Contains("Close short first", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CanApply_AllowsShortRecommendationForOpenShort()
    {
        var allowed = FuturesAutoRecommendationSafety.CanApply(
            Recommendation(FuturesStrategyType.TrendFollowShortOnly, FuturesDirection.ShortOnly),
            Position("Sell"),
            shortsAllowed: true,
            out var reason);

        Assert.True(allowed);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void CanApply_BlocksShortRecommendationWhenShortsAreDisabled()
    {
        var allowed = FuturesAutoRecommendationSafety.CanApply(
            Recommendation(FuturesStrategyType.TrendFollowShortOnly, FuturesDirection.ShortOnly),
            Position("None", size: 0m),
            shortsAllowed: false,
            out var reason);

        Assert.False(allowed);
        Assert.Contains("FUTURES_TESTNET_SHORTS_ENABLED", reason, StringComparison.Ordinal);
    }

    private static FuturesAutoConfigRecommendation Recommendation(
        FuturesStrategyType strategyType,
        FuturesDirection direction) => new()
    {
        StrategyType = strategyType,
        Direction = direction,
        Reason = "test",
        Leverage = 2m,
        MaxNotionalUsdt = 10m,
        MaxMarginUsdt = 5m,
        StopLossPercent = 2m,
        TakeProfitPercent = 4m,
        LiquidationBufferPercent = 15m,
        StrategyConfigJson = "{}"
    };

    private static FuturesPositionSnapshot Position(string side, decimal size = 1m) => new()
    {
        Symbol = "BTCUSDT",
        Category = "linear",
        Side = side,
        Size = size
    };
}
