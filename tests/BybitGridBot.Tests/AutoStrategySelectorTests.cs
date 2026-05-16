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
    public void Recommend_UsesProfitableGridStep()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 9.9m,
            UpperPrice = 10.2m,
            Step = 0.03m,
            OrderSizeUsdt = 15m,
            StopLowerPrice = 9.7m,
            StopUpperPrice = 10.4m
        };
        var candles = BuildCandles(10.05m, 10.07m, 10.03m, 60);

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Range,
                Recommendation = "Range",
                Support = 10.03m,
                Resistance = 10.07m
            },
            candles);

        var expectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(
            recommendation.LowerPrice,
            recommendation.LowerPrice + recommendation.Step);

        Assert.True(expectedProfitPercent > ExpectedProfitFilter.RequiredPercent(options));
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
        Assert.True(config.RootElement.GetProperty("takeProfitLadderEnabled").GetBoolean());
        Assert.Equal(0.45m, config.RootElement.GetProperty("takeProfitLadderFirstPercent").GetDecimal());
        Assert.Equal(50m, config.RootElement.GetProperty("takeProfitLadderFirstQuantityPercent").GetDecimal());
        Assert.Equal(0.9m, config.RootElement.GetProperty("takeProfitLadderSecondPercent").GetDecimal());
        Assert.Equal(30m, config.RootElement.GetProperty("takeProfitLadderSecondQuantityPercent").GetDecimal());
        Assert.Equal(1.5m, config.RootElement.GetProperty("takeProfitLadderFinalPercent").GetDecimal());
        Assert.Equal(20m, config.RootElement.GetProperty("takeProfitLadderFinalQuantityPercent").GetDecimal());
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
    public void Recommend_ReturnsSmallReversalBtd_AfterConfirmedDumpStabilization()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 0.15m,
            UpperPrice = 0.18m,
            Step = 0.001m,
            OrderSizeUsdt = 20m,
            MinOrderSizeUsdt = 10m,
            StopLowerPrice = 0.13m,
            StopUpperPrice = 0.20m
        };
        var candles = BuildReversalAfterDumpCandles();

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Danger,
                MovePercent = -12m,
                RangePercent = 18m,
                VolumeRatio = 1.8m,
                Recommendation = "Danger",
                Support = candles.Min(candle => candle.Low),
                Resistance = candles.Max(candle => candle.High)
            },
            new MarketPhaseResult
            {
                Phase = MarketPhase.Dump,
                Confidence = 0.9m,
                Score = 92m,
                Reason = "Dump detected, but lows have stabilized.",
                SuggestedStrategy = StrategyType.Pause,
                DetectedAt = DateTimeOffset.UtcNow
            },
            candles);

        using var config = JsonDocument.Parse(recommendation.StrategyConfigJson);

        Assert.Equal(TradingStrategyType.Btd, recommendation.StrategyType);
        Assert.Contains("Reversal BTD after dump", recommendation.Reason);
        Assert.Equal(10m, recommendation.OrderSizeUsdt);
        Assert.True(config.RootElement.GetProperty("reversalMode").GetBoolean());
        Assert.True(config.RootElement.GetProperty("takeProfitLadderEnabled").GetBoolean());
        Assert.Equal(2, config.RootElement.GetProperty("maxBuys").GetInt32());
    }

    [Fact]
    public void Recommend_KeepsNoTrade_WhenDumpHasNoStabilization()
    {
        var selector = new AutoStrategySelector();
        var options = new GridOptions
        {
            LowerPrice = 0.15m,
            UpperPrice = 0.18m,
            Step = 0.001m,
            OrderSizeUsdt = 20m,
            MinOrderSizeUsdt = 10m,
            StopLowerPrice = 0.13m,
            StopUpperPrice = 0.20m
        };
        var candles = BuildContinuingDumpCandles();

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Danger,
                MovePercent = -14m,
                RangePercent = 20m,
                Recommendation = "Danger",
                Support = candles.Min(candle => candle.Low),
                Resistance = candles.Max(candle => candle.High)
            },
            new MarketPhaseResult
            {
                Phase = MarketPhase.Dump,
                Confidence = 0.9m,
                Score = 92m,
                Reason = "Dump still making new lows.",
                SuggestedStrategy = StrategyType.Pause,
                DetectedAt = DateTimeOffset.UtcNow
            },
            candles);

        Assert.Equal(TradingStrategyType.NoTrade, recommendation.StrategyType);
        Assert.DoesNotContain("Reversal BTD", recommendation.Reason);
    }

    [Fact]
    public void Recommend_UsesCombo_WhenBtdPhaseIsBullishOverextended()
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
        var candles = BuildOverextendedBullishCandles();

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Trend,
                MovePercent = 1.4m,
                Recommendation = "Overextended trend",
                Support = candles.Min(candle => candle.Low),
                Resistance = candles.Max(candle => candle.High)
            },
            new MarketPhaseResult
            {
                Phase = MarketPhase.PullbackInUptrend,
                Confidence = 0.8m,
                Score = 80m,
                Reason = "Pullback phase, but signal is already stretched.",
                SuggestedStrategy = StrategyType.Btd,
                DetectedAt = DateTimeOffset.UtcNow
            },
            candles);

        Assert.Equal(TradingStrategyType.Combo, recommendation.StrategyType);
        Assert.Contains("BTD is avoided", recommendation.Reason);
        Assert.Contains("maxActiveBuyOrders", recommendation.StrategyConfigJson);
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

    [Theory]
    [InlineData(MarketPhase.RangeBound, TradingStrategyType.Grid)]
    [InlineData(MarketPhase.Uptrend, TradingStrategyType.TrendFollowing)]
    [InlineData(MarketPhase.PullbackInUptrend, TradingStrategyType.Btd)]
    [InlineData(MarketPhase.BreakoutUp, TradingStrategyType.Breakout)]
    [InlineData(MarketPhase.Dump, TradingStrategyType.NoTrade)]
    [InlineData(MarketPhase.HighVolatility, TradingStrategyType.NoTrade)]
    [InlineData(MarketPhase.BreakoutDown, TradingStrategyType.NoTrade)]
    public void Recommend_UsesMarketPhase_AsPrimaryAutoSelector(MarketPhase phase, TradingStrategyType expectedStrategy)
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
        var candles = BuildCandles(2.10m, 2.14m, 2.08m, 60);
        var phaseResult = new MarketPhaseResult
        {
            Phase = phase,
            Confidence = 0.8m,
            Score = 80m,
            Reason = "test phase",
            SuggestedStrategy = StrategyType.Grid,
            DetectedAt = DateTimeOffset.UtcNow
        };

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Range,
                Recommendation = "Range",
                Support = 2.08m,
                Resistance = 2.14m
            },
            phaseResult,
            candles);

        Assert.Equal(expectedStrategy, recommendation.StrategyType);
    }

    [Fact]
    public void Recommend_UsesNoTradeForUnknownPhase_WhenAggressiveModeInactive()
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
        var candles = BuildCandles(2.10m, 2.14m, 2.08m, 60);

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Trend,
                MovePercent = -1.2m,
                Recommendation = "Downtrend",
                Support = 2.08m,
                Resistance = 2.14m
            },
            UnknownPhase(),
            candles,
            aggressiveModeActive: false);

        Assert.Equal(TradingStrategyType.NoTrade, recommendation.StrategyType);
    }

    [Fact]
    public void Recommend_UsesComboForUnknownDowntrend_WhenAggressiveModeActive()
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
        var candles = BuildCandles(2.10m, 2.14m, 2.08m, 60);

        var recommendation = selector.Recommend(
            options,
            new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Trend,
                MovePercent = -1.2m,
                Recommendation = "Downtrend",
                Support = 2.08m,
                Resistance = 2.14m
            },
            UnknownPhase(),
            candles,
            aggressiveModeActive: true);

        Assert.Equal(TradingStrategyType.Combo, recommendation.StrategyType);
        Assert.Contains("Baseline was Btd", recommendation.Reason);
        Assert.Contains("maxActiveBuyOrders", recommendation.StrategyConfigJson);
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

    private static MarketPhaseResult UnknownPhase()
    {
        return new MarketPhaseResult
        {
            Phase = MarketPhase.Unknown,
            Confidence = 0.35m,
            Score = 35m,
            Reason = "No reliable phase match.",
            SuggestedStrategy = StrategyType.Pause,
            DetectedAt = DateTimeOffset.UtcNow
        };
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

    private static IReadOnlyList<Candle> BuildOverextendedBullishCandles()
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, 45)
            .Select(index =>
            {
                var close = 100m + index * 0.18m;
                return new Candle(
                    now.AddMinutes(index - 45),
                    close,
                    close + 0.08m,
                    close - 0.04m,
                    close,
                    index >= 38 ? 1800m : 1000m,
                    close * (index >= 38 ? 1800m : 1000m));
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

    private static IReadOnlyList<Candle> BuildReversalAfterDumpCandles()
    {
        var now = DateTimeOffset.UtcNow;
        var candles = new List<Candle>();
        for (var index = 0; index < 22; index++)
        {
            var close = 0.235m - index * 0.0004m;
            candles.Add(new Candle(
                now.AddMinutes(index - 60),
                close + 0.0002m,
                close + 0.001m,
                close - 0.001m,
                close,
                1000m,
                close * 1000m));
        }

        for (var index = 0; index < 26; index++)
        {
            var close = 0.226m - index * 0.00255m;
            candles.Add(new Candle(
                now.AddMinutes(index - 38),
                close + 0.001m,
                close + 0.0018m,
                close - 0.0018m,
                close,
                1200m,
                close * 1200m));
        }

        var stabilizing = new[]
        {
            (Open: 0.1590m, High: 0.1602m, Low: 0.1580m, Close: 0.1592m, Volume: 1800m),
            (Open: 0.1592m, High: 0.1610m, Low: 0.1587m, Close: 0.1606m, Volume: 2300m),
            (Open: 0.1605m, High: 0.1624m, Low: 0.1598m, Close: 0.1620m, Volume: 2600m),
            (Open: 0.1619m, High: 0.1635m, Low: 0.1609m, Close: 0.1631m, Volume: 2400m),
            (Open: 0.1630m, High: 0.1650m, Low: 0.1622m, Close: 0.1644m, Volume: 2500m),
            (Open: 0.1641m, High: 0.1662m, Low: 0.1634m, Close: 0.1652m, Volume: 2200m),
            (Open: 0.1650m, High: 0.1672m, Low: 0.1642m, Close: 0.1664m, Volume: 2300m),
            (Open: 0.1662m, High: 0.1680m, Low: 0.1655m, Close: 0.1672m, Volume: 2100m),
            (Open: 0.1670m, High: 0.1688m, Low: 0.1662m, Close: 0.1680m, Volume: 2200m),
            (Open: 0.1678m, High: 0.1694m, Low: 0.1670m, Close: 0.1686m, Volume: 2000m)
        };
        for (var index = 0; index < stabilizing.Length; index++)
        {
            var candle = stabilizing[index];
            candles.Add(new Candle(
                now.AddMinutes(index - stabilizing.Length),
                candle.Open,
                candle.High,
                candle.Low,
                candle.Close,
                candle.Volume,
                candle.Close * candle.Volume));
        }

        return candles;
    }

    private static IReadOnlyList<Candle> BuildContinuingDumpCandles()
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, 55)
            .Select(index =>
            {
                var close = 0.235m - index * 0.00145m;
                return new Candle(
                    now.AddMinutes(index - 55),
                    close + 0.0008m,
                    close + 0.0015m,
                    close - 0.0018m,
                    close,
                    index >= 50 ? 2400m : 1100m,
                    close * (index >= 50 ? 2400m : 1100m));
            })
            .ToArray();
    }
}
