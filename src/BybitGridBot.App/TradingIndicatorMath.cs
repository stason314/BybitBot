using BybitGridBot.Domain;

namespace BybitGridBot.App;

public static class TradingIndicatorMath
{
    public static decimal Ema(IReadOnlyList<decimal> values, int period)
    {
        if (values.Count == 0)
        {
            return 0m;
        }

        if (period <= 1 || values.Count == 1)
        {
            return values[^1];
        }

        var multiplier = 2m / (period + 1m);
        var ema = values.Take(Math.Min(period, values.Count)).Average();
        for (var index = Math.Min(period, values.Count); index < values.Count; index++)
        {
            ema = (values[index] - ema) * multiplier + ema;
        }

        return ema;
    }

    public static decimal Atr(IReadOnlyList<Candle> candles, int period)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < 2 || period <= 0)
        {
            return 0m;
        }

        var trueRanges = new List<decimal>();
        for (var index = 1; index < ordered.Length; index++)
        {
            var current = ordered[index];
            var previous = ordered[index - 1];
            var trueRange = decimal.Max(
                current.High - current.Low,
                decimal.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
            trueRanges.Add(trueRange);
        }

        return trueRanges.TakeLast(Math.Min(period, trueRanges.Count)).DefaultIfEmpty(0m).Average();
    }

    public static decimal Adx(IReadOnlyList<Candle> candles, int period)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < period + 2 || period <= 0)
        {
            return 0m;
        }

        var plusDm = new List<decimal>();
        var minusDm = new List<decimal>();
        var trueRanges = new List<decimal>();
        for (var index = 1; index < ordered.Length; index++)
        {
            var current = ordered[index];
            var previous = ordered[index - 1];
            var upMove = current.High - previous.High;
            var downMove = previous.Low - current.Low;
            plusDm.Add(upMove > downMove && upMove > 0m ? upMove : 0m);
            minusDm.Add(downMove > upMove && downMove > 0m ? downMove : 0m);
            trueRanges.Add(decimal.Max(
                current.High - current.Low,
                decimal.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close))));
        }

        var dxValues = new List<decimal>();
        for (var end = period; end <= trueRanges.Count; end++)
        {
            var tr = trueRanges.Skip(end - period).Take(period).Sum();
            if (tr <= 0m)
            {
                dxValues.Add(0m);
                continue;
            }

            var plusDi = plusDm.Skip(end - period).Take(period).Sum() / tr * 100m;
            var minusDi = minusDm.Skip(end - period).Take(period).Sum() / tr * 100m;
            var denominator = plusDi + minusDi;
            dxValues.Add(denominator > 0m ? Math.Abs(plusDi - minusDi) / denominator * 100m : 0m);
        }

        return dxValues.TakeLast(Math.Min(period, dxValues.Count)).DefaultIfEmpty(0m).Average();
    }

    public static decimal VolumeSma(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count == 0 || period <= 0)
        {
            return 0m;
        }

        return candles
            .OrderBy(candle => candle.OpenTime)
            .TakeLast(Math.Min(period, candles.Count))
            .Average(candle => candle.Volume);
    }

    public static decimal DonchianHigh(IReadOnlyList<Candle> candles, int period, bool excludeCurrent = true)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (excludeCurrent && ordered.Length > 0)
        {
            ordered = ordered.SkipLast(1).ToArray();
        }

        var window = ordered.TakeLast(Math.Min(period, ordered.Length)).ToArray();
        return window.Length > 0 ? window.Max(candle => candle.High) : 0m;
    }

    public static decimal DonchianLow(IReadOnlyList<Candle> candles, int period, bool excludeCurrent = true)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (excludeCurrent && ordered.Length > 0)
        {
            ordered = ordered.SkipLast(1).ToArray();
        }

        var window = ordered.TakeLast(Math.Min(period, ordered.Length)).ToArray();
        return window.Length > 0 ? window.Min(candle => candle.Low) : 0m;
    }

    public static decimal Clamp(decimal value, decimal min, decimal max) =>
        decimal.Min(max, decimal.Max(min, value));
}
