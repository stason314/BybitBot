using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class MarketRegimeDetectorNewTests
{
    [Fact]
    public void Detect_ReturnsRangeBound_WhenAdxLowAndPriceInsideRange()
    {
        var detector = new MarketRegimeDetector();
        var options = Options();
        var candles = FlatCandles(60, 2.45m);

        var regime = detector.Detect(Snapshot(candles, 2.45m), options);

        Assert.Equal(MarketRegime.RangeBound, regime);
    }

    [Fact]
    public void Detect_ReturnsUptrend_WhenEmaFastAboveSlowAndAdxHigh()
    {
        var detector = new MarketRegimeDetector();
        var options = Options();
        var candles = TrendingCandles(60, 2.0m, 0.015m);

        var regime = detector.Detect(Snapshot(candles, candles[^1].Close), options);

        Assert.Equal(MarketRegime.Uptrend, regime);
    }

    [Fact]
    public void Detect_ReturnsDowntrend_WhenEmaFastBelowSlowAndAdxHigh()
    {
        var detector = new MarketRegimeDetector();
        var options = Options();
        var candles = TrendingCandles(60, 3.0m, -0.015m);

        var regime = detector.Detect(Snapshot(candles, candles[^1].Close), options);

        Assert.Equal(MarketRegime.Downtrend, regime);
    }

    [Fact]
    public void Detect_ReturnsBreakoutUp_WithVolumeSpike()
    {
        var detector = new MarketRegimeDetector();
        var options = Options();
        var candles = TrendingCandles(59, 2.2m, 0.004m).Append(new Candle(DateTimeOffset.UtcNow.AddMinutes(60), 2.59m, 2.75m, 2.58m, 2.72m, 5000m, 0m)).ToArray();

        var regime = detector.Detect(Snapshot(candles, 2.72m), options);

        Assert.Equal(MarketRegime.BreakoutUp, regime);
    }

    [Fact]
    public void Detect_ReturnsHighVolatility_WithAtrSpike()
    {
        var detector = new MarketRegimeDetector();
        var options = Options();
        var candles = FlatCandles(50, 2.45m).Concat(VolatileCandles(12, 2.45m)).ToArray();

        var regime = detector.Detect(Snapshot(candles, 2.45m), options);

        Assert.Equal(MarketRegime.HighVolatility, regime);
    }

    [Fact]
    public void Detect_ReturnsUnknown_WhenNotEnoughCandles()
    {
        var detector = new MarketRegimeDetector();

        var regime = detector.Detect(Snapshot(FlatCandles(10, 2.45m), 2.45m), Options());

        Assert.Equal(MarketRegime.Unknown, regime);
    }

    private static GridOptions Options() => new()
    {
        Symbol = "TONUSDT",
        LowerPrice = 2.30m,
        UpperPrice = 2.60m,
        Step = 0.05m,
        RangeAdxMax = 20m,
        TrendAdxMin = 25m,
        BreakoutAdxMin = 25m,
        VolumeSpikeMultiplier = 2m,
        BtcFilterEnabled = false
    };

    private static MarketSnapshot Snapshot(IReadOnlyList<Candle> candles, decimal currentPrice) => new()
    {
        Symbol = "TONUSDT",
        CurrentPrice = currentPrice,
        Candles = candles
    };

    private static Candle[] FlatCandles(int count, decimal price)
    {
        return Enumerable.Range(0, count)
            .Select(index => new Candle(DateTimeOffset.UtcNow.AddMinutes(index), price, price + 0.01m, price - 0.01m, price, 100m, 0m))
            .ToArray();
    }

    private static Candle[] TrendingCandles(int count, decimal start, decimal step)
    {
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var open = start + step * index;
                var close = open + step * 0.8m;
                return new Candle(DateTimeOffset.UtcNow.AddMinutes(index), open, Math.Max(open, close) + 0.01m, Math.Min(open, close) - 0.01m, close, 100m, 0m);
            })
            .ToArray();
    }

    private static Candle[] VolatileCandles(int count, decimal price)
    {
        return Enumerable.Range(0, count)
            .Select(index => new Candle(DateTimeOffset.UtcNow.AddMinutes(100 + index), price, price + 0.30m, price - 0.30m, price, 100m, 0m))
            .ToArray();
    }
}
