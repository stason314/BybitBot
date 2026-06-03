using BybitGridBot.Domain;

namespace BybitGridBot.App;

public sealed class PatternConfirmationEngine
{
    private readonly NySessionBreakoutOptions _options;

    public PatternConfirmationEngine(NySessionBreakoutOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<PatternSignal> Detect(
        IReadOnlyList<Candle> candles,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        var closed = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var result = new List<PatternSignal>();
        AddIfNotNull(result, DetectEngulfing(closed, upperBoundary, lowerBoundary));
        AddIfNotNull(result, DetectPinbar(closed, upperBoundary, lowerBoundary));
        AddIfNotNull(result, DetectThreeBarContinuation(closed, upperBoundary, lowerBoundary));
        AddIfNotNull(result, DetectThreeBarReversal(closed, upperBoundary, lowerBoundary));
        AddIfNotNull(result, DetectBreakoutCandle(closed, upperBoundary, lowerBoundary));
        AddIfNotNull(result, DetectShrinkingCandles(closed, upperBoundary, lowerBoundary));
        AddIfNotNull(result, DetectMomentumCandle(closed));
        return result
            .OrderByDescending(signal => signal.CandleTime)
            .ThenByDescending(signal => signal.Strength)
            .ToArray();
    }

    public decimal ScoreModifierForStrategy(string strategyName, StrategySide side, IReadOnlyList<PatternSignal> signals)
    {
        var sameSide = signals.Where(signal => signal.Side == side).ToArray();
        if (sameSide.Length == 0)
        {
            return 0m;
        }

        var modifier = 0m;
        foreach (var signal in sameSide)
        {
            modifier += signal.PatternName switch
            {
                "Engulfing" when strategyName == "NYSweepReversalStrategy" => 20m,
                "Pinbar" when strategyName == "NYSweepReversalStrategy" => 15m,
                "Momentum Candle" when strategyName == "TurtleTrendStrategy" => 20m,
                "Breakout Candle" when strategyName == "TurtleTrendStrategy" => 20m,
                "3-Bar Continuation" when strategyName == "TurtleTrendStrategy" => 12m,
                "3-Bar Reversal" when strategyName == "NYSweepReversalStrategy" => 10m,
                "Shrinking Candles" when strategyName == "NYSweepReversalStrategy" => 10m,
                _ => 5m
            };
        }

        return TradingIndicatorMath.Clamp(modifier, 0m, 25m);
    }

    private PatternSignal? DetectEngulfing(IReadOnlyList<Candle> closed, decimal upperBoundary, decimal lowerBoundary)
    {
        if (!_options.EngulfingEnabled || closed.Count < 2 || upperBoundary <= lowerBoundary)
        {
            return null;
        }

        var previous = closed[^2];
        var current = closed[^1];
        if (!IsInsideRange(previous.Close, upperBoundary, lowerBoundary) ||
            !IsInsideRange(current.Close, upperBoundary, lowerBoundary))
        {
            return null;
        }

        var previousBody = Math.Abs(previous.Close - previous.Open);
        var currentBody = Math.Abs(current.Close - current.Open);
        if (previousBody <= 0m || currentBody < previousBody * _options.MinEngulfingBodyRatio)
        {
            return null;
        }

        if (previous.Close < previous.Open &&
            current.Close > current.Open &&
            current.Open <= previous.Close &&
            current.Close >= previous.Open)
        {
            return Signal("Engulfing", StrategySide.Long, current.OpenTime, 70m, "Bullish engulfing confirmation.");
        }

        if (previous.Close > previous.Open &&
            current.Close < current.Open &&
            current.Open >= previous.Close &&
            current.Close <= previous.Open)
        {
            return Signal("Engulfing", StrategySide.Short, current.OpenTime, 70m, "Bearish engulfing confirmation.");
        }

        return null;
    }

    private PatternSignal? DetectPinbar(IReadOnlyList<Candle> closed, decimal upperBoundary, decimal lowerBoundary)
    {
        if (!_options.PinbarEnabled || closed.Count == 0 || upperBoundary <= lowerBoundary)
        {
            return null;
        }

        var current = closed[^1];
        if (!IsInsideRange(current.Open, upperBoundary, lowerBoundary) ||
            !IsInsideRange(current.Close, upperBoundary, lowerBoundary))
        {
            return null;
        }

        var range = current.High - current.Low;
        var body = Math.Abs(current.Close - current.Open);
        if (range <= 0m || body <= 0m || body / range * 100m > _options.MaxPinbarBodyRangePercent)
        {
            return null;
        }

        var upperWick = current.High - decimal.Max(current.Open, current.Close);
        var lowerWick = decimal.Min(current.Open, current.Close) - current.Low;
        if (lowerWick / body >= _options.MinPinbarWickBodyRatio &&
            lowerWick / range * 100m >= _options.MinPinbarWickRangePercent &&
            upperWick < lowerWick)
        {
            return Signal("Pinbar", StrategySide.Long, current.OpenTime, 65m, "Bullish pinbar confirmation.");
        }

        if (upperWick / body >= _options.MinPinbarWickBodyRatio &&
            upperWick / range * 100m >= _options.MinPinbarWickRangePercent &&
            lowerWick < upperWick)
        {
            return Signal("Pinbar", StrategySide.Short, current.OpenTime, 65m, "Bearish pinbar confirmation.");
        }

        return null;
    }

    private PatternSignal? DetectThreeBarContinuation(IReadOnlyList<Candle> closed, decimal upperBoundary, decimal lowerBoundary)
    {
        if (!_options.ThreeBarContinuationEnabled || closed.Count < 3 || upperBoundary <= lowerBoundary)
        {
            return null;
        }

        var first = closed[^3];
        var second = closed[^2];
        var third = closed[^1];
        if (!AreClosesInsideRange([first, second, third], upperBoundary, lowerBoundary))
        {
            return null;
        }

        var firstBody = Math.Abs(first.Close - first.Open);
        var secondBody = Math.Abs(second.Close - second.Open);
        var thirdBody = Math.Abs(third.Close - third.Open);
        if (firstBody <= 0m || secondBody <= 0m || thirdBody <= 0m ||
            decimal.Min(firstBody, thirdBody) / secondBody < _options.MinThreeBarOuterBodyRatio)
        {
            return null;
        }

        if (first.Close > first.Open && second.Close < second.Open && third.Close > third.Open && third.Close > first.Close)
        {
            return Signal("3-Bar Continuation", StrategySide.Long, third.OpenTime, 60m, "Bullish 3-bar continuation confirmation.");
        }

        if (first.Close < first.Open && second.Close > second.Open && third.Close < third.Open && third.Close < first.Close)
        {
            return Signal("3-Bar Continuation", StrategySide.Short, third.OpenTime, 60m, "Bearish 3-bar continuation confirmation.");
        }

        return null;
    }

    private PatternSignal? DetectThreeBarReversal(IReadOnlyList<Candle> closed, decimal upperBoundary, decimal lowerBoundary)
    {
        if (!_options.ThreeBarReversalEnabled || closed.Count < 3 || upperBoundary <= lowerBoundary)
        {
            return null;
        }

        var first = closed[^3];
        var second = closed[^2];
        var third = closed[^1];
        if (!AreClosesInsideRange([first, second, third], upperBoundary, lowerBoundary))
        {
            return null;
        }

        var firstBody = Math.Abs(first.Close - first.Open);
        var secondBody = Math.Abs(second.Close - second.Open);
        var thirdBody = Math.Abs(third.Close - third.Open);
        if (firstBody <= 0m || secondBody <= 0m || thirdBody <= 0m ||
            decimal.Min(firstBody, thirdBody) / secondBody < _options.MinThreeBarOuterBodyRatio)
        {
            return null;
        }

        if (first.Close < first.Open && second.Close < second.Open && third.Close > third.Open && third.Close > first.Open)
        {
            return Signal("3-Bar Reversal", StrategySide.Long, third.OpenTime, 60m, "Bullish 3-bar reversal confirmation.");
        }

        if (first.Close > first.Open && second.Close > second.Open && third.Close < third.Open && third.Close < first.Open)
        {
            return Signal("3-Bar Reversal", StrategySide.Short, third.OpenTime, 60m, "Bearish 3-bar reversal confirmation.");
        }

        return null;
    }

    private PatternSignal? DetectBreakoutCandle(IReadOnlyList<Candle> closed, decimal upperBoundary, decimal lowerBoundary)
    {
        var consolidationCount = Math.Max(2, _options.BreakoutConsolidationCandles);
        if (!_options.BreakoutCandleEnabled || closed.Count < consolidationCount + 1)
        {
            return null;
        }

        var breakout = closed[^1];
        var consolidation = closed.Skip(closed.Count - consolidationCount - 1).Take(consolidationCount).ToArray();
        var averageBody = consolidation.Average(candle => Math.Abs(candle.Close - candle.Open));
        var breakoutBody = Math.Abs(breakout.Close - breakout.Open);
        if (averageBody <= 0m || breakoutBody / averageBody < _options.MinBreakoutBodyRatio)
        {
            return null;
        }

        var high = consolidation.Max(candle => candle.High);
        var low = consolidation.Min(candle => candle.Low);
        if (breakout.Close > breakout.Open && breakout.Close > high)
        {
            return Signal("Breakout Candle", StrategySide.Long, breakout.OpenTime, 75m, "Bullish breakout candle confirmation.");
        }

        if (breakout.Close < breakout.Open && breakout.Close < low)
        {
            return Signal("Breakout Candle", StrategySide.Short, breakout.OpenTime, 75m, "Bearish breakout candle confirmation.");
        }

        return null;
    }

    private PatternSignal? DetectShrinkingCandles(IReadOnlyList<Candle> closed, decimal upperBoundary, decimal lowerBoundary)
    {
        var sequenceCount = Math.Max(3, _options.ShrinkingSequenceCandles);
        if (!_options.ShrinkingCandlesEnabled || closed.Count < sequenceCount + 1)
        {
            return null;
        }

        var sequence = closed.Skip(closed.Count - sequenceCount - 1).Take(sequenceCount).ToArray();
        var reversal = closed[^1];
        var bodies = sequence.Select(candle => Math.Abs(candle.Close - candle.Open)).ToArray();
        if (bodies.Any(body => body <= 0m))
        {
            return null;
        }

        for (var index = 1; index < bodies.Length; index++)
        {
            if (bodies[index - 1] / bodies[index] < _options.MinShrinkingBodyStepRatio)
            {
                return null;
            }
        }

        var reversalBody = Math.Abs(reversal.Close - reversal.Open);
        if (reversalBody <= 0m || reversalBody / bodies.Average() < _options.MinShrinkingReversalBodyRatio)
        {
            return null;
        }

        if (sequence.All(candle => candle.Close < candle.Open) && reversal.Close > reversal.Open)
        {
            return Signal("Shrinking Candles", StrategySide.Long, reversal.OpenTime, 65m, "Bullish shrinking candles reversal confirmation.");
        }

        if (sequence.All(candle => candle.Close > candle.Open) && reversal.Close < reversal.Open)
        {
            return Signal("Shrinking Candles", StrategySide.Short, reversal.OpenTime, 65m, "Bearish shrinking candles reversal confirmation.");
        }

        return null;
    }

    private static PatternSignal? DetectMomentumCandle(IReadOnlyList<Candle> closed)
    {
        if (closed.Count < 12)
        {
            return null;
        }

        var current = closed[^1];
        var averageBody = closed.SkipLast(1).TakeLast(10).Average(candle => Math.Abs(candle.Close - candle.Open));
        var currentBody = Math.Abs(current.Close - current.Open);
        if (averageBody <= 0m || currentBody / averageBody < 1.8m)
        {
            return null;
        }

        return current.Close > current.Open
            ? Signal("Momentum Candle", StrategySide.Long, current.OpenTime, 80m, "Bullish momentum candle confirmation.")
            : Signal("Momentum Candle", StrategySide.Short, current.OpenTime, 80m, "Bearish momentum candle confirmation.");
    }

    private static PatternSignal Signal(string name, StrategySide side, DateTimeOffset time, decimal strength, string reason) => new()
    {
        PatternName = name,
        Side = side,
        Strength = TradingIndicatorMath.Clamp(strength, 0m, 100m),
        Confidence = TradingIndicatorMath.Clamp(strength / 100m, 0.1m, 0.95m),
        CandleTime = time,
        Reason = reason
    };

    private static void AddIfNotNull(List<PatternSignal> result, PatternSignal? signal)
    {
        if (signal is not null)
        {
            result.Add(signal);
        }
    }

    private static bool AreClosesInsideRange(IReadOnlyList<Candle> candles, decimal upper, decimal lower) =>
        candles.All(candle => IsInsideRange(candle.Close, upper, lower));

    private static bool IsInsideRange(decimal price, decimal upper, decimal lower) =>
        price <= upper && price >= lower;
}
