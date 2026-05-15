using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class SignalEngine
{
    public IReadOnlyList<Signal> Generate(MarketSnapshot snapshot, GridOptions options)
    {
        var candles = snapshot.Candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (candles.Length < 55)
        {
            return
            [
                new Signal
                {
                    Type = SignalType.Avoid,
                    Strength = 10m,
                    Confidence = 0.1m,
                    Reason = "Not enough candles for SignalEngine."
                }
            ];
        }

        var result = new List<Signal>();
        var emaFast = MarketRegimeDetector.CalculateEma(candles, options.TrendEmaFast);
        var emaSlow = MarketRegimeDetector.CalculateEma(candles, options.TrendEmaSlow);
        var previousFast = MarketRegimeDetector.CalculateEma(candles.Take(candles.Length - 1).ToArray(), options.TrendEmaFast);
        var previousSlow = MarketRegimeDetector.CalculateEma(candles.Take(candles.Length - 1).ToArray(), options.TrendEmaSlow);
        var volumeSma = candles.TakeLast(options.VolumeSmaPeriod).Average(candle => candle.Volume);
        var last = candles[^1];

        var bullishEmaStrength = last.Close > 0m ? (emaFast - emaSlow) / last.Close * 100m : 0m;
        if (emaFast > emaSlow && (previousFast <= previousSlow || bullishEmaStrength > 0.5m))
        {
            result.Add(Build(SignalType.EmaCross, TradeSide.Buy, 75m, 0.7m, "EMA20 crossed above EMA50."));
        }
        else if (emaFast < emaSlow && (previousFast >= previousSlow || bullishEmaStrength < -0.5m))
        {
            result.Add(Build(SignalType.EmaCross, TradeSide.Sell, 75m, 0.7m, "EMA20 crossed below EMA50."));
        }

        if (volumeSma > 0m && last.Volume > volumeSma * options.VolumeSpikeMultiplier)
        {
            result.Add(Build(SignalType.VolumeSpike, null, 70m, 0.7m, "Volume is above SMA threshold."));
        }

        if (options.BtcFilterEnabled && HasBtcRiskOff(snapshot.BtcCandles, options.BtcLookbackCandles, options.BtcMaxMovePercent))
        {
            result.Add(Build(SignalType.BtcRiskOff, TradeSide.Sell, 85m, 0.85m, "BTC move exceeds risk-off threshold."));
        }

        if (last.Low <= options.LowerPrice + options.Step && last.Close > last.Open)
        {
            result.Add(Build(SignalType.RangeSupportBounce, TradeSide.Buy, 65m, 0.6m, "Price bounced near range support."));
        }

        if (last.High >= options.UpperPrice - options.Step && last.Close < last.Open)
        {
            result.Add(Build(SignalType.RangeResistanceReject, TradeSide.Sell, 65m, 0.6m, "Price rejected near range resistance."));
        }

        if (result.Count == 0)
        {
            result.Add(Build(SignalType.Hold, null, 40m, 0.45m, "No actionable signal."));
        }

        return result;
    }

    private static Signal Build(SignalType type, TradeSide? direction, decimal strength, decimal confidence, string reason) => new()
    {
        Type = type,
        Direction = direction,
        Strength = strength,
        Confidence = confidence,
        Reason = reason
    };

    private static bool HasBtcRiskOff(IReadOnlyList<Candle> btcCandles, int lookbackCandles, decimal maxMovePercent)
    {
        var slice = btcCandles.OrderBy(candle => candle.OpenTime).TakeLast(Math.Max(1, lookbackCandles)).ToArray();
        if (slice.Length < Math.Max(1, lookbackCandles))
        {
            return false;
        }

        var open = slice[0].Open;
        return open > 0m && Math.Abs((slice[^1].Close - open) / open * 100m) > maxMovePercent;
    }
}
