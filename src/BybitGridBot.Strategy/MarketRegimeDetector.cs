using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class MarketRegimeDetector
{
    private readonly MarketRegimeFilter _marketRegimeFilter;

    public MarketRegimeDetector()
        : this(new MarketRegimeFilter())
    {
    }

    public MarketRegimeDetector(MarketRegimeFilter marketRegimeFilter)
    {
        _marketRegimeFilter = marketRegimeFilter;
    }

    public MarketRegime Detect(MarketSnapshot snapshot, GridOptions options)
    {
        var candles = snapshot.Candles.OrderBy(candle => candle.OpenTime).ToArray();
        var minimumCandles = Math.Max(options.TrendEmaSlow + 5, Math.Max(options.AdxPeriod + 2, options.VolumeSmaPeriod + 2));
        if (candles.Length < minimumCandles || snapshot.CurrentPrice <= 0m)
        {
            return MarketRegime.Unknown;
        }

        var currentPrice = snapshot.CurrentPrice;
        var adx = _marketRegimeFilter.CalculateAdx(candles, options.AdxPeriod);
        var emaFast = CalculateEma(candles, options.TrendEmaFast);
        var emaSlow = CalculateEma(candles, options.TrendEmaSlow);
        var volumeSma = candles.TakeLast(options.VolumeSmaPeriod).Average(candle => candle.Volume);
        var lastVolume = candles[^1].Volume;
        var hasVolumeSpike = volumeSma > 0m && lastVolume > volumeSma * options.VolumeSpikeMultiplier;
        var resistance = Math.Max(options.UpperPrice, candles.TakeLast(30).Max(candle => candle.High));
        var support = Math.Min(options.LowerPrice, candles.TakeLast(30).Min(candle => candle.Low));
        var atrSpike = HasAtrSpike(candles, options.AtrPeriod, options.AtrSpikeMultiplier);
        var btcRiskOff = HasBtcRiskOff(snapshot.BtcCandles, options.BtcLookbackCandles, options.BtcMaxMovePercent);

        if (atrSpike || (options.BtcFilterEnabled && btcRiskOff))
        {
            return MarketRegime.HighVolatility;
        }

        if ((currentPrice > options.UpperPrice || currentPrice > resistance) &&
            hasVolumeSpike &&
            adx >= options.BreakoutAdxMin)
        {
            return MarketRegime.BreakoutUp;
        }

        if ((currentPrice < options.LowerPrice || currentPrice < support) &&
            hasVolumeSpike &&
            adx >= options.BreakoutAdxMin)
        {
            return MarketRegime.BreakoutDown;
        }

        if (emaFast > emaSlow && adx >= options.TrendAdxMin && currentPrice > emaSlow)
        {
            return MarketRegime.Uptrend;
        }

        if (emaFast < emaSlow && adx >= options.TrendAdxMin && currentPrice < emaSlow)
        {
            return MarketRegime.Downtrend;
        }

        if (adx < options.RangeAdxMax &&
            currentPrice >= options.LowerPrice &&
            currentPrice <= options.UpperPrice &&
            !hasVolumeSpike)
        {
            return MarketRegime.RangeBound;
        }

        return MarketRegime.Unknown;
    }

    internal static decimal CalculateEma(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count == 0)
        {
            return 0m;
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var multiplier = 2m / (period + 1);
        var ema = ordered[0].Close;
        foreach (var candle in ordered.Skip(1))
        {
            ema = (candle.Close - ema) * multiplier + ema;
        }

        return ema;
    }

    internal static decimal CalculateAtr(IReadOnlyList<Candle> candles, int period)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < period + 1)
        {
            return 0m;
        }

        var trueRanges = new List<decimal>();
        for (var index = 1; index < ordered.Length; index++)
        {
            var current = ordered[index];
            var previous = ordered[index - 1];
            trueRanges.Add(decimal.Max(
                current.High - current.Low,
                decimal.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close))));
        }

        return trueRanges.TakeLast(period).Average();
    }

    private static bool HasAtrSpike(IReadOnlyList<Candle> candles, int period, decimal multiplier)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < period * 3)
        {
            return false;
        }

        var recentAtr = CalculateAtr(ordered.TakeLast(period + 1).ToArray(), period);
        var baselineAtr = CalculateAtr(ordered.Take(ordered.Length - period).TakeLast(period * 2 + 1).ToArray(), period);
        return baselineAtr > 0m && recentAtr > baselineAtr * multiplier;
    }

    private static bool HasBtcRiskOff(IReadOnlyList<Candle> btcCandles, int lookbackCandles, decimal maxMovePercent)
    {
        var slice = btcCandles.OrderBy(candle => candle.OpenTime).TakeLast(Math.Max(1, lookbackCandles)).ToArray();
        if (slice.Length < Math.Max(1, lookbackCandles))
        {
            return false;
        }

        var open = slice[0].Open;
        var close = slice[^1].Close;
        if (open <= 0m)
        {
            return false;
        }

        return Math.Abs((close - open) / open * 100m) > maxMovePercent;
    }
}
