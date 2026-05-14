using System.Text.Json;
using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class AutoStrategySelector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        var stopLower = FloorToStep(decimal.Max(step, lower - padding), step);
        var stopUpper = CeilingToStep(upper + padding, step);
        var orderSize = RecommendOrderSize(currentOptions, regime);
        var metrics = new AutoConfigMetrics
        {
            AtrPercent = decimal.Round(atrPercent, 4, MidpointRounding.AwayFromZero),
            DrawdownPercent = decimal.Round(drawdownPercent, 4, MidpointRounding.AwayFromZero),
            Support = support,
            Resistance = resistance,
            LastPrice = lastPrice
        };

        return regime.Regime switch
        {
            MarketRegimeType.Danger => Build(
                TradingStrategyType.Btd,
                "Danger regime. Use BTD only for controlled dip entries; avoid broad grid buys.",
                lower,
                upper,
                step,
                orderSize * 0.5m,
                stopLower,
                stopUpper,
                BuildBtdConfig(orderSize * 0.5m, drawdownPercent, lastPrice, stopLower, stopUpper),
                metrics),

            MarketRegimeType.Breakout => Build(
                TradingStrategyType.Combo,
                "Breakout-like move. Use wider Combo and let DCA activate only below support.",
                lower,
                upper,
                step,
                orderSize * 0.75m,
                stopLower,
                stopUpper,
                BuildComboConfig(orderSize * 0.75m, lower),
                metrics),

            MarketRegimeType.Trend when regime.MovePercent < 0m => Build(
                TradingStrategyType.Btd,
                "Downtrend. Prefer cautious BTD instead of continuous grid accumulation.",
                lower,
                upper,
                step,
                orderSize * 0.6m,
                stopLower,
                stopUpper,
                BuildBtdConfig(orderSize * 0.6m, drawdownPercent, lastPrice, stopLower, stopUpper),
                metrics),

            MarketRegimeType.Trend => Build(
                TradingStrategyType.Combo,
                "Uptrend or directional trend. Use wider Combo; DCA only on pullbacks.",
                lower,
                upper,
                step,
                orderSize * 0.75m,
                stopLower,
                stopUpper,
                BuildComboConfig(orderSize * 0.75m, lower),
                metrics),

            MarketRegimeType.LowVolatility => Build(
                TradingStrategyType.Grid,
                "Low volatility. Keep grid but reduce order size because fees can eat small cycles.",
                lower,
                upper,
                step,
                orderSize * 0.5m,
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

    private static string BuildComboConfig(decimal orderSize, decimal dcaBelowPrice)
    {
        var config = new
        {
            orderSizeUsdt = decimal.Round(decimal.Max(5m, orderSize), 2, MidpointRounding.AwayFromZero),
            buyIntervalMinutes = 30,
            maxActiveBuyOrders = 1,
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

    private static string BuildBtdConfig(
        decimal orderSize,
        decimal drawdownPercent,
        decimal lastPrice,
        decimal stopLower,
        decimal stopUpper)
    {
        var config = new
        {
            orderSizeUsdt = decimal.Round(decimal.Max(5m, orderSize), 2, MidpointRounding.AwayFromZero),
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

        return decimal.Max(5m, options.OrderSizeUsdt * multiplier);
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
