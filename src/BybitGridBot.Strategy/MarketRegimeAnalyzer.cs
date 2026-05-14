using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class MarketRegimeAnalyzer
{
    private readonly MarketRegimeFilter _marketRegimeFilter;

    public MarketRegimeAnalyzer(MarketRegimeFilter marketRegimeFilter)
    {
        _marketRegimeFilter = marketRegimeFilter;
    }

    public MarketRegimeAnalysis Analyze(IReadOnlyList<Candle> candles)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < 10)
        {
            return new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Danger,
                Confidence = 0.2m,
                Recommendation = "Not enough market data. Keep trading conservative."
            };
        }

        var first = ordered[0];
        var last = ordered[^1];
        var high = ordered.Max(candle => candle.High);
        var low = ordered.Min(candle => candle.Low);
        var referencePrice = last.Close > 0m ? last.Close : first.Open;
        var movePercent = first.Open > 0m ? (last.Close - first.Open) / first.Open * 100m : 0m;
        var rangePercent = referencePrice > 0m ? (high - low) / referencePrice * 100m : 0m;
        var adx = _marketRegimeFilter.CalculateAdx(ordered);
        var recentCount = Math.Min(5, ordered.Length);
        var recentVolume = ordered.TakeLast(recentCount).Average(candle => candle.Volume);
        var baselineCandles = ordered.Take(Math.Max(1, ordered.Length - recentCount)).ToArray();
        var baselineVolume = baselineCandles.Length > 0 ? baselineCandles.Average(candle => candle.Volume) : recentVolume;
        var volumeRatio = baselineVolume > 0m ? recentVolume / baselineVolume : 1m;
        var support = ordered.TakeLast(Math.Min(20, ordered.Length)).Min(candle => candle.Low);
        var resistance = ordered.TakeLast(Math.Min(20, ordered.Length)).Max(candle => candle.High);

        if (Math.Abs(movePercent) >= 2.5m || adx >= 35m)
        {
            return Build(
                MarketRegimeType.Danger,
                0.85m,
                "Strong move or high ADX. Prefer no new grid buys until volatility cools down.",
                adx,
                movePercent,
                rangePercent,
                volumeRatio,
                support,
                resistance);
        }

        if (volumeRatio >= 2m && Math.Abs(movePercent) >= 0.8m)
        {
            return Build(
                MarketRegimeType.Breakout,
                0.75m,
                "Volume breakout detected. Grid may lag; consider breakout strategy or wait for confirmation.",
                adx,
                movePercent,
                rangePercent,
                volumeRatio,
                support,
                resistance);
        }

        if (adx >= 22m || Math.Abs(movePercent) >= 1m)
        {
            return Build(
                MarketRegimeType.Trend,
                0.65m,
                "Directional trend detected. Use wider grid or reduce order size.",
                adx,
                movePercent,
                rangePercent,
                volumeRatio,
                support,
                resistance);
        }

        if (rangePercent <= 0.35m)
        {
            return Build(
                MarketRegimeType.LowVolatility,
                0.7m,
                "Low volatility. Grid profit per cycle may be eaten by fees.",
                adx,
                movePercent,
                rangePercent,
                volumeRatio,
                support,
                resistance);
        }

        return Build(
            MarketRegimeType.Range,
            0.7m,
            "Range market. Grid strategy is suitable if fees and spread are covered.",
            adx,
            movePercent,
            rangePercent,
            volumeRatio,
            support,
            resistance);
    }

    private static MarketRegimeAnalysis Build(
        MarketRegimeType regime,
        decimal confidence,
        string recommendation,
        decimal adx,
        decimal movePercent,
        decimal rangePercent,
        decimal volumeRatio,
        decimal support,
        decimal resistance) => new()
    {
        Regime = regime,
        Confidence = confidence,
        Recommendation = recommendation,
        Adx = decimal.Round(adx, 4, MidpointRounding.AwayFromZero),
        MovePercent = decimal.Round(movePercent, 4, MidpointRounding.AwayFromZero),
        RangePercent = decimal.Round(rangePercent, 4, MidpointRounding.AwayFromZero),
        VolumeRatio = decimal.Round(volumeRatio, 4, MidpointRounding.AwayFromZero),
        Support = support,
        Resistance = resistance
    };
}
