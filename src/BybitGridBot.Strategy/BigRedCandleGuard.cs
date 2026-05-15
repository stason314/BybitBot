using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class BigRedCandleGuard
{
    public BigRedCandleGuardResult Evaluate(
        GridOptions options,
        IReadOnlyCollection<Candle> symbolCandles,
        IReadOnlyCollection<Candle> btcCandles,
        DateTimeOffset? evaluatedAt = null)
    {
        var now = evaluatedAt ?? DateTimeOffset.UtcNow;
        var candles = symbolCandles.OrderBy(candle => candle.OpenTime).ToArray();
        if (candles.Length == 0)
        {
            return BigRedCandleGuardResult.Inactive("No candles available.", now);
        }

        var last = candles[^1];
        var lastMove = PercentMove(last.Open, last.Close);
        var closeLocation = last.High > last.Low ? (last.Close - last.Low) / (last.High - last.Low) : 0.5m;
        var lookback = candles.TakeLast(Math.Max(1, options.DumpLookbackCandles)).ToArray();
        var lookbackMove = lookback.Length == 0 ? 0m : PercentMove(lookback[0].Open, lookback[^1].Close);
        var volumeWindow = candles.TakeLast(Math.Max(2, options.VolumeSmaPeriod)).ToArray();
        var volumeAverage = volumeWindow.Length > 1 ? volumeWindow.Take(volumeWindow.Length - 1).Average(candle => candle.Volume) : 0m;
        var volumeElevated = volumeAverage <= 0m || last.Volume >= volumeAverage;
        var btcRiskOff = IsBtcRiskOff(btcCandles, options.BtcLookbackCandles, options.BtcMaxMovePercent);

        var bigRed = lastMove <= -Math.Abs(options.BigRedCandlePercent) && closeLocation <= 0.35m;
        var dumpMove = lookbackMove <= -Math.Abs(options.DumpMovePercent);
        if ((bigRed || dumpMove) && (volumeElevated || btcRiskOff))
        {
            var reason = $"Big red candle guard active. Last: {lastMove:F2}%, lookback: {lookbackMove:F2}%, close location: {closeLocation:F2}.";
            return new BigRedCandleGuardResult(
                IsActive: true,
                BlocksBuy: true,
                CancelGridBuyOrders: options.CancelGridBuysOnDump || options.GridCancelBuysOnDump,
                PauseCandles: Math.Max(1, options.DumpPauseCandles),
                Reason: reason,
                EvaluatedAt: now,
                NoTradeReason: NoTradeReason.DumpDetected);
        }

        return BigRedCandleGuardResult.Inactive("No dump candle detected.", now);
    }

    private static decimal PercentMove(decimal from, decimal to) => from <= 0m ? 0m : (to - from) / from * 100m;

    private static bool IsBtcRiskOff(IReadOnlyCollection<Candle> btcCandles, int lookbackCandles, decimal maxMovePercent)
    {
        var slice = btcCandles.OrderBy(candle => candle.OpenTime).TakeLast(Math.Max(1, lookbackCandles)).ToArray();
        if (slice.Length < Math.Max(1, lookbackCandles) || slice[0].Open <= 0m)
        {
            return false;
        }

        return PercentMove(slice[0].Open, slice[^1].Close) <= -Math.Abs(maxMovePercent);
    }
}

public sealed record BigRedCandleGuardResult(
    bool IsActive,
    bool BlocksBuy,
    bool CancelGridBuyOrders,
    int PauseCandles,
    string Reason,
    DateTimeOffset EvaluatedAt,
    NoTradeReason NoTradeReason)
{
    public static BigRedCandleGuardResult Inactive(string reason, DateTimeOffset evaluatedAt)
    {
        return new BigRedCandleGuardResult(false, false, false, 0, reason, evaluatedAt, NoTradeReason.None);
    }
}
