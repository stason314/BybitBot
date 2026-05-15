using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class BigRedCandleGuardTests
{
    [Fact]
    public void BlocksBuyOnBigRedCandle()
    {
        var candles = PriceActionPhaseDetectorTests.RangeCandles(1.1m, 0.01m, 25, 100m).ToList();
        candles.Add(PriceActionPhaseDetectorTests.CandleAt(candles[^1].OpenTime.AddMinutes(1), 1.1m, 1.105m, 1.02m, 1.03m, 300m));

        var result = new BigRedCandleGuard().Evaluate(Options(), candles, []);

        Assert.True(result.BlocksBuy);
        Assert.True(result.CancelGridBuyOrders);
        Assert.Equal(NoTradeReason.DumpDetected, result.NoTradeReason);
    }

    [Fact]
    public void KeepsPauseForConfiguredCandles()
    {
        var candles = PriceActionPhaseDetectorTests.RangeCandles(1.1m, 0.01m, 25, 100m).ToList();
        candles.Add(PriceActionPhaseDetectorTests.CandleAt(candles[^1].OpenTime.AddMinutes(1), 1.1m, 1.105m, 1.02m, 1.03m, 300m));

        var result = new BigRedCandleGuard().Evaluate(Options(dumpPauseCandles: 7), candles, []);

        Assert.Equal(7, result.PauseCandles);
    }

    [Fact]
    public void DoesNotTriggerOnSmallCandle()
    {
        var candles = PriceActionPhaseDetectorTests.RangeCandles(1.1m, 0.01m, 30, 100m);

        var result = new BigRedCandleGuard().Evaluate(Options(), candles, []);

        Assert.False(result.IsActive);
        Assert.False(result.BlocksBuy);
    }

    private static GridOptions Options(int dumpPauseCandles = 4) =>
        PriceActionPhaseDetectorTests.Options(
            1m,
            1.2m,
            dumpPauseCandles: dumpPauseCandles,
            cancelGridBuysOnDump: true,
            gridCancelBuysOnDump: true);
}
