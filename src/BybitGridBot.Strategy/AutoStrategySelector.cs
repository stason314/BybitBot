using System.Text.Json;
using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class AutoStrategySelector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const decimal DefaultStopBufferPercent = 2m;
    private const decimal TrendStopBufferPercent = 3m;
    private const decimal VolatileStopBufferPercent = 4m;
    private const decimal RegimeRangeStopBufferMultiplier = 0.75m;
    private readonly SignalAnalyzer _signalAnalyzer = new();

    public AutoConfigRecommendation Recommend(
        GridOptions currentOptions,
        MarketRegimeAnalysis regime,
        IReadOnlyList<Candle> candles)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length == 0)
        {
            return FromCurrent(currentOptions, TradingStrategyType.Grid, "No candles available. Keep current grid.");
        }

        var lastPrice = ordered[^1].Close > 0m ? ordered[^1].Close : currentOptions.LowerPrice;
        var support = regime.Support ?? ordered.TakeLast(Math.Min(30, ordered.Length)).Min(candle => candle.Low);
        var resistance = regime.Resistance ?? ordered.TakeLast(Math.Min(30, ordered.Length)).Max(candle => candle.High);
        var atr = CalculateAtr(ordered.TakeLast(Math.Min(30, ordered.Length)).ToArray());
        var atrPercent = lastPrice > 0m ? atr / lastPrice * 100m : 0m;
        var high = ordered.Max(candle => candle.High);
        var drawdownPercent = high > 0m ? (high - lastPrice) / high * 100m : 0m;
        var step = ChooseStep(lastPrice, atr);
        var padding = decimal.Max(step * 2m, atr * 1.5m);
        var lower = FloorToStep(decimal.Max(step, decimal.Min(support, lastPrice) - padding), step);
        var upper = CeilingToStep(decimal.Max(resistance, lastPrice) + padding, step);
        var stopPadding = ChooseStopPadding(regime, step, atr, lastPrice, padding);
        var stopLower = FloorToStep(decimal.Max(step, lower - stopPadding), step);
        var stopUpper = CeilingToStep(upper + stopPadding, step);
        var orderSize = RecommendOrderSize(currentOptions, regime);
        var signal = _signalAnalyzer.Analyze(ordered);
        var metrics = new AutoConfigMetrics
        {
            AtrPercent = decimal.Round(atrPercent, 4, MidpointRounding.AwayFromZero),
            DrawdownPercent = decimal.Round(drawdownPercent, 4, MidpointRounding.AwayFromZero),
            Support = support,
            Resistance = resistance,
            LastPrice = lastPrice
        };

        if (ShouldRecommendVolatileRange(regime, signal, atrPercent))
        {
            var comboOrderSize = RecommendActiveOrderSize(currentOptions, orderSize);
            return Build(
                TradingStrategyType.Combo,
                "Volatile range without danger. Prefer active Combo so grid captures oscillations and DCA handles lower pullbacks.",
                lower,
                upper,
                step,
                comboOrderSize,
                stopLower,
                stopUpper,
                BuildComboConfig(comboOrderSize, currentOptions.MinOrderSizeUsdt, lower),
                metrics);
        }

        if (ShouldRecommendSignalBot(regime, signal))
        {
            var signalOrderSize = decimal.Max(currentOptions.MinOrderSizeUsdt, orderSize * 0.75m);
            return Build(
                TradingStrategyType.Signal,
                $"Directional {signal.Signal.ToString().ToLowerInvariant()} signal confirmed. Use Signal Bot instead of passive grid orders. Signal confidence: {signal.Confidence:0.####}.",
                lower,
                upper,
                step,
                signalOrderSize,
                stopLower,
                stopUpper,
                BuildSignalConfig(signalOrderSize, currentOptions.MinOrderSizeUsdt, stopLower, stopUpper),
                metrics);
        }

        if (ShouldRecommendDipStrategy(regime, signal, drawdownPercent))
        {
            var dipOrderSize = RecommendActiveOrderSize(currentOptions, orderSize * 0.6m);
            if (regime.MovePercent < 0m || drawdownPercent >= 1m)
            {
                return Build(
                    TradingStrategyType.Btd,
                    $"Dip setup detected near lower Bollinger with RSI {signal.Rsi:0.####}. Prefer cautious BTD instead of strict Signal Bot.",
                    lower,
                    upper,
                    step,
                    dipOrderSize,
                    stopLower,
                    stopUpper,
                    BuildBtdConfig(dipOrderSize, currentOptions.MinOrderSizeUsdt, drawdownPercent, lastPrice, stopLower, stopUpper),
                    metrics);
            }

            return Build(
                TradingStrategyType.Combo,
                $"Pullback setup detected near lower Bollinger with RSI {signal.Rsi:0.####}. Use Combo so grid can trade the range and DCA only on deeper pullbacks.",
                lower,
                upper,
                step,
                dipOrderSize,
                stopLower,
                stopUpper,
                BuildComboConfig(dipOrderSize, currentOptions.MinOrderSizeUsdt, lower),
                metrics);
        }

        return regime.Regime switch
        {
            MarketRegimeType.Danger => Build(
                TradingStrategyType.NoTrade,
                "Danger regime. Do not open new orders until volatility cools down.",
                lower,
                upper,
                step,
                currentOptions.MinOrderSizeUsdt,
                stopLower,
                stopUpper,
                "{}",
                metrics),

            MarketRegimeType.Breakout => Build(
                TradingStrategyType.NoTrade,
                "Breakout-like move. Wait for confirmation before opening new grid or dip orders.",
                lower,
                upper,
                step,
                currentOptions.MinOrderSizeUsdt,
                stopLower,
                stopUpper,
                "{}",
                metrics),

            MarketRegimeType.Trend when regime.MovePercent < 0m => Build(
                TradingStrategyType.Btd,
                "Downtrend. Prefer cautious BTD instead of continuous grid accumulation.",
                lower,
                upper,
                step,
                RecommendActiveOrderSize(currentOptions, orderSize * 0.6m),
                stopLower,
                stopUpper,
                BuildBtdConfig(RecommendActiveOrderSize(currentOptions, orderSize * 0.6m), currentOptions.MinOrderSizeUsdt, drawdownPercent, lastPrice, stopLower, stopUpper),
                metrics),

            MarketRegimeType.Trend => Build(
                TradingStrategyType.Combo,
                "Uptrend or directional trend. Use wider Combo; DCA only on pullbacks.",
                lower,
                upper,
                step,
                RecommendActiveOrderSize(currentOptions, orderSize * 0.75m),
                stopLower,
                stopUpper,
                BuildComboConfig(RecommendActiveOrderSize(currentOptions, orderSize * 0.75m), currentOptions.MinOrderSizeUsdt, lower),
                metrics),

            MarketRegimeType.LowVolatility => Build(
                TradingStrategyType.Grid,
                "Low volatility. Keep grid but reduce order size because fees can eat small cycles.",
                lower,
                upper,
                step,
                decimal.Max(currentOptions.MinOrderSizeUsdt, orderSize * 0.5m),
                stopLower,
                stopUpper,
                "{}",
                metrics),

            _ => Build(
                TradingStrategyType.Grid,
                "Range market. Grid is suitable if spread and fees are covered.",
                lower,
                upper,
                step,
                orderSize,
                stopLower,
                stopUpper,
                "{}",
                metrics)
        };
    }

    private static AutoConfigRecommendation FromCurrent(
        GridOptions options,
        TradingStrategyType strategyType,
        string reason) => Build(
            strategyType,
            reason,
            options.LowerPrice,
            options.UpperPrice,
            options.Step,
            options.OrderSizeUsdt,
            options.StopLowerPrice,
            options.StopUpperPrice,
            "{}",
            new AutoConfigMetrics());

    private static AutoConfigRecommendation Build(
        TradingStrategyType strategyType,
        string reason,
        decimal lower,
        decimal upper,
        decimal step,
        decimal orderSize,
        decimal stopLower,
        decimal stopUpper,
        string strategyConfigJson,
        AutoConfigMetrics metrics) => new()
    {
        StrategyType = strategyType,
        Reason = reason,
        LowerPrice = decimal.Round(lower, 8, MidpointRounding.ToZero),
        UpperPrice = decimal.Round(upper, 8, MidpointRounding.AwayFromZero),
        Step = decimal.Round(step, 8, MidpointRounding.AwayFromZero),
        OrderSizeUsdt = decimal.Round(decimal.Max(5m, orderSize), 2, MidpointRounding.AwayFromZero),
        StopLowerPrice = decimal.Round(stopLower, 8, MidpointRounding.ToZero),
        StopUpperPrice = decimal.Round(stopUpper, 8, MidpointRounding.AwayFromZero),
        StrategyConfigJson = strategyConfigJson,
        Metrics = metrics
    };

    private static bool ShouldRecommendSignalBot(MarketRegimeAnalysis regime, SignalAnalysis signal)
    {
        if (regime.Regime is MarketRegimeType.Danger or MarketRegimeType.LowVolatility)
        {
            return false;
        }

        if (signal.Signal is not (SignalType.Buy or SignalType.Sell) || signal.Confidence < 0.65m)
        {
            return false;
        }

        if (signal.Signal == SignalType.Buy)
        {
            return signal.TrendStrength > 0.08m &&
                signal.BollingerPosition > 0.2m &&
                signal.BollingerPosition < 0.85m &&
                regime.Regime is MarketRegimeType.Breakout or MarketRegimeType.Trend;
        }

        return signal.TrendStrength < -0.08m &&
            signal.BollingerPosition >= 0.65m &&
            regime.Regime is MarketRegimeType.Breakout or MarketRegimeType.Trend;
    }

    private static bool ShouldRecommendVolatileRange(
        MarketRegimeAnalysis regime,
        SignalAnalysis signal,
        decimal atrPercent)
    {
        if (regime.Regime is MarketRegimeType.Danger or MarketRegimeType.LowVolatility)
        {
            return false;
        }

        return regime.RangePercent >= 1.5m &&
            Math.Abs(regime.MovePercent) <= 1.5m &&
            atrPercent >= 0.15m &&
            signal.VolumeRatio < 2.5m;
    }

    private static bool ShouldRecommendDipStrategy(
        MarketRegimeAnalysis regime,
        SignalAnalysis signal,
        decimal drawdownPercent)
    {
        if (regime.Regime == MarketRegimeType.Danger)
        {
            return false;
        }

        return signal.BollingerPosition <= 0.2m &&
            signal.Rsi <= 55m &&
            signal.VolumeRatio < 1.8m &&
            drawdownPercent >= 0.5m;
    }

    private static string BuildComboConfig(decimal orderSize, decimal minOrderSize, decimal dcaBelowPrice)
    {
        var config = new
        {
            orderSizeUsdt = decimal.Round(decimal.Max(minOrderSize, orderSize), 2, MidpointRounding.AwayFromZero),
            buyIntervalMinutes = 15,
            maxActiveBuyOrders = 3,
            takeProfitPercent = 1m,
            limitOffsetPercent = 0.1m,
            dipPercent = 0.7m,
            dipLookbackCandles = 30,
            candleInterval = "1",
            maxPositionUsdt = 500m,
            dcaBelowPrice = decimal.Round(dcaBelowPrice, 8, MidpointRounding.ToZero)
        };

        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private static string BuildSignalConfig(
        decimal orderSize,
        decimal minOrderSize,
        decimal stopLower,
        decimal stopUpper)
    {
        var config = new
        {
            orderSizeUsdt = decimal.Round(decimal.Max(minOrderSize, orderSize), 2, MidpointRounding.AwayFromZero),
            cooldownMinutes = 30,
            minConfidence = 0.65m,
            maxPositionUsdt = 400m,
            stopLossPercent = 2m,
            takeProfitPercent = 3m,
            limitOffsetPercent = 0m,
            lookbackCandles = 120,
            candleInterval = "1",
            stopLowerPrice = stopLower,
            stopUpperPrice = stopUpper
        };

        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private static string BuildBtdConfig(
        decimal orderSize,
        decimal minOrderSize,
        decimal drawdownPercent,
        decimal lastPrice,
        decimal stopLower,
        decimal stopUpper)
    {
        var config = new
        {
            orderSizeUsdt = decimal.Round(decimal.Max(minOrderSize, orderSize), 2, MidpointRounding.AwayFromZero),
            dipPercent = decimal.Round(decimal.Max(0.8m, decimal.Min(3m, drawdownPercent <= 0m ? 1.2m : drawdownPercent)), 2, MidpointRounding.AwayFromZero),
            dipLookbackCandles = 30,
            candleInterval = "1",
            maxBuys = 3,
            minMinutesBetweenBuys = 10,
            takeProfitPercent = 1.2m,
            limitOffsetPercent = 0.2m,
            maxPositionUsdt = 400m,
            referencePrice = decimal.Round(lastPrice, 8, MidpointRounding.AwayFromZero),
            stopLowerPrice = stopLower,
            stopUpperPrice = stopUpper
        };

        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private static decimal RecommendOrderSize(GridOptions options, MarketRegimeAnalysis regime)
    {
        var multiplier = regime.Regime switch
        {
            MarketRegimeType.Danger => 0.5m,
            MarketRegimeType.Breakout => 0.75m,
            MarketRegimeType.Trend => 0.75m,
            MarketRegimeType.LowVolatility => 0.5m,
            _ => 1m
        };

        return decimal.Max(options.MinOrderSizeUsdt, options.OrderSizeUsdt * multiplier);
    }

    private static decimal RecommendActiveOrderSize(GridOptions options, decimal baseOrderSize)
    {
        return decimal.Max(decimal.Max(options.MinOrderSizeUsdt, baseOrderSize), 20m);
    }

    private static decimal ChooseStopPadding(
        MarketRegimeAnalysis regime,
        decimal step,
        decimal atr,
        decimal lastPrice,
        decimal gridPadding)
    {
        var minimumStopPercent = regime.Regime switch
        {
            MarketRegimeType.Danger or MarketRegimeType.Breakout => VolatileStopBufferPercent,
            MarketRegimeType.Trend => TrendStopBufferPercent,
            _ => DefaultStopBufferPercent
        };
        var regimeStopPercent = decimal.Max(
            minimumStopPercent,
            decimal.Max(
                Math.Abs(regime.MovePercent),
                regime.RangePercent * RegimeRangeStopBufferMultiplier));
        var pricePercentPadding = lastPrice > 0m ? lastPrice * regimeStopPercent / 100m : 0m;
        var technicalPadding = decimal.Max(gridPadding * 2m, decimal.Max(atr * 4m, step * 4m));

        return decimal.Max(technicalPadding, pricePercentPadding);
    }

    private static decimal CalculateAtr(IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0)
        {
            return 0m;
        }

        return candles.Average(candle => candle.High - candle.Low);
    }

    private static decimal ChooseStep(decimal lastPrice, decimal atr)
    {
        var rawStep = decimal.Max(lastPrice * 0.003m, atr / 2m);
        if (lastPrice < 1m)
        {
            return decimal.Max(0.0001m, decimal.Round(rawStep, 4, MidpointRounding.AwayFromZero));
        }

        if (lastPrice < 10m)
        {
            return decimal.Max(0.001m, decimal.Round(rawStep, 3, MidpointRounding.AwayFromZero));
        }

        return decimal.Max(0.01m, decimal.Round(rawStep, 2, MidpointRounding.AwayFromZero));
    }

    private static decimal FloorToStep(decimal value, decimal step) =>
        decimal.Round(Math.Floor(value / step) * step, 8, MidpointRounding.ToZero);

    private static decimal CeilingToStep(decimal value, decimal step) =>
        decimal.Round(Math.Ceiling(value / step) * step, 8, MidpointRounding.AwayFromZero);
}
