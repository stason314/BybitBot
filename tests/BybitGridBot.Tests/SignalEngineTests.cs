using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class SignalEngineTests
{
    [Fact]
    public void Generate_DetectsEmaCross()
    {
        var candles = Enumerable.Range(0, 50)
            .Select(index => Candle(index, 2m - index * 0.001m, 100m))
            .Concat(Enumerable.Range(50, 10).Select(index => Candle(index, 1.95m + (index - 49) * 0.03m, 100m)))
            .ToArray();

        var signals = new SignalEngine().Generate(Snapshot(candles), Options());

        Assert.Contains(signals, signal => signal.Type == SignalType.EmaCross && signal.Direction == TradeSide.Buy);
    }

    [Fact]
    public void Generate_DetectsVolumeSpike()
    {
        var candles = FlatCandles(59, 2.45m, 100m).Append(Candle(60, 2.45m, 500m)).ToArray();

        var signals = new SignalEngine().Generate(Snapshot(candles), Options());

        Assert.Contains(signals, signal => signal.Type == SignalType.VolumeSpike);
    }

    [Fact]
    public void Generate_DetectsBtcRiskOff()
    {
        var snapshot = Snapshot(FlatCandles(60, 2.45m, 100m), [
            Candle(1, 100m, 100m),
            Candle(2, 96m, 100m),
            Candle(3, 95m, 100m)
        ]);

        var signals = new SignalEngine().Generate(snapshot, Options());

        Assert.Contains(signals, signal => signal.Type == SignalType.BtcRiskOff);
    }

    [Fact]
    public void Generate_DetectsSupportBounce()
    {
        var candles = FlatCandles(59, 2.45m, 100m).Append(new Candle(DateTimeOffset.UtcNow.AddMinutes(60), 2.31m, 2.42m, 2.30m, 2.40m, 100m, 0m)).ToArray();

        var signals = new SignalEngine().Generate(Snapshot(candles), Options());

        Assert.Contains(signals, signal => signal.Type == SignalType.RangeSupportBounce);
    }

    private static GridOptions Options() => new()
    {
        Symbol = "TONUSDT",
        LowerPrice = 2.30m,
        UpperPrice = 2.60m,
        Step = 0.05m,
        VolumeSmaPeriod = 20,
        VolumeSpikeMultiplier = 2m,
        BtcFilterEnabled = true,
        BtcLookbackCandles = 3,
        BtcMaxMovePercent = 2.5m
    };

    private static MarketSnapshot Snapshot(IReadOnlyList<Candle> candles, IReadOnlyList<Candle>? btcCandles = null) => new()
    {
        Symbol = "TONUSDT",
        CurrentPrice = candles[^1].Close,
        Candles = candles,
        BtcCandles = btcCandles ?? []
    };

    private static Candle[] FlatCandles(int count, decimal price, decimal volume) =>
        Enumerable.Range(0, count).Select(index => Candle(index, price, volume)).ToArray();

    private static Candle Candle(int index, decimal close, decimal volume) =>
        new(DateTimeOffset.UtcNow.AddMinutes(index), close, close + 0.01m, close - 0.01m, close, volume, 0m);
}
