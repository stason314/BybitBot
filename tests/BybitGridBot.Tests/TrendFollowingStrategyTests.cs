using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class TrendFollowingStrategyTests
{
    [Fact]
    public void EntersOnlyInUptrend()
    {
        var options = PriceActionPhaseDetectorTests.Options(1m, 2m, trendMaxDistanceFromEmaPercent: 100m);
        var candles = PriceActionPhaseDetectorTests.TrendCandles(1m, 0.01m, 80, 100m);
        var strategy = new TrendFollowingStrategy();

        var canEnterUptrend = strategy.CanEnter(options, Phase(MarketPhase.Uptrend), candles, candles[^1].Close, []);
        var canEnterDump = strategy.CanEnter(options, Phase(MarketPhase.Dump), candles, candles[^1].Close, []);

        Assert.True(canEnterUptrend);
        Assert.False(canEnterDump);
    }

    private static MarketPhaseResult Phase(MarketPhase phase) => new()
    {
        Phase = phase,
        Score = 80m,
        Confidence = 0.8m,
        Reason = phase.ToString()
    };
}
