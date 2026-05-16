using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed record ReversalBtdSetup(
    decimal DrawdownPercent,
    int CandlesSinceLow,
    decimal BuyVolumeRatio,
    decimal Rsi,
    decimal TrendStrength,
    decimal RecentLow,
    decimal LastPrice);

public static class ReversalBtdDetector
{
    private const int MinimumCandles = 45;
    private const decimal MinimumDrawdownPercent = 8m;
    private const decimal MaximumDrawdownPercent = 45m;
    private const int MinimumCandlesSinceLow = 3;
    private const decimal MinimumBuyVolumeRatio = 1.2m;

    public static bool TryDetect(IReadOnlyList<Candle> candles, out ReversalBtdSetup setup)
    {
        return TryDetect(candles, new SignalAnalyzer().Analyze(candles), out setup);
    }

    public static bool TryDetect(IReadOnlyList<Candle> candles, SignalAnalysis signal, out ReversalBtdSetup setup)
    {
        setup = new ReversalBtdSetup(0m, 0, 0m, signal.Rsi, signal.TrendStrength, 0m, 0m);

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < MinimumCandles)
        {
            return false;
        }

        var window = ordered.TakeLast(Math.Min(120, ordered.Length)).ToArray();
        var last = window[^1];
        if (last.Close <= 0m)
        {
            return false;
        }

        var high = window.Max(candle => candle.High);
        var drawdownPercent = high > 0m ? (high - last.Close) / high * 100m : 0m;
        if (drawdownPercent < MinimumDrawdownPercent || drawdownPercent > MaximumDrawdownPercent)
        {
            return false;
        }

        var lowestIndex = 0;
        for (var index = 1; index < window.Length; index++)
        {
            if (window[index].Low < window[lowestIndex].Low)
            {
                lowestIndex = index;
            }
        }

        var recentLow = window[lowestIndex].Low;
        var candlesSinceLow = window.Length - lowestIndex - 1;
        if (candlesSinceLow < MinimumCandlesSinceLow)
        {
            return false;
        }

        var lastThree = window.TakeLast(3).ToArray();
        var recentClosesRecovering = lastThree[^1].Close >= lastThree[0].Close ||
            (recentLow > 0m && last.Close >= recentLow * 1.01m);
        var lastCandleNotDumping = last.Close >= last.Open * 0.995m &&
            last.Close >= window[^2].Close * 0.997m;
        if (!recentClosesRecovering || !lastCandleNotDumping)
        {
            return false;
        }

        var buyVolumeRatio = CalculateBuyVolumeRatio(ordered);
        if (buyVolumeRatio < MinimumBuyVolumeRatio)
        {
            return false;
        }

        var rsiRecovering = signal.Rsi is >= 30m and <= 62m;
        var emaTurning = last.Close >= signal.EmaFast ||
            signal.TrendStrength >= -0.25m ||
            recentClosesRecovering;
        var notChasingUpperBand = signal.BollingerPosition < 0.9m;
        if (!rsiRecovering || !emaTurning || !notChasingUpperBand)
        {
            return false;
        }

        setup = new ReversalBtdSetup(
            decimal.Round(drawdownPercent, 4, MidpointRounding.AwayFromZero),
            candlesSinceLow,
            decimal.Round(buyVolumeRatio, 4, MidpointRounding.AwayFromZero),
            signal.Rsi,
            signal.TrendStrength,
            recentLow,
            last.Close);
        return true;
    }

    private static decimal CalculateBuyVolumeRatio(IReadOnlyList<Candle> ordered)
    {
        var recentCount = Math.Min(5, ordered.Count);
        var recent = ordered.TakeLast(recentCount).ToArray();
        var baseline = ordered
            .Take(ordered.Count - recentCount)
            .TakeLast(Math.Min(30, Math.Max(1, ordered.Count - recentCount)))
            .ToArray();
        var baselineVolume = baseline.Length > 0
            ? baseline.Average(candle => candle.Volume)
            : recent.Average(candle => candle.Volume);
        if (baselineVolume <= 0m)
        {
            return 0m;
        }

        var buyVolume = recent
            .Where(candle => candle.Close >= candle.Open)
            .Select(candle => candle.Volume)
            .DefaultIfEmpty(0m)
            .Max();
        return buyVolume / baselineVolume;
    }
}
