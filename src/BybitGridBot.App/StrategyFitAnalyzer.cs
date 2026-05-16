using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.App;

public sealed record StrategyFitResult(
    decimal GridFitScore,
    decimal BtdFitScore,
    decimal ComboFitScore,
    decimal ReversalFitScore,
    decimal SelectedFitScore,
    string SelectedFitStrategy,
    IReadOnlyList<string> Reasons);

public static class StrategyFitAnalyzer
{
    public static StrategyFitResult Analyze(
        IReadOnlyList<Candle> candles,
        GridOptions options,
        decimal lastPrice,
        decimal atrPercent,
        decimal volatilityPercent,
        decimal momentumPercent,
        TradingStrategyType recommendedStrategy)
    {
        if (candles.Count < 30 || lastPrice <= 0m)
        {
            return new StrategyFitResult(0m, 0m, 0m, 0m, 0m, "Unknown", ["not enough fit data"]);
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var range = AnalyzeRangeShape(ordered, options, atrPercent, volatilityPercent, momentumPercent);
        var pullback = AnalyzePullbackShape(ordered, lastPrice, momentumPercent);

        var gridFit = ScoreGridFit(range);
        var btdFit = ScoreBtdFit(pullback, momentumPercent);
        var reversalFit = ScoreReversalFit(pullback, momentumPercent);
        var comboFit = ScoreComboFit(gridFit, btdFit, range);
        var selected = SelectFit(recommendedStrategy, gridFit, btdFit, comboFit, reversalFit);

        var reasons = new List<string>
        {
            $"grid fit {gridFit:0}",
            $"btd fit {btdFit:0}",
            $"combo fit {comboFit:0}",
            $"reversal fit {reversalFit:0}"
        };

        if (range.LevelTouchRatio >= 0.35m)
        {
            reasons.Add($"grid levels touched {range.LevelTouchRatio:P0}");
        }

        if (range.TrendEfficiency >= 0.65m)
        {
            reasons.Add($"trend-dominant candles {range.TrendEfficiency:P0}");
        }

        if (pullback.HasFreshLowerLow)
        {
            reasons.Add("fresh lower low");
        }
        else if (pullback.PullbackPercent >= 0.7m)
        {
            reasons.Add($"pullback {pullback.PullbackPercent:0.##}% stabilized");
        }

        return new StrategyFitResult(
            gridFit,
            btdFit,
            comboFit,
            reversalFit,
            selected.Score,
            selected.Name,
            reasons);
    }

    private static RangeShape AnalyzeRangeShape(
        IReadOnlyList<Candle> candles,
        GridOptions options,
        decimal atrPercent,
        decimal volatilityPercent,
        decimal momentumPercent)
    {
        var recent = candles.TakeLast(48).ToArray();
        var closes = recent.Select(candle => candle.Close).ToArray();
        var high = recent.Max(candle => candle.High);
        var low = recent.Min(candle => candle.Low);
        var mid = (high + low) / 2m;
        var levelTouchRatio = CalculateGridLevelTouchRatio(recent, options);
        var flipRatio = CalculateDirectionFlipRatio(closes);
        var midCrossRatio = CalculateMidCrossRatio(recent, mid);
        var trendEfficiency = CalculateTrendEfficiency(closes);
        var requiredMovePercent = Math.Max(options.MinNetProfitPercent, options.FeePercent * 2m + options.SlippagePercent * 2m + 0.15m);
        var candleProfitRatio = requiredMovePercent > 0m ? atrPercent / requiredMovePercent : atrPercent;
        var lastMovePercent = recent[^1].Open > 0m ? (recent[^1].Close - recent[^1].Open) / recent[^1].Open * 100m : 0m;

        return new RangeShape(
            volatilityPercent,
            Math.Abs(momentumPercent),
            levelTouchRatio,
            flipRatio,
            midCrossRatio,
            trendEfficiency,
            candleProfitRatio,
            lastMovePercent);
    }

    private static PullbackShape AnalyzePullbackShape(IReadOnlyList<Candle> candles, decimal lastPrice, decimal momentumPercent)
    {
        var recent = candles.TakeLast(30).ToArray();
        var high = recent.Max(candle => candle.High);
        var pullbackPercent = high > 0m ? (high - lastPrice) / high * 100m : 0m;
        var lastTwelve = recent.TakeLast(12).ToArray();
        var previous = recent.Take(recent.Length - lastTwelve.Length).ToArray();
        var recentLow = lastTwelve.Min(candle => candle.Low);
        var previousLow = previous.Length == 0 ? recentLow : previous.Min(candle => candle.Low);
        var hasFreshLowerLow = recentLow < previousLow * 0.995m;
        var closes = recent.Select(candle => candle.Close).ToArray();
        var rsi = CalculateRsi(closes, 14);
        var previousRsi = CalculateRsi(recent.Take(recent.Length - 3).Select(candle => candle.Close).ToArray(), 14);
        var volumeSpikeRatio = CalculateVolumeSpikeRatio(recent);
        var recoveryPercent = recentLow > 0m ? (lastPrice - recentLow) / recentLow * 100m : 0m;
        var closesRising = lastTwelve.Length >= 4 && lastTwelve[^1].Close > lastTwelve[^2].Close && lastTwelve[^2].Close >= lastTwelve[^4].Close;
        var dumpPercent = high > 0m ? (lastPrice - high) / high * 100m : momentumPercent;

        return new PullbackShape(
            pullbackPercent,
            recoveryPercent,
            rsi,
            previousRsi,
            volumeSpikeRatio,
            hasFreshLowerLow,
            closesRising,
            dumpPercent);
    }

    private static decimal ScoreGridFit(RangeShape range)
    {
        var score = 50m;
        score += range.VolatilityPercent is >= 1m and <= 9m ? 12m : -10m;
        score += range.AbsoluteMomentumPercent <= Math.Max(1m, range.VolatilityPercent * 0.55m) ? 14m : -16m;
        score += range.TrendEfficiency <= 0.35m ? 16m : range.TrendEfficiency >= 0.65m ? -22m : 4m;
        score += range.LevelTouchRatio >= 0.35m ? 18m : range.LevelTouchRatio >= 0.18m ? 8m : -14m;
        score += range.FlipRatio is >= 0.3m and <= 0.8m ? 12m : -8m;
        score += range.MidCrossRatio >= 0.15m ? 8m : -6m;
        score += range.CandleProfitRatio >= 1.15m ? 8m : -14m;
        if (range.LastMovePercent <= -3m)
        {
            score -= 20m;
        }

        return ClampScore(score);
    }

    private static decimal ScoreBtdFit(PullbackShape pullback, decimal momentumPercent)
    {
        var score = 45m;
        score += pullback.PullbackPercent is >= 0.7m and <= 8m ? 22m : -12m;
        score += pullback.Rsi is >= 25m and <= 55m ? 10m : pullback.Rsi < 20m ? -8m : -4m;
        score += pullback.HasFreshLowerLow ? -18m : 14m;
        score += pullback.RecoveryPercent is >= 0.15m and <= 4m ? 10m : 0m;
        score += pullback.ClosesRising ? 8m : -4m;
        score += pullback.VolumeSpikeRatio >= 1.25m ? 8m : 0m;
        if (momentumPercent > 8m || pullback.Rsi > 72m)
        {
            score -= 15m;
        }

        return ClampScore(score);
    }

    private static decimal ScoreReversalFit(PullbackShape pullback, decimal momentumPercent)
    {
        var score = 35m;
        score += pullback.DumpPercent is <= -5m and >= -25m ? 24m : -10m;
        score += pullback.HasFreshLowerLow ? -22m : 18m;
        score += pullback.ClosesRising ? 12m : -6m;
        score += pullback.VolumeSpikeRatio >= 1.4m ? 10m : 0m;
        score += pullback.Rsi > pullback.PreviousRsi && pullback.Rsi is >= 25m and <= 55m ? 14m : -4m;
        score += pullback.RecoveryPercent >= 0.25m ? 8m : -4m;
        if (momentumPercent <= -25m)
        {
            score -= 20m;
        }

        return ClampScore(score);
    }

    private static decimal ScoreComboFit(decimal gridFit, decimal btdFit, RangeShape range)
    {
        var score = gridFit * 0.55m + btdFit * 0.45m;
        if (range.TrendEfficiency is >= 0.25m and <= 0.6m && range.LevelTouchRatio >= 0.18m)
        {
            score += 8m;
        }

        if (range.LastMovePercent <= -3m)
        {
            score -= 14m;
        }

        return ClampScore(score);
    }

    private static (string Name, decimal Score) SelectFit(
        TradingStrategyType recommendedStrategy,
        decimal gridFit,
        decimal btdFit,
        decimal comboFit,
        decimal reversalFit) =>
        recommendedStrategy switch
        {
            TradingStrategyType.Grid => ("Grid", gridFit),
            TradingStrategyType.Btd => reversalFit > btdFit + 8m ? ("Reversal", reversalFit) : ("BTD", btdFit),
            TradingStrategyType.Combo => ("Combo", comboFit),
            TradingStrategyType.Hybrid => MaxFit(gridFit, btdFit, comboFit, reversalFit),
            TradingStrategyType.Signal or TradingStrategyType.TrendFollow or TradingStrategyType.TrendFollowing or TradingStrategyType.Breakout => ("Combo", comboFit),
            _ => MaxFit(gridFit, btdFit, comboFit, reversalFit)
        };

    private static (string Name, decimal Score) MaxFit(decimal gridFit, decimal btdFit, decimal comboFit, decimal reversalFit)
    {
        var candidates = new[]
        {
            ("Grid", gridFit),
            ("BTD", btdFit),
            ("Combo", comboFit),
            ("Reversal", reversalFit)
        };

        return candidates.OrderByDescending(candidate => candidate.Item2).First();
    }

    private static decimal CalculateGridLevelTouchRatio(IReadOnlyList<Candle> candles, GridOptions options)
    {
        if (options.Step <= 0m || options.LowerPrice <= 0m || options.UpperPrice <= options.LowerPrice)
        {
            return 0m;
        }

        var levels = new List<decimal>();
        for (var level = options.LowerPrice; level <= options.UpperPrice && levels.Count < 120; level += options.Step)
        {
            levels.Add(level);
        }

        if (levels.Count == 0)
        {
            return 0m;
        }

        var touchedCandles = candles.Count(candle => levels.Any(level => candle.Low <= level && candle.High >= level));
        return touchedCandles / (decimal)candles.Count;
    }

    private static decimal CalculateDirectionFlipRatio(IReadOnlyList<decimal> closes)
    {
        if (closes.Count < 4)
        {
            return 0m;
        }

        var directions = new List<int>();
        for (var index = 1; index < closes.Count; index++)
        {
            var diff = closes[index] - closes[index - 1];
            if (diff != 0m)
            {
                directions.Add(diff > 0m ? 1 : -1);
            }
        }

        if (directions.Count < 2)
        {
            return 0m;
        }

        var flips = 0;
        for (var index = 1; index < directions.Count; index++)
        {
            if (directions[index] != directions[index - 1])
            {
                flips++;
            }
        }

        return flips / (decimal)(directions.Count - 1);
    }

    private static decimal CalculateMidCrossRatio(IReadOnlyList<Candle> candles, decimal mid)
    {
        if (mid <= 0m)
        {
            return 0m;
        }

        var crosses = candles.Count(candle => (candle.Open <= mid && candle.Close >= mid) || (candle.Open >= mid && candle.Close <= mid));
        return crosses / (decimal)candles.Count;
    }

    private static decimal CalculateTrendEfficiency(IReadOnlyList<decimal> closes)
    {
        if (closes.Count < 3)
        {
            return 0m;
        }

        var directMove = Math.Abs(closes[^1] - closes[0]);
        var pathMove = 0m;
        for (var index = 1; index < closes.Count; index++)
        {
            pathMove += Math.Abs(closes[index] - closes[index - 1]);
        }

        return pathMove > 0m ? directMove / pathMove : 0m;
    }

    private static decimal CalculateRsi(IReadOnlyList<decimal> closes, int period)
    {
        if (closes.Count <= period)
        {
            return 50m;
        }

        var gains = 0m;
        var losses = 0m;
        for (var index = closes.Count - period; index < closes.Count; index++)
        {
            var diff = closes[index] - closes[index - 1];
            if (diff >= 0m)
            {
                gains += diff;
            }
            else
            {
                losses += Math.Abs(diff);
            }
        }

        if (losses == 0m)
        {
            return 100m;
        }

        var relativeStrength = gains / losses;
        return 100m - 100m / (1m + relativeStrength);
    }

    private static decimal CalculateVolumeSpikeRatio(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 12)
        {
            return 0m;
        }

        var recent = candles.TakeLast(6).Average(candle => candle.Turnover);
        var baselineCandles = candles.Take(candles.Count - 6).TakeLast(24).ToArray();
        var baseline = baselineCandles.Length == 0 ? 0m : baselineCandles.Average(candle => candle.Turnover);
        return baseline > 0m ? recent / baseline : 0m;
    }

    private static decimal ClampScore(decimal score) =>
        decimal.Max(0m, decimal.Min(100m, decimal.Round(score, 2, MidpointRounding.AwayFromZero)));

    private sealed record RangeShape(
        decimal VolatilityPercent,
        decimal AbsoluteMomentumPercent,
        decimal LevelTouchRatio,
        decimal FlipRatio,
        decimal MidCrossRatio,
        decimal TrendEfficiency,
        decimal CandleProfitRatio,
        decimal LastMovePercent);

    private sealed record PullbackShape(
        decimal PullbackPercent,
        decimal RecoveryPercent,
        decimal Rsi,
        decimal PreviousRsi,
        decimal VolumeSpikeRatio,
        bool HasFreshLowerLow,
        bool ClosesRising,
        decimal DumpPercent);
}
