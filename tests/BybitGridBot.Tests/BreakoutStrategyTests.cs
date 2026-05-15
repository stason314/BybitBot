using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class BreakoutStrategyTests
{
    [Fact]
    public void RequiresConfirmationCandles()
    {
        var candles = PriceActionPhaseDetectorTests.RangeCandles(1.1m, 0.01m, 60, 100m).ToList();
        candles.Add(PriceActionPhaseDetectorTests.CandleAt(candles[^1].OpenTime.AddMinutes(1), 1.2m, 1.23m, 1.19m, 1.22m, 250m));

        var confirmed = new BreakoutStrategy().HasConfirmation(candles, 1.2m, confirmationCandles: 2);

        Assert.False(confirmed);
    }

    [Fact]
    public void CreatesStopLossBelowBreakoutLevel()
    {
        var stop = new BreakoutStrategy().CalculateStopLoss(new GridOptions { BreakoutAtrStopMultiplier = 1.5m }, 1.2m, 0.02m);

        Assert.Equal(1.17m, stop);
    }
}
