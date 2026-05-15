using BybitGridBot.Domain;
using BybitGridBot.Strategy;
using System.Text.Json;

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
    public void Recommend_UsesWideStopBoundaries()
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

        var lowerStopDistance = recommendation.LowerPrice - recommendation.StopLowerPrice;
        var upperStopDistance = recommendation.StopUpperPrice - recommendation.UpperPrice;
        var gridWidth = recommendation.UpperPrice - recommendation.LowerPrice;

        Assert.True(lowerStopDistance >= gridWidth * 0.5m);
        Assert.True(upperStopDistance >= gridWidth * 0.5m);
    }

    [Fact]
    public void RecommendForStrategy_KeepsRequestedStrategy()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 0.22m,
            UpperPrice = 0.24m,
            Step = 0.001m,
            OrderSizeUsdt = 15m,
            MinOrderSizeUsdt = 15m,
            StopLowerPrice = 0.20m,
            StopUpperPrice = 0.26m
        };
        var candles = BuildCandles(0.23m, 0.235m, 0.221m, 30);

        var recommendation = selector.RecommendForStrategy(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Danger,
                MovePercent = 5m,
                RangePercent = 10m,
                Recommendation = "Danger",
                Support = 0.221m,
                Resistance = 0.235m
            },
            candles,
            TradingStrategyType.Hybrid);

        using var config = JsonDocument.Parse(recommendation.StrategyConfigJson);

        Assert.Equal(TradingStrategyType.Hybrid, recommendation.StrategyType);
        Assert.Equal(15m, config.RootElement.GetProperty("orderSizeUsdt").GetDecimal());
        Assert.Equal(15m, config.RootElement.GetProperty("trendOrderSizeUsdt").GetDecimal());
        Assert.Equal(60, config.RootElement.GetProperty("breakoutLookbackCandles").GetInt32());
        Assert.Equal(15m, config.RootElement.GetProperty("signalOrderSizeUsdt").GetDecimal());
        Assert.True(recommendation.StopLowerPrice < recommendation.LowerPrice);
        Assert.True(recommendation.StopUpperPrice > recommendation.UpperPrice);
    }

    [Fact]
    public void RecommendForStrategy_BuildsTrendFollowConfig()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 2.0m,
            UpperPrice = 2.2m,
            Step = 0.01m,
            OrderSizeUsdt = 20m,
            MinOrderSizeUsdt = 15m,
            StopLowerPrice = 1.9m,
            StopUpperPrice = 2.3m
        };
        var candles = BuildCandles(2.10m, 2.14m, 2.08m, 30);

        var recommendation = selector.RecommendForStrategy(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Breakout,
                MovePercent = 1.2m,
                RangePercent = 2m,
                Recommendation = "Breakout",
                Support = 2.08m,
                Resistance = 2.14m
            },
            candles,
            TradingStrategyType.TrendFollow);

        using var config = JsonDocument.Parse(recommendation.StrategyConfigJson);

        Assert.Equal(TradingStrategyType.TrendFollow, recommendation.StrategyType);
        Assert.Equal(15m, config.RootElement.GetProperty("trendOrderSizeUsdt").GetDecimal());
        Assert.Equal(0.08m, config.RootElement.GetProperty("minTrendStrengthPercent").GetDecimal());
        Assert.Equal(1.2m, config.RootElement.GetProperty("minVolumeRatio").GetDecimal());
        Assert.Equal(60, config.RootElement.GetProperty("breakoutLookbackCandles").GetInt32());
        Assert.True(recommendation.StopLowerPrice < recommendation.LowerPrice);
        Assert.True(recommendation.StopUpperPrice > recommendation.UpperPrice);
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

    [Fact]
    public void Recommend_ReturnsNoTrade_ForLowVolatility()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 2.0m,
            UpperPrice = 2.2m,
            Step = 0.01m,
            OrderSizeUsdt = 20m,
            MinOrderSizeUsdt = 10m,
            StopLowerPrice = 1.9m,
            StopUpperPrice = 2.3m
        };
        var candles = BuildCandles(2.10m, 2.105m, 2.095m, 30);

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.LowVolatility,
                Recommendation = "Low volatility",
                Support = 2.095m,
                Resistance = 2.105m
            },
            candles);

        Assert.Equal(TradingStrategyType.NoTrade, recommendation.StrategyType);
        Assert.Equal("{}", recommendation.StrategyConfigJson);
    }

    [Fact]
    public void Recommend_RespectsConfiguredMinimumOrderSize()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 2.0m,
            UpperPrice = 2.2m,
            Step = 0.01m,
            OrderSizeUsdt = 20m,
            MinOrderSizeUsdt = 25m,
            StopLowerPrice = 1.9m,
            StopUpperPrice = 2.3m
        };
        var candles = BuildCandles(2.10m, 2.17m, 2.08m, 30);

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Trend,
                MovePercent = 1.2m,
                Recommendation = "Trend up",
                Support = 2.08m,
                Resistance = 2.17m
            },
            candles);

        using var config = JsonDocument.Parse(recommendation.StrategyConfigJson);

        Assert.Equal(TradingStrategyType.Combo, recommendation.StrategyType);
        Assert.Equal(25m, recommendation.OrderSizeUsdt);
        Assert.Equal(25m, config.RootElement.GetProperty("orderSizeUsdt").GetDecimal());
    }

    [Fact]
    public void Recommend_ReturnsSignal_ForConfirmedBreakoutSignal()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 2.0m,
            UpperPrice = 2.2m,
            Step = 0.01m,
            OrderSizeUsdt = 20m,
            MinOrderSizeUsdt = 10m,
            StopLowerPrice = 1.9m,
            StopUpperPrice = 2.3m
        };
        var candles = BuildBullishSignalCandles();

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Breakout,
                MovePercent = 1.1m,
                VolumeRatio = 2.2m,
                Recommendation = "Breakout",
                Support = candles.Min(candle => candle.Low),
                Resistance = candles.Max(candle => candle.High)
            },
            candles);

        using var config = JsonDocument.Parse(recommendation.StrategyConfigJson);

        Assert.Equal(TradingStrategyType.Signal, recommendation.StrategyType);
        Assert.Contains("Signal Bot", recommendation.Reason);
        Assert.Equal(0.65m, config.RootElement.GetProperty("minConfidence").GetDecimal());
        Assert.Equal(30, config.RootElement.GetProperty("cooldownMinutes").GetInt32());
    }

    [Fact]
    public void Recommend_ReturnsBtd_ForLowerBollingerDipInsteadOfSignal()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 0.19m,
            UpperPrice = 0.21m,
            Step = 0.001m,
            OrderSizeUsdt = 20m,
            MinOrderSizeUsdt = 15m,
            StopLowerPrice = 0.18m,
            StopUpperPrice = 0.22m
        };
        var candles = BuildLowerBollingerDipCandles();

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Trend,
                MovePercent = -0.8m,
                Recommendation = "Pullback",
                Support = candles.Min(candle => candle.Low),
                Resistance = candles.Max(candle => candle.High)
            },
            candles);

        Assert.Equal(TradingStrategyType.Btd, recommendation.StrategyType);
        Assert.Contains("Dip setup", recommendation.Reason);
        Assert.Contains("dipPercent", recommendation.StrategyConfigJson);
    }

    [Fact]
    public void Recommend_ReturnsCombo_ForVolatileRangeWithoutDanger()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 0.195m,
            UpperPrice = 0.21m,
            Step = 0.0005m,
            OrderSizeUsdt = 15m,
            MinOrderSizeUsdt = 15m,
            StopLowerPrice = 0.19m,
            StopUpperPrice = 0.215m
        };
        var candles = BuildVolatileRangeCandles();

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Breakout,
                MovePercent = 0.4m,
                RangePercent = 3.2m,
                VolumeRatio = 1.7m,
                Recommendation = "Volatile range",
                Support = candles.Min(candle => candle.Low),
                Resistance = candles.Max(candle => candle.High)
            },
            candles);

        using var config = JsonDocument.Parse(recommendation.StrategyConfigJson);

        Assert.Equal(TradingStrategyType.Combo, recommendation.StrategyType);
        Assert.Contains("Volatile range", recommendation.Reason);
        Assert.Equal(20m, recommendation.OrderSizeUsdt);
        Assert.Equal(15, config.RootElement.GetProperty("buyIntervalMinutes").GetInt32());
        Assert.Equal(3, config.RootElement.GetProperty("maxActiveBuyOrders").GetInt32());
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

    private static IReadOnlyList<Candle> BuildBullishSignalCandles()
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, 45)
            .Select(index =>
            {
                var close = 100m + index * 0.03m + (index % 2 == 0 ? -0.08m : 0.08m);
                return new Candle(
                    now.AddMinutes(index - 45),
                    close,
                    close + 0.05m,
                    close - 0.05m,
                    close,
                    index >= 40 ? 2200m : 1000m,
                    close * (index >= 40 ? 2200m : 1000m));
            })
            .ToArray();
    }

    private static IReadOnlyList<Candle> BuildLowerBollingerDipCandles()
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, 45)
            .Select(index =>
            {
                var close = index < 30
                    ? 100m + (index % 2 == 0 ? -0.03m : 0.03m)
                    : 100m - (index - 29) * 0.05m + (index % 2 == 0 ? -0.02m : 0.02m);
                return new Candle(
                    now.AddMinutes(index - 45),
                    close,
                    close + 0.05m,
                    close - 0.05m,
                    close,
                    1000m,
                    close * 1000m);
            })
            .ToArray();
    }

    private static IReadOnlyList<Candle> BuildVolatileRangeCandles()
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, 45)
            .Select(index =>
            {
                var close = 100m + (index % 6 - 2.5m) * 0.35m;
                return new Candle(
                    now.AddMinutes(index - 45),
                    close,
                    close + 0.25m,
                    close - 0.25m,
                    close,
                    1000m,
                    close * 1000m);
            })
            .ToArray();
    }
}
