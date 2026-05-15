using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class PriceActionPhaseDetectorTests
{
    [Fact]
    public void DetectsUptrend()
    {
        var options = Options(lower: 1.0m, upper: 2.0m);
        var candles = TrendCandles(1m, 0.012m, 80, volume: 100m);

        var result = new PriceActionPhaseDetector().Detect(options, candles[^1].Close, candles, []);

        Assert.Equal(MarketPhase.Uptrend, result.Phase);
        Assert.Equal(StrategyType.TrendFollowing, result.SuggestedStrategy);
    }

    [Fact]
    public void DetectsPullbackInUptrend()
    {
        var options = Options(lower: 1.0m, upper: 2.0m, btdMinPullbackPercent: 0.2m, btdMaxDistanceFromEmaPercent: 5m);
        var candles = TrendCandles(1m, 0.012m, 75, volume: 100m).ToList();
        var last = candles[^1];
        candles[^1] = last with { Open = last.Close + 0.01m, High = last.Close + 0.015m, Low = last.Close - 0.035m, Close = last.Close - 0.025m };

        var result = new PriceActionPhaseDetector().Detect(options, candles[^1].Close, candles, []);

        Assert.Equal(MarketPhase.PullbackInUptrend, result.Phase);
        Assert.Equal(StrategyType.Btd, result.SuggestedStrategy);
    }

    [Fact]
    public void DetectsRangeBound()
    {
        var options = Options(lower: 0.98m, upper: 1.04m, rangeAdxMax: 25m);
        var candles = RangeCandles(1m, 0.015m, 90, volume: 100m);

        var result = new PriceActionPhaseDetector().Detect(options, candles[^1].Close, candles, []);

        Assert.Equal(MarketPhase.RangeBound, result.Phase);
        Assert.Equal(StrategyType.Grid, result.SuggestedStrategy);
    }

    [Fact]
    public void DetectsBreakoutUp()
    {
        var options = Options(
            lower: 1m,
            upper: 1.2m,
            breakoutAdxMin: 10m,
            breakoutConfirmationCandles: 2,
            breakoutMaxDistanceFromEmaPercent: 100m,
            breakoutVolumeMultiplier: 1.5m);
        var candles = RangeCandles(1.1m, 0.01m, 70, volume: 100m).ToList();
        candles.Add(CandleAt(candles[^1].OpenTime.AddMinutes(1), 1.205m, 1.235m, 1.202m, 1.225m, 250m));
        candles.Add(CandleAt(candles[^1].OpenTime.AddMinutes(1), 1.225m, 1.25m, 1.22m, 1.24m, 260m));

        var result = new PriceActionPhaseDetector().Detect(options, candles[^1].Close, candles, []);

        Assert.Equal(MarketPhase.BreakoutUp, result.Phase);
        Assert.Equal(StrategyType.Breakout, result.SuggestedStrategy);
    }

    [Fact]
    public void DetectsBreakoutDown()
    {
        var options = Options(
            lower: 1m,
            upper: 1.2m,
            breakoutAdxMin: 10m,
            breakoutConfirmationCandles: 2,
            breakoutVolumeMultiplier: 1.5m,
            bigRedCandlePercent: 20m,
            dumpMovePercent: 30m);
        var candles = RangeCandles(1.1m, 0.01m, 70, volume: 100m).ToList();
        candles.Add(CandleAt(candles[^1].OpenTime.AddMinutes(1), 0.995m, 1.0m, 0.965m, 0.975m, 250m));
        candles.Add(CandleAt(candles[^1].OpenTime.AddMinutes(1), 0.975m, 0.98m, 0.94m, 0.95m, 260m));

        var result = new PriceActionPhaseDetector().Detect(options, candles[^1].Close, candles, []);

        Assert.Equal(MarketPhase.BreakoutDown, result.Phase);
        Assert.Equal(StrategyType.Pause, result.SuggestedStrategy);
    }

    [Fact]
    public void DetectsDumpOnBigRedCandle()
    {
        var options = Options(lower: 1m, upper: 1.2m);
        var candles = RangeCandles(1.1m, 0.01m, 70, volume: 100m).ToList();
        candles.Add(CandleAt(candles[^1].OpenTime.AddMinutes(1), 1.1m, 1.105m, 1.02m, 1.03m, 300m));

        var result = new PriceActionPhaseDetector().Detect(options, candles[^1].Close, candles, []);

        Assert.Equal(MarketPhase.Dump, result.Phase);
        Assert.Equal(StrategyType.Pause, result.SuggestedStrategy);
    }

    [Fact]
    public void DetectsHighVolatilityOnAtrSpike()
    {
        var options = Options(lower: 1m, upper: 1.2m, atrSpikeMultiplier: 1.5m);
        var candles = RangeCandles(1.1m, 0.003m, 70, volume: 100m).ToList();
        for (var i = 0; i < 15; i++)
        {
            var close = 1.1m + (i % 2 == 0 ? 0.045m : -0.045m);
            candles.Add(CandleAt(candles[^1].OpenTime.AddMinutes(1), 1.1m, 1.16m, 1.04m, close, 100m));
        }

        var result = new PriceActionPhaseDetector().Detect(options, candles[^1].Close, candles, []);

        Assert.Equal(MarketPhase.HighVolatility, result.Phase);
    }

    [Fact]
    public void ReturnsUnknownWhenNotEnoughCandles()
    {
        var result = new PriceActionPhaseDetector().Detect(Options(1m, 2m), 1m, RangeCandles(1m, 0.01m, 5, 100m), []);

        Assert.Equal(MarketPhase.Unknown, result.Phase);
    }

    internal static GridOptions Options(
        decimal lower,
        decimal upper,
        decimal? rangeAdxMax = null,
        decimal? breakoutAdxMin = null,
        int? breakoutConfirmationCandles = null,
        decimal? breakoutMaxDistanceFromEmaPercent = null,
        decimal? breakoutVolumeMultiplier = null,
        decimal? btdMinPullbackPercent = null,
        decimal? btdMaxDistanceFromEmaPercent = null,
        decimal? trendMaxDistanceFromEmaPercent = null,
        decimal? atrSpikeMultiplier = null,
        decimal? bigRedCandlePercent = null,
        decimal? dumpMovePercent = null,
        int? dumpPauseCandles = null,
        bool cancelGridBuysOnDump = true,
        bool gridCancelBuysOnDump = true) => new()
    {
        LowerPrice = lower,
        UpperPrice = upper,
        Step = 0.01m,
        RangeAdxMax = rangeAdxMax ?? 20m,
        TrendAdxMin = 15m,
        BreakoutAdxMin = breakoutAdxMin ?? 15m,
        StrategyMinScore = 65m,
        MinStrategyScore = 65m,
        StrategyMinConfidence = 0.6m,
        MinPhaseConfidence = 0.6m,
        AdxPeriod = 14,
        AtrPeriod = 14,
        EmaFast = 20,
        EmaSlow = 50,
        TrendEmaFast = 20,
        TrendEmaSlow = 50,
        VolumeSmaPeriod = 20,
        AtrSpikeMultiplier = atrSpikeMultiplier ?? 2m,
        BigRedCandlePercent = bigRedCandlePercent ?? 4m,
        DumpMovePercent = dumpMovePercent ?? 6m,
        DumpLookbackCandles = 3,
        DumpPauseCandles = dumpPauseCandles ?? 4,
        CancelGridBuysOnDump = cancelGridBuysOnDump,
        GridCancelBuysOnDump = gridCancelBuysOnDump,
        BtdMaxDistanceFromEmaPercent = btdMaxDistanceFromEmaPercent ?? 3m,
        BtdMinPullbackPercent = btdMinPullbackPercent ?? 2m,
        BreakoutConfirmationCandles = breakoutConfirmationCandles ?? 2,
        BreakoutVolumeMultiplier = breakoutVolumeMultiplier ?? 1.8m,
        BreakoutMaxDistanceFromEmaPercent = breakoutMaxDistanceFromEmaPercent ?? 5m,
        TrendMaxDistanceFromEmaPercent = trendMaxDistanceFromEmaPercent ?? 4m,
        BtcFilterEnabled = false
    };

    internal static Candle[] TrendCandles(decimal start, decimal increment, int count, decimal volume)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-count);
        var candles = new Candle[count];
        var price = start;
        for (var i = 0; i < count; i++)
        {
            var open = price;
            price += increment;
            candles[i] = CandleAt(now.AddMinutes(i), open, price + 0.008m, open - 0.006m, price, volume);
        }

        return candles;
    }

    internal static Candle[] RangeCandles(decimal center, decimal amplitude, int count, decimal volume)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-count);
        var candles = new Candle[count];
        for (var i = 0; i < count; i++)
        {
            var open = center + (i % 2 == 0 ? -amplitude / 2m : amplitude / 2m);
            var close = center + (i % 2 == 0 ? amplitude / 2m : -amplitude / 2m);
            candles[i] = CandleAt(now.AddMinutes(i), open, center + amplitude, center - amplitude, close, volume);
        }

        return candles;
    }

    internal static Candle CandleAt(DateTimeOffset time, decimal open, decimal high, decimal low, decimal close, decimal volume)
    {
        return new Candle(time, open, high, low, close, volume, volume * close);
    }
}
