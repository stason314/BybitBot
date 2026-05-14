using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class MarketRegimeAnalyzerTests
{
    [Fact]
    public void Analyze_ReturnsLowVolatilityForTightRange()
    {
        var analyzer = new MarketRegimeAnalyzer(new MarketRegimeFilter());
        var candles = Enumerable.Range(0, 30)
            .Select(index => new Candle(
                DateTimeOffset.UnixEpoch.AddMinutes(index),
                100m,
                100.05m,
                99.95m,
                100m,
                1000m,
                100000m))
            .ToArray();

        var result = analyzer.Analyze(candles);

        Assert.Equal(MarketRegimeType.LowVolatility, result.Regime);
    }

    [Fact]
    public void Analyze_ReturnsBreakoutForVolumeMove()
    {
        var analyzer = new MarketRegimeAnalyzer(new MarketRegimeFilter());
        var candles = Enumerable.Range(0, 30)
            .Select(index =>
            {
                var close = index < 25 ? 100m : 101m;
                var volume = index < 25 ? 1000m : 4000m;
                return new Candle(
                    DateTimeOffset.UnixEpoch.AddMinutes(index),
                    100m,
                    close + 0.1m,
                    99.9m,
                    close,
                    volume,
                    volume * close);
            })
            .ToArray();

        var result = analyzer.Analyze(candles);

        Assert.Equal(MarketRegimeType.Breakout, result.Regime);
    }
}
