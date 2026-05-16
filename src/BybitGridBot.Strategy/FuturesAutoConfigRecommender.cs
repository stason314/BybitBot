using System.Text.Json;
using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class FuturesAutoConfigRecommender
{
    private const decimal MinimumRiskRewardRatio = 3m;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FuturesAutoConfigRecommendation Recommend(
        FuturesBotSettings currentSettings,
        IReadOnlyList<Candle> candles,
        bool hasOpenPosition)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length == 0)
        {
            return Build(
                currentSettings,
                hasOpenPosition ? FuturesStrategyType.ReduceOnly : FuturesStrategyType.Pause,
                "No futures candles available. Keep futures trading paused until market data is available.",
                new FuturesAutoConfigMetrics());
        }

        var lastPrice = ordered[^1].Close;
        var lookback = ordered.TakeLast(Math.Min(60, ordered.Length)).ToArray();
        var firstPrice = lookback[0].Open > 0m ? lookback[0].Open : lookback[0].Close;
        var movePercent = firstPrice > 0m ? (lastPrice - firstPrice) / firstPrice * 100m : 0m;
        var high = lookback.Max(candle => candle.High);
        var low = lookback.Min(candle => candle.Low);
        var drawdownPercent = high > 0m ? (high - lastPrice) / high * 100m : 0m;
        var atr = CalculateAtr(lookback);
        var atrPercent = lastPrice > 0m ? atr / lastPrice * 100m : 0m;
        var metrics = new FuturesAutoConfigMetrics
        {
            LastPrice = lastPrice,
            MovePercent = decimal.Round(movePercent, 4, MidpointRounding.AwayFromZero),
            AtrPercent = decimal.Round(atrPercent, 4, MidpointRounding.AwayFromZero),
            DrawdownPercent = decimal.Round(drawdownPercent, 4, MidpointRounding.AwayFromZero),
            Support = low,
            Resistance = high
        };

        var recommendedLeverage = RecommendLeverage(currentSettings.Leverage, atrPercent);
        var stopLossPercent = RecommendStopLoss(atrPercent);
        var takeProfitPercent = stopLossPercent * MinimumRiskRewardRatio;
        var exposureMultiplier = RecommendExposureMultiplier(atrPercent);
        var modeEntryMultiplier = 1m;
        var maxNotional = decimal.Max(1m, currentSettings.MaxNotionalUsdt);
        var maxMargin = recommendedLeverage > 0m
            ? decimal.Min(currentSettings.MaxMarginUsdt, maxNotional / recommendedLeverage)
            : currentSettings.MaxMarginUsdt;

        if (IsDanger(movePercent, drawdownPercent, atrPercent) &&
            currentSettings.Direction == FuturesDirection.LongOnly)
        {
            return Build(
                currentSettings,
                hasOpenPosition ? FuturesStrategyType.ReduceOnly : FuturesStrategyType.Pause,
                "Danger futures regime. Block new long exposure and reduce existing position only.",
                metrics,
                recommendedLeverage,
                maxNotional,
                maxMargin,
                stopLossPercent,
                takeProfitPercent,
                entryNotionalMultiplier: 0.25m * exposureMultiplier * modeEntryMultiplier);
        }

        if (AllowsShort(currentSettings.Direction) && movePercent <= -1.2m)
        {
            return Build(
                currentSettings,
                FuturesStrategyType.TrendFollowShortOnly,
                "Negative futures trend. Prefer short-only TrendFollow with stop-loss above entry.",
                metrics,
                recommendedLeverage,
                maxNotional,
                maxMargin,
                stopLossPercent,
                takeProfitPercent,
                FuturesDirection.ShortOnly,
                entryNotionalMultiplier: 0.25m * exposureMultiplier * modeEntryMultiplier);
        }

        if (AllowsShort(currentSettings.Direction) && lastPrice < low + (atr * 0.5m) && movePercent < -0.4m)
        {
            return Build(
                currentSettings,
                FuturesStrategyType.BreakdownShort,
                "Potential downside breakdown. Use short-only Breakdown with reduced notional and stop-loss.",
                metrics,
                recommendedLeverage,
                maxNotional,
                maxMargin,
                stopLossPercent,
                takeProfitPercent,
                FuturesDirection.ShortOnly,
                entryNotionalMultiplier: 0.1875m * exposureMultiplier * modeEntryMultiplier);
        }

        if (movePercent >= 1.2m && drawdownPercent <= 2m)
        {
            return Build(
                currentSettings,
                FuturesStrategyType.TrendFollow,
                "Positive futures trend. Prefer long-only TrendFollow with required stop-loss.",
                metrics,
                recommendedLeverage,
                maxNotional,
                maxMargin,
                stopLossPercent,
                takeProfitPercent,
                entryNotionalMultiplier: 0.25m * exposureMultiplier * modeEntryMultiplier);
        }

        if (lastPrice > high - (atr * 0.5m) && movePercent > 0.4m)
        {
            return Build(
                currentSettings,
                FuturesStrategyType.Breakout,
                "Potential upside breakout. Use long-only Breakout with reduced notional and stop-loss.",
                metrics,
                recommendedLeverage,
                maxNotional,
                maxMargin,
                stopLossPercent,
                takeProfitPercent,
                entryNotionalMultiplier: 0.1875m * exposureMultiplier * modeEntryMultiplier);
        }

        return Build(
            currentSettings,
            currentSettings.Direction == FuturesDirection.ShortOnly
                ? FuturesStrategyType.GridShortOnly
                : FuturesStrategyType.GridLongOnly,
            currentSettings.Direction == FuturesDirection.ShortOnly
                ? "Range-like futures market. Use short-only grid with conservative leverage."
                : "Range-like futures market. Use long-only grid with conservative leverage.",
            metrics,
            decimal.Min(recommendedLeverage, 2m),
            maxNotional,
            maxMargin,
            stopLossPercent,
            takeProfitPercent,
            currentSettings.Direction == FuturesDirection.ShortOnly ? FuturesDirection.ShortOnly : FuturesDirection.LongOnly,
            entryNotionalMultiplier: 0.1875m * exposureMultiplier * modeEntryMultiplier);
    }

    private static FuturesAutoConfigRecommendation Build(
        FuturesBotSettings currentSettings,
        FuturesStrategyType strategyType,
        string reason,
        FuturesAutoConfigMetrics metrics,
        decimal? leverage = null,
        decimal? maxNotionalUsdt = null,
        decimal? maxMarginUsdt = null,
        decimal? stopLossPercent = null,
        decimal? takeProfitPercent = null,
        FuturesDirection? direction = null,
        decimal entryNotionalMultiplier = 0.25m)
    {
        var resolvedLeverage = decimal.Max(1m, leverage ?? currentSettings.Leverage);
        var resolvedMaxNotional = decimal.Max(1m, maxNotionalUsdt ?? currentSettings.MaxNotionalUsdt);
        var resolvedMaxMargin = decimal.Max(1m, maxMarginUsdt ?? currentSettings.MaxMarginUsdt);
        var resolvedStopLoss = decimal.Max(0.1m, stopLossPercent ?? currentSettings.StopLossPercent);
        var resolvedTakeProfit = decimal.Max(resolvedStopLoss * MinimumRiskRewardRatio, takeProfitPercent ?? resolvedStopLoss * MinimumRiskRewardRatio);

        return new FuturesAutoConfigRecommendation
        {
            StrategyType = strategyType,
            Reason = reason,
            Leverage = decimal.Round(resolvedLeverage, 2, MidpointRounding.AwayFromZero),
            MarginMode = FuturesMarginMode.Isolated,
            PositionMode = FuturesPositionMode.OneWay,
            Direction = direction ?? FuturesDirection.LongOnly,
            MaxNotionalUsdt = decimal.Round(resolvedMaxNotional, 4, MidpointRounding.AwayFromZero),
            MaxMarginUsdt = decimal.Round(resolvedMaxMargin, 4, MidpointRounding.AwayFromZero),
            StopLossPercent = decimal.Round(resolvedStopLoss, 4, MidpointRounding.AwayFromZero),
            TakeProfitPercent = decimal.Round(resolvedTakeProfit, 4, MidpointRounding.AwayFromZero),
            LiquidationBufferPercent = currentSettings.LiquidationBufferPercent,
            ReduceOnlyEnabled = true,
            StrategyConfigJson = BuildStrategyConfig(
                strategyType,
                resolvedMaxNotional,
                resolvedStopLoss,
                resolvedTakeProfit,
                metrics,
                direction ?? FuturesDirection.LongOnly,
                entryNotionalMultiplier),
            Metrics = metrics
        };
    }

    private static string BuildStrategyConfig(
        FuturesStrategyType strategyType,
        decimal maxNotionalUsdt,
        decimal stopLossPercent,
        decimal takeProfitPercent,
        FuturesAutoConfigMetrics metrics,
        FuturesDirection direction,
        decimal entryNotionalMultiplier)
    {
        var config = new
        {
            strategyType = strategyType.ToString(),
            entryNotionalUsdt = decimal.Round(maxNotionalUsdt * entryNotionalMultiplier, 4, MidpointRounding.AwayFromZero),
            stopLossPercent = decimal.Round(stopLossPercent, 4, MidpointRounding.AwayFromZero),
            takeProfitPercent = decimal.Round(takeProfitPercent, 4, MidpointRounding.AwayFromZero),
            support = metrics.Support,
            resistance = metrics.Resistance,
            lastPrice = metrics.LastPrice,
            longOnly = direction == FuturesDirection.LongOnly,
            shortOnly = direction == FuturesDirection.ShortOnly,
            reduceOnlyOnExit = true
        };

        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private static decimal RecommendLeverage(decimal currentLeverage, decimal atrPercent)
    {
        var cap = atrPercent switch
        {
            >= 4m => 1m,
            >= 2m => 2m,
            _ => 3m
        };

        return decimal.Max(1m, decimal.Min(currentLeverage <= 0m ? 2m : currentLeverage, cap));
    }

    private static decimal RecommendStopLoss(decimal atrPercent)
    {
        var atrStop = decimal.Max(1m, atrPercent * 1.8m);
        return decimal.Max(2m, atrStop);
    }

    private static decimal RecommendExposureMultiplier(decimal atrPercent) =>
        atrPercent switch
        {
            >= 4m => 0.4m,
            >= 2m => 0.65m,
            _ => 1m
        };

    private static bool IsDanger(decimal movePercent, decimal drawdownPercent, decimal atrPercent) =>
        movePercent <= -1.5m || drawdownPercent >= 5m || atrPercent >= 6m;

    private static bool AllowsShort(FuturesDirection direction) =>
        direction is FuturesDirection.ShortOnly or FuturesDirection.LongAndShort;

    private static decimal CalculateAtr(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 2)
        {
            return candles.Count == 0 ? 0m : candles[0].High - candles[0].Low;
        }

        var total = 0m;
        for (var index = 1; index < candles.Count; index++)
        {
            var current = candles[index];
            var previous = candles[index - 1];
            var trueRange = decimal.Max(
                current.High - current.Low,
                decimal.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
            total += trueRange;
        }

        return total / (candles.Count - 1);
    }
}
