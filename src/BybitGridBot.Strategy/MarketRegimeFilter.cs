using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class MarketRegimeFilter
{
    public decimal CalculateAdx(IReadOnlyList<Candle> candles, int period = 14)
    {
        if (candles.Count < period + 1)
        {
            return 0m;
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var trList = new List<decimal>();
        var plusDmList = new List<decimal>();
        var minusDmList = new List<decimal>();

        for (var index = 1; index < ordered.Length; index++)
        {
            var current = ordered[index];
            var previous = ordered[index - 1];

            var upMove = current.High - previous.High;
            var downMove = previous.Low - current.Low;
            var plusDm = upMove > downMove && upMove > 0m ? upMove : 0m;
            var minusDm = downMove > upMove && downMove > 0m ? downMove : 0m;

            var tr = decimal.Max(
                current.High - current.Low,
                decimal.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));

            trList.Add(tr);
            plusDmList.Add(plusDm);
            minusDmList.Add(minusDm);
        }

        if (trList.Count < period)
        {
            return 0m;
        }

        var smoothedTr = trList.Take(period).Sum();
        var smoothedPlusDm = plusDmList.Take(period).Sum();
        var smoothedMinusDm = minusDmList.Take(period).Sum();
        var dxValues = new List<decimal>();

        for (var index = period; index <= trList.Count; index++)
        {
            if (index > period)
            {
                smoothedTr = smoothedTr - (smoothedTr / period) + trList[index - 1];
                smoothedPlusDm = smoothedPlusDm - (smoothedPlusDm / period) + plusDmList[index - 1];
                smoothedMinusDm = smoothedMinusDm - (smoothedMinusDm / period) + minusDmList[index - 1];
            }

            if (smoothedTr <= 0m)
            {
                dxValues.Add(0m);
                continue;
            }

            var plusDi = 100m * (smoothedPlusDm / smoothedTr);
            var minusDi = 100m * (smoothedMinusDm / smoothedTr);
            var denominator = plusDi + minusDi;
            var dx = denominator <= 0m ? 0m : 100m * Math.Abs(plusDi - minusDi) / denominator;
            dxValues.Add(dx);
        }

        if (dxValues.Count == 0)
        {
            return 0m;
        }

        return decimal.Round(dxValues.Average(), 4, MidpointRounding.AwayFromZero);
    }

    public bool ShouldBlockNewOrders(GridOptions options, IReadOnlyList<Candle> symbolCandles, IReadOnlyList<Candle> btcCandles)
    {
        if (options.MarketFilterEnabled)
        {
            var adx = CalculateAdx(symbolCandles);
            if (adx > options.AdxMax)
            {
                return true;
            }
        }

        if (options.BtcFilterEnabled && btcCandles.Count >= options.BtcLookbackCandles)
        {
            var slice = btcCandles.OrderBy(candle => candle.OpenTime).TakeLast(options.BtcLookbackCandles).ToArray();
            var oldest = slice[0].Open;
            var latest = slice[^1].Close;

            if (oldest > 0m)
            {
                var movePercent = Math.Abs((latest - oldest) / oldest) * 100m;
                if (movePercent > options.BtcMaxMovePercent)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
