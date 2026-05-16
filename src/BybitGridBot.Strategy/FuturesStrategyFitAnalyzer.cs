using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class FuturesStrategyFitAnalyzer
{
    public FuturesStrategyFitResult Analyze(IReadOnlyList<Candle> candles)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < 20)
        {
            return new FuturesStrategyFitResult
            {
                BestStrategyType = FuturesStrategyType.Pause,
                Direction = FuturesDirection.LongOnly,
                Reasons = ["not enough candle data for futures fit"]
            };
        }

        var lookback = ordered.TakeLast(Math.Min(72, ordered.Length)).ToArray();
        var first = lookback[0].Open > 0m ? lookback[0].Open : lookback[0].Close;
        var last = lookback[^1].Close;
        var high = lookback.Max(candle => candle.High);
        var low = lookback.Min(candle => candle.Low);
        var rangePercent = last > 0m ? (high - low) / last * 100m : 0m;
        var movePercent = first > 0m ? (last - first) / first * 100m : 0m;
        var atrPercent = CalculateAtrPercent(lookback, last);
        var closeLocation = high > low ? (last - low) / (high - low) : 0.5m;
        var meanReversionCrosses = CountMeanReversionCrosses(lookback);
        var maxStreak = CountMaxDirectionalStreak(lookback);
        var dumpRisk = HasLargeDirectionalCandle(lookback, -2.5m);
        var pumpRisk = HasLargeDirectionalCandle(lookback, 2.5m);

        var gridBase = ScoreGrid(rangePercent, movePercent, atrPercent, meanReversionCrosses, maxStreak, dumpRisk || pumpRisk);
        var trendLong = ScoreTrend(movePercent, atrPercent, closeLocation, maxStreak, isLong: true);
        var trendShort = ScoreTrend(movePercent, atrPercent, closeLocation, maxStreak, isLong: false);
        var breakout = ScoreBreakout(movePercent, atrPercent, closeLocation, isLong: true);
        var breakdown = ScoreBreakout(movePercent, atrPercent, closeLocation, isLong: false);

        var gridLong = gridBase - (movePercent < -2m ? 12m : 0m);
        var gridShort = gridBase - (movePercent > 2m ? 12m : 0m);
        var scores = new (FuturesStrategyType Strategy, FuturesDirection Direction, decimal Score)[]
        {
            (FuturesStrategyType.GridLongOnly, FuturesDirection.LongOnly, gridLong),
            (FuturesStrategyType.GridShortOnly, FuturesDirection.ShortOnly, gridShort),
            (FuturesStrategyType.TrendFollow, FuturesDirection.LongOnly, trendLong),
            (FuturesStrategyType.TrendFollowShortOnly, FuturesDirection.ShortOnly, trendShort),
            (FuturesStrategyType.Breakout, FuturesDirection.LongOnly, breakout),
            (FuturesStrategyType.BreakdownShort, FuturesDirection.ShortOnly, breakdown)
        };
        var best = scores.OrderByDescending(item => item.Score).First();
        var reasons = new List<string>
        {
            $"fit {best.Strategy} {best.Score:0.#}",
            $"range {rangePercent:0.##}%",
            $"move {movePercent:0.##}%",
            $"ATR {atrPercent:0.###}%",
            $"mean crosses {meanReversionCrosses}",
            $"max streak {maxStreak}"
        };

        return new FuturesStrategyFitResult
        {
            BestStrategyType = best.Score < 20m ? FuturesStrategyType.Pause : best.Strategy,
            Direction = best.Score < 20m ? FuturesDirection.LongOnly : best.Direction,
            GridLongOnlyScore = ClampScore(gridLong),
            GridShortOnlyScore = ClampScore(gridShort),
            TrendFollowScore = ClampScore(trendLong),
            TrendFollowShortOnlyScore = ClampScore(trendShort),
            BreakoutScore = ClampScore(breakout),
            BreakdownShortScore = ClampScore(breakdown),
            RangePercent = decimal.Round(rangePercent, 4, MidpointRounding.AwayFromZero),
            MovePercent = decimal.Round(movePercent, 4, MidpointRounding.AwayFromZero),
            MeanReversionCrosses = meanReversionCrosses,
            MaxDirectionalStreak = maxStreak,
            Reasons = reasons
        };
    }

    private static decimal ScoreGrid(
        decimal rangePercent,
        decimal movePercent,
        decimal atrPercent,
        int meanReversionCrosses,
        int maxStreak,
        bool hasLargeDirectionalCandle)
    {
        var score = 35m;
        score += rangePercent is >= 1m and <= 8m ? 20m : rangePercent > 12m ? -12m : -6m;
        score += meanReversionCrosses switch
        {
            >= 10 => 20m,
            >= 6 => 12m,
            >= 3 => 5m,
            _ => -10m
        };
        score += Math.Abs(movePercent) <= decimal.Max(0.5m, rangePercent * 0.45m) ? 15m : -15m;
        score += atrPercent is >= 0.06m and <= 1.2m ? 10m : -6m;
        if (maxStreak >= 7)
        {
            score -= 14m;
        }

        if (hasLargeDirectionalCandle)
        {
            score -= 12m;
        }

        return ClampScore(score);
    }

    private static decimal ScoreTrend(decimal movePercent, decimal atrPercent, decimal closeLocation, int maxStreak, bool isLong)
    {
        var directionalMove = isLong ? movePercent : -movePercent;
        var location = isLong ? closeLocation : 1m - closeLocation;
        var score = 30m;
        score += directionalMove switch
        {
            >= 4m => 25m,
            >= 1.2m => 18m,
            >= 0.4m => 8m,
            <= -0.8m => -20m,
            _ => -4m
        };
        score += location >= 0.65m ? 15m : location >= 0.5m ? 6m : -8m;
        score += atrPercent is >= 0.05m and <= 2.5m ? 8m : -8m;
        score += maxStreak >= 3 ? 6m : 0m;
        return ClampScore(score);
    }

    private static decimal ScoreBreakout(decimal movePercent, decimal atrPercent, decimal closeLocation, bool isLong)
    {
        var directionalMove = isLong ? movePercent : -movePercent;
        var location = isLong ? closeLocation : 1m - closeLocation;
        var score = 25m;
        score += directionalMove >= 0.5m ? 15m : -10m;
        score += location >= 0.8m ? 25m : location >= 0.65m ? 12m : -12m;
        score += atrPercent is >= 0.08m and <= 2m ? 10m : -8m;
        return ClampScore(score);
    }

    private static int CountMeanReversionCrosses(IReadOnlyList<Candle> candles)
    {
        var closes = candles.Select(candle => candle.Close).ToArray();
        var mean = closes.Average();
        var crosses = 0;
        var previous = Math.Sign(closes[0] - mean);
        foreach (var close in closes.Skip(1))
        {
            var current = Math.Sign(close - mean);
            if (current != 0 && previous != 0 && current != previous)
            {
                crosses++;
            }

            if (current != 0)
            {
                previous = current;
            }
        }

        return crosses;
    }

    private static int CountMaxDirectionalStreak(IReadOnlyList<Candle> candles)
    {
        var max = 0;
        var current = 0;
        var previousSign = 0;
        foreach (var candle in candles)
        {
            var sign = Math.Sign(candle.Close - candle.Open);
            if (sign == 0)
            {
                current = 0;
                previousSign = 0;
                continue;
            }

            current = sign == previousSign ? current + 1 : 1;
            previousSign = sign;
            max = Math.Max(max, current);
        }

        return max;
    }

    private static bool HasLargeDirectionalCandle(IReadOnlyList<Candle> candles, decimal thresholdPercent) =>
        candles.Any(candle =>
        {
            if (candle.Open <= 0m)
            {
                return false;
            }

            var move = (candle.Close - candle.Open) / candle.Open * 100m;
            return thresholdPercent < 0m ? move <= thresholdPercent : move >= thresholdPercent;
        });

    private static decimal CalculateAtrPercent(IReadOnlyList<Candle> candles, decimal lastPrice)
    {
        if (candles.Count < 2 || lastPrice <= 0m)
        {
            return 0m;
        }

        var total = 0m;
        for (var index = 1; index < candles.Count; index++)
        {
            var current = candles[index];
            var previous = candles[index - 1];
            total += decimal.Max(
                current.High - current.Low,
                decimal.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
        }

        return total / (candles.Count - 1) / lastPrice * 100m;
    }

    private static decimal ClampScore(decimal score) =>
        decimal.Round(Math.Clamp(score, 0m, 100m), 2, MidpointRounding.AwayFromZero);
}

public sealed class FuturesStrategyFitResult
{
    public FuturesStrategyType BestStrategyType { get; init; } = FuturesStrategyType.Pause;

    public FuturesDirection Direction { get; init; } = FuturesDirection.LongOnly;

    public decimal GridLongOnlyScore { get; init; }

    public decimal GridShortOnlyScore { get; init; }

    public decimal TrendFollowScore { get; init; }

    public decimal TrendFollowShortOnlyScore { get; init; }

    public decimal BreakoutScore { get; init; }

    public decimal BreakdownShortScore { get; init; }

    public decimal RangePercent { get; init; }

    public decimal MovePercent { get; init; }

    public int MeanReversionCrosses { get; init; }

    public int MaxDirectionalStreak { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }
}
