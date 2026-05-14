using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class AutoStrategySelectorTests
{
    [Fact]
    public void Recommend_ReturnsGrid_ForRangeRegime()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 2.0m,
            UpperPrice = 2.2m,
            Step = 0.01m,
            OrderSizeUsdt = 20m,
            StopLowerPrice = 1.9m,
            StopUpperPrice = 2.3m
        };
        var candles = BuildCandles(2.10m, 2.12m, 2.08m, 30);

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Range,
                Recommendation = "Range",
                Support = 2.08m,
                Resistance = 2.12m
            },
            candles);

        Assert.Equal(TradingStrategyType.Grid, recommendation.StrategyType);
        Assert.Equal("{}", recommendation.StrategyConfigJson);
    }

    [Fact]
    public void Recommend_ReturnsBtd_ForDowntrend()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 0.19m,
            UpperPrice = 0.21m,
            Step = 0.001m,
            OrderSizeUsdt = 20m,
            StopLowerPrice = 0.18m,
            StopUpperPrice = 0.22m
        };
        var candles = BuildCandles(0.20m, 0.205m, 0.19m, 30);

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Trend,
                MovePercent = -1.5m,
                Recommendation = "Trend down",
                Support = 0.19m,
                Resistance = 0.205m
            },
            candles);

        Assert.Equal(TradingStrategyType.Btd, recommendation.StrategyType);
        Assert.Contains("dipPercent", recommendation.StrategyConfigJson);
    }

    [Fact]
    public void Recommend_ReturnsNoTrade_ForDangerRegime()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 2.0m,
            UpperPrice = 2.2m,
            Step = 0.01m,
            OrderSizeUsdt = 20m,
            StopLowerPrice = 1.9m,
            StopUpperPrice = 2.3m
        };
        var candles = BuildCandles(2.10m, 2.20m, 2.00m, 30);

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Danger,
                Recommendation = "Danger",
                Support = 2.0m,
                Resistance = 2.2m
            },
            candles);

        Assert.Equal(TradingStrategyType.NoTrade, recommendation.StrategyType);
        Assert.Equal("{}", recommendation.StrategyConfigJson);
    }

    private static IReadOnlyList<Candle> BuildCandles(decimal open, decimal high, decimal low, int count)
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, count)
            .Select(index => new Candle(
                now.AddMinutes(index - count),
                open,
                high,
                low,
                open,
                1000m,
                1000m * open))
            .ToArray();
    }
}
