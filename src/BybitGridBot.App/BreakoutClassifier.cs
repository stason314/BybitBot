using BybitGridBot.Domain;

namespace BybitGridBot.App;

public sealed class BreakoutClassifier
{
    private readonly NySessionBreakoutOptions _nyOptions;
    private readonly TurtleTrendOptions _turtleOptions;

    public BreakoutClassifier(NySessionBreakoutOptions nyOptions, TurtleTrendOptions turtleOptions)
    {
        _nyOptions = nyOptions;
        _turtleOptions = turtleOptions;
    }

    public BreakoutClassifierResult Classify(NyStrategyContext context)
    {
        var five = context.FiveMinuteCandles.OrderBy(candle => candle.OpenTime).ToArray();
        if (five.Length < 3 || context.Range.Upper <= context.Range.Lower)
        {
            return Unclear("Not enough 5m candles or invalid NY range.");
        }

        var current = five[^1];
        var previous = five[^2];
        var side = ResolveBreakoutSide(current, context.Range.Upper, context.Range.Lower);
        if (side == StrategySide.None)
        {
            var reclaimedSide = ResolveFalseBreakoutSide(current, context.Range.Upper, context.Range.Lower);
            if (reclaimedSide != StrategySide.None)
            {
                return BuildFalseBreakout(context, current, reclaimedSide);
            }

            return Unclear("Price is inside range without fresh sweep or breakout.");
        }

        var heldOutside = side == StrategySide.Long
            ? previous.Close > context.Range.Upper || current.Low > context.Range.Upper
            : previous.Close < context.Range.Lower || current.High < context.Range.Lower;
        var adx = TradingIndicatorMath.Adx(context.FifteenMinuteCandles, 14);
        var previousAdx = TradingIndicatorMath.Adx(context.FifteenMinuteCandles.OrderBy(candle => candle.OpenTime).SkipLast(4).ToArray(), 14);
        var adxRising = adx >= _nyOptions.TrueBreakoutAdx || adx > previousAdx && adx >= _turtleOptions.MinAdx;
        var closes = context.FifteenMinuteCandles.OrderBy(candle => candle.OpenTime).Select(candle => candle.Close).ToArray();
        var ema20 = TradingIndicatorMath.Ema(closes, 20);
        var ema50 = TradingIndicatorMath.Ema(closes, 50);
        var emaConfirmed = side == StrategySide.Long ? ema20 >= ema50 : ema20 <= ema50;
        var volumeSma = TradingIndicatorMath.VolumeSma(five.SkipLast(1).ToArray(), 20);
        var volumeConfirmed = volumeSma <= 0m || current.Volume >= volumeSma * _turtleOptions.VolumeMultiplier;
        var btcAgainst = IsBtcAgainst(context.BtcFifteenMinuteCandles, side);

        if (heldOutside && adxRising && emaConfirmed && volumeConfirmed && !btcAgainst)
        {
            return new BreakoutClassifierResult
            {
                Classification = BreakoutClassification.TrueBreakout,
                BreakoutSide = side,
                BlocksSweep = true,
                BoostsTurtle = true,
                ScoreModifierForSweep = -30m,
                ScoreModifierForTurtle = 25m,
                Reason = $"True breakout: {side}, heldOutside={heldOutside}, ADX={adx:F2}, EMA20={ema20:F8}, EMA50={ema50:F8}, volume ok."
            };
        }

        return Unclear($"Breakout unclear: held={heldOutside}, ADX={adx:F2}, EMA confirmed={emaConfirmed}, volume confirmed={volumeConfirmed}, btcAgainst={btcAgainst}.");
    }

    private BreakoutClassifierResult BuildFalseBreakout(NyStrategyContext context, Candle current, StrategySide side)
    {
        var boundary = side == StrategySide.Short ? context.Range.Upper : context.Range.Lower;
        var reclaimPercent = boundary > 0m
            ? side == StrategySide.Short
                ? (boundary - current.Close) / boundary * 100m
                : (current.Close - boundary) / boundary * 100m
            : 0m;
        var volumeSma = TradingIndicatorMath.VolumeSma(context.FiveMinuteCandles.SkipLast(1).ToArray(), 20);
        var volumeConfirmsTrueBreakout = volumeSma > 0m && current.Volume >= volumeSma * _nyOptions.HighBreakoutVolumeRatio;
        var btcAgainstReversal = IsBtcAgainst(context.BtcFifteenMinuteCandles, side);
        if (reclaimPercent >= _nyOptions.MinReclaimPercent && !volumeConfirmsTrueBreakout && !btcAgainstReversal)
        {
            return new BreakoutClassifierResult
            {
                Classification = BreakoutClassification.FalseBreakout,
                BreakoutSide = side,
                ScoreModifierForSweep = 20m,
                ScoreModifierForTurtle = -20m,
                Reason = $"False breakout: {side}, reclaim={reclaimPercent:F4}%, volume did not confirm continuation."
            };
        }

        return Unclear($"False breakout unclear: reclaim={reclaimPercent:F4}%, highVolume={volumeConfirmsTrueBreakout}, btcAgainst={btcAgainstReversal}.");
    }

    private static StrategySide ResolveBreakoutSide(Candle candle, decimal upper, decimal lower)
    {
        if (candle.Close > upper)
        {
            return StrategySide.Long;
        }

        if (candle.Close < lower)
        {
            return StrategySide.Short;
        }

        return StrategySide.None;
    }

    private static StrategySide ResolveFalseBreakoutSide(Candle candle, decimal upper, decimal lower)
    {
        if (candle.High > upper && candle.Close < upper)
        {
            return StrategySide.Short;
        }

        if (candle.Low < lower && candle.Close > lower)
        {
            return StrategySide.Long;
        }

        return StrategySide.None;
    }

    private bool IsBtcAgainst(IReadOnlyList<Candle> btcCandles, StrategySide side)
    {
        var closed = btcCandles.OrderBy(candle => candle.OpenTime).TakeLast(12).ToArray();
        if (closed.Length < 6)
        {
            return false;
        }

        var movePercent = closed[0].Open > 0m ? (closed[^1].Close - closed[0].Open) / closed[0].Open * 100m : 0m;
        var adx = TradingIndicatorMath.Adx(btcCandles, 14);
        var strong = Math.Abs(movePercent) >= _nyOptions.BtcTrendMovePercent && adx >= _nyOptions.BtcTrendAdx;
        return strong && (side == StrategySide.Short && movePercent > 0m || side == StrategySide.Long && movePercent < 0m);
    }

    private static BreakoutClassifierResult Unclear(string reason) => new()
    {
        Classification = BreakoutClassification.Unclear,
        BreakoutSide = StrategySide.None,
        Reason = reason
    };
}

public sealed class TrueBreakoutDetector
{
    private readonly BreakoutClassifier _classifier;

    public TrueBreakoutDetector(BreakoutClassifier classifier)
    {
        _classifier = classifier;
    }

    public BreakoutClassifierResult Analyze(NyStrategyContext context) => _classifier.Classify(context);
}
