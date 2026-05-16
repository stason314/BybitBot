using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class FuturesAutoConfigRecommenderTests
{
    [Fact]
    public void Recommend_SelectsTrendFollow_ForPositiveTrend()
    {
        var recommendation = new FuturesAutoConfigRecommender().Recommend(
            Settings(),
            Candles(start: 50000m, step: 30m, count: 80),
            hasOpenPosition: false);

        Assert.Equal(FuturesStrategyType.TrendFollow, recommendation.StrategyType);
        Assert.Equal(FuturesDirection.LongOnly, recommendation.Direction);
        Assert.Equal(FuturesMarginMode.Isolated, recommendation.MarginMode);
        Assert.Equal(FuturesPositionMode.OneWay, recommendation.PositionMode);
        Assert.True(recommendation.ReduceOnlyEnabled);
        Assert.Contains("reduceOnlyOnExit", recommendation.StrategyConfigJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Recommend_SelectsPause_WhenDangerAndNoPosition()
    {
        var recommendation = new FuturesAutoConfigRecommender().Recommend(
            Settings(),
            Candles(start: 50000m, step: -60m, count: 80),
            hasOpenPosition: false);

        Assert.Equal(FuturesStrategyType.Pause, recommendation.StrategyType);
    }

    [Fact]
    public void Recommend_SelectsReduceOnly_WhenDangerAndPositionOpen()
    {
        var recommendation = new FuturesAutoConfigRecommender().Recommend(
            Settings(),
            Candles(start: 50000m, step: -60m, count: 80),
            hasOpenPosition: true);

        Assert.Equal(FuturesStrategyType.ReduceOnly, recommendation.StrategyType);
    }

    [Fact]
    public void Recommend_SelectsShortTrend_WhenDirectionAllowsShorts()
    {
        var recommendation = new FuturesAutoConfigRecommender().Recommend(
            Settings(FuturesDirection.ShortOnly),
            Candles(start: 50000m, step: -60m, count: 80),
            hasOpenPosition: false);

        Assert.Equal(FuturesStrategyType.TrendFollowShortOnly, recommendation.StrategyType);
        Assert.Equal(FuturesDirection.ShortOnly, recommendation.Direction);
        Assert.Contains("shortOnly", recommendation.StrategyConfigJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Recommend_DoesNotCompoundProfileLimits_AfterApply()
    {
        var recommender = new FuturesAutoConfigRecommender();
        var candles = Candles(start: 50000m, step: 0m, count: 80);
        var first = recommender.Recommend(Settings(), candles, hasOpenPosition: false);

        var appliedSettings = new FuturesBotSettings
        {
            Symbol = "BTCUSDT",
            Category = "linear",
            StrategyType = first.StrategyType,
            StrategyConfigJson = first.StrategyConfigJson,
            Leverage = first.Leverage,
            MarginMode = FuturesMarginMode.Isolated,
            PositionMode = FuturesPositionMode.OneWay,
            Direction = FuturesDirection.LongOnly,
            MaxNotionalUsdt = first.MaxNotionalUsdt,
            MaxMarginUsdt = first.MaxMarginUsdt,
            StopLossPercent = first.StopLossPercent,
            TakeProfitPercent = first.TakeProfitPercent,
            LiquidationBufferPercent = 15m,
            ReduceOnlyEnabled = true
        };

        var second = recommender.Recommend(appliedSettings, candles, hasOpenPosition: false);

        Assert.Equal(FuturesStrategyType.GridLongOnly, first.StrategyType);
        Assert.Equal(first.MaxNotionalUsdt, second.MaxNotionalUsdt);
        Assert.Equal(first.MaxMarginUsdt, second.MaxMarginUsdt);
    }

    private static FuturesBotSettings Settings(FuturesDirection direction = FuturesDirection.LongOnly) => new()
    {
        Symbol = "BTCUSDT",
        Category = "linear",
        StrategyType = FuturesStrategyType.Pause,
        Leverage = 2m,
        MarginMode = FuturesMarginMode.Isolated,
        PositionMode = FuturesPositionMode.OneWay,
        Direction = direction,
        MaxNotionalUsdt = 100m,
        MaxMarginUsdt = 50m,
        StopLossPercent = 2m,
        TakeProfitPercent = 4m,
        LiquidationBufferPercent = 15m,
        ReduceOnlyEnabled = true
    };

    private static IReadOnlyList<Candle> Candles(decimal start, decimal step, int count)
    {
        var candles = new List<Candle>();
        var price = start;
        for (var index = 0; index < count; index++)
        {
            var open = price;
            var close = price + step;
            var high = decimal.Max(open, close) + 20m;
            var low = decimal.Min(open, close) - 20m;
            candles.Add(new Candle(DateTimeOffset.UtcNow.AddMinutes(index), open, high, low, close, 1m, close));
            price = close;
        }

        return candles;
    }
}
