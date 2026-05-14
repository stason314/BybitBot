using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class SignalAnalyzer
{
    private const int FastEmaPeriod = 9;
    private const int SlowEmaPeriod = 21;
    private const int RsiPeriod = 14;
    private const int BollingerPeriod = 20;

    public SignalAnalysis Analyze(IReadOnlyList<Candle> candles)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < 30)
        {
            return new SignalAnalysis
            {
                Signal = SignalType.Avoid,
                Confidence = 0.1m,
                Reason = "Not enough candles for signal analysis."
            };
        }

        var close = ordered[^1].Close;
        var emaFast = CalculateEma(ordered, FastEmaPeriod);
        var emaSlow = CalculateEma(ordered, SlowEmaPeriod);
        var rsi = CalculateRsi(ordered, RsiPeriod);
        var bollingerPosition = CalculateBollingerPosition(ordered, BollingerPeriod);
        var volumeRatio = CalculateVolumeRatio(ordered);
        var trendStrength = close > 0m ? (emaFast - emaSlow) / close * 100m : 0m;

        var emaLabel = trendStrength switch
        {
            > 0.03m => "EMA bullish",
            < -0.03m => "EMA bearish",
            _ => "EMA flat"
        };
        var rsiLabel = rsi switch
        {
            >= 70m => "RSI overbought",
            <= 30m => "RSI oversold",
            _ => "RSI neutral"
        };
        var bollingerLabel = bollingerPosition switch
        {
            >= 0.8m => "Bollinger upper",
            <= 0.2m => "Bollinger lower",
            _ => "Bollinger middle"
        };
        var volumeLabel = volumeRatio >= 1.8m ? "volume spike" : "no volume spike";

        var signal = ResolveSignal(trendStrength, rsi, bollingerPosition, volumeRatio);
        var confidence = ResolveConfidence(signal, trendStrength, rsi, bollingerPosition, volumeRatio);

        return new SignalAnalysis
        {
            Signal = signal,
            Confidence = decimal.Round(confidence, 4, MidpointRounding.AwayFromZero),
            Reason = $"{emaLabel}, {rsiLabel}, {bollingerLabel}, {volumeLabel}",
            EmaFast = decimal.Round(emaFast, 8, MidpointRounding.AwayFromZero),
            EmaSlow = decimal.Round(emaSlow, 8, MidpointRounding.AwayFromZero),
            Rsi = decimal.Round(rsi, 4, MidpointRounding.AwayFromZero),
            BollingerPosition = decimal.Round(bollingerPosition, 4, MidpointRounding.AwayFromZero),
            VolumeRatio = decimal.Round(volumeRatio, 4, MidpointRounding.AwayFromZero),
            TrendStrength = decimal.Round(trendStrength, 4, MidpointRounding.AwayFromZero)
        };
    }

    private static SignalType ResolveSignal(
        decimal trendStrength,
        decimal rsi,
        decimal bollingerPosition,
        decimal volumeRatio)
    {
        if ((rsi <= 20m && trendStrength < 0m) || (volumeRatio >= 3m && trendStrength < -0.05m))
        {
            return SignalType.Avoid;
        }

        if ((rsi >= 75m && bollingerPosition >= 0.75m) || (trendStrength < -0.08m && rsi >= 45m))
        {
            return SignalType.Sell;
        }

        if (trendStrength > 0.08m && rsi is >= 35m and <= 68m && bollingerPosition <= 0.85m)
        {
            return SignalType.Buy;
        }

        return SignalType.Hold;
    }

    private static decimal ResolveConfidence(
        SignalType signal,
        decimal trendStrength,
        decimal rsi,
        decimal bollingerPosition,
        decimal volumeRatio)
    {
        var confidence = signal == SignalType.Hold ? 0.45m : 0.5m;
        var absTrend = Math.Abs(trendStrength);

        if (absTrend >= 0.08m)
        {
            confidence += 0.12m;
        }

        if (rsi is > 35m and < 65m)
        {
            confidence += signal == SignalType.Hold ? 0.08m : 0.05m;
        }
        else if (rsi is <= 30m or >= 70m)
        {
            confidence += 0.1m;
        }

        if (bollingerPosition is <= 0.2m or >= 0.8m)
        {
            confidence += 0.08m;
        }

        if (volumeRatio >= 1.8m)
        {
            confidence += 0.08m;
        }

        return Math.Min(0.95m, confidence);
    }

    private static decimal CalculateEma(IReadOnlyList<Candle> candles, int period)
    {
        var multiplier = 2m / (period + 1);
        var ema = candles[0].Close;
        foreach (var candle in candles.Skip(1))
        {
            ema = (candle.Close - ema) * multiplier + ema;
        }

        return ema;
    }

    private static decimal CalculateRsi(IReadOnlyList<Candle> candles, int period)
    {
        var window = candles.TakeLast(period + 1).ToArray();
        var gains = 0m;
        var losses = 0m;
        for (var index = 1; index < window.Length; index++)
        {
            var change = window[index].Close - window[index - 1].Close;
            if (change >= 0m)
            {
                gains += change;
            }
            else
            {
                losses += Math.Abs(change);
            }
        }

        var averageGain = gains / period;
        var averageLoss = losses / period;
        if (averageLoss == 0m)
        {
            return averageGain == 0m ? 50m : 100m;
        }

        var relativeStrength = averageGain / averageLoss;
        return 100m - 100m / (1m + relativeStrength);
    }

    private static decimal CalculateBollingerPosition(IReadOnlyList<Candle> candles, int period)
    {
        var window = candles.TakeLast(period).Select(candle => candle.Close).ToArray();
        var average = window.Average();
        var variance = window
            .Select(value => Math.Pow((double)(value - average), 2))
            .Average();
        var standardDeviation = (decimal)Math.Sqrt(variance);
        var upper = average + standardDeviation * 2m;
        var lower = average - standardDeviation * 2m;
        var width = upper - lower;
        if (width <= 0m)
        {
            return 0.5m;
        }

        return Math.Clamp((candles[^1].Close - lower) / width, 0m, 1m);
    }

    private static decimal CalculateVolumeRatio(IReadOnlyList<Candle> candles)
    {
        var recentCount = Math.Min(5, candles.Count);
        var recentVolume = candles.TakeLast(recentCount).Average(candle => candle.Volume);
        var baseline = candles
            .Take(candles.Count - recentCount)
            .TakeLast(Math.Min(30, Math.Max(1, candles.Count - recentCount)))
            .ToArray();
        var baselineVolume = baseline.Length > 0
            ? baseline.Average(candle => candle.Volume)
            : recentVolume;

        return baselineVolume > 0m ? recentVolume / baselineVolume : 1m;
    }
}
