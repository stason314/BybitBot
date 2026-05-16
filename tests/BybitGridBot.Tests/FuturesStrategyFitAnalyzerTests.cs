using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class FuturesStrategyFitAnalyzerTests
{
    [Fact]
    public void Analyze_PrefersGridForMeanRevertingRange()
    {
        var result = new FuturesStrategyFitAnalyzer().Analyze(MeanRevertingCandles());

        Assert.True(result.GridLongOnlyScore >= result.TrendFollowScore);
        Assert.True(result.GridShortOnlyScore >= result.TrendFollowShortOnlyScore);
        Assert.Contains(result.Reasons, reason => reason.StartsWith("mean crosses", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_PrefersTrendShortForOrderlyDowntrend()
    {
        var result = new FuturesStrategyFitAnalyzer().Analyze(TrendCandles(isDowntrend: true));

        Assert.Equal(FuturesStrategyType.TrendFollowShortOnly, result.BestStrategyType);
        Assert.True(result.TrendFollowShortOnlyScore > result.GridShortOnlyScore);
    }

    [Fact]
    public void Analyze_PrefersTrendLongForOrderlyUptrend()
    {
        var result = new FuturesStrategyFitAnalyzer().Analyze(TrendCandles(isDowntrend: false));

        Assert.Equal(FuturesStrategyType.TrendFollow, result.BestStrategyType);
        Assert.True(result.TrendFollowScore > result.GridLongOnlyScore);
    }

    private static IReadOnlyList<Candle> MeanRevertingCandles()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-360);
        return Enumerable.Range(0, 72)
            .Select(index =>
            {
                var offset = index % 4 switch
                {
                    0 => -0.8m,
                    1 => 0.7m,
                    2 => -0.6m,
                    _ => 0.8m
                };
                var open = 100m - offset / 2m;
                var close = 100m + offset;
                return new Candle(start.AddMinutes(index * 5), open, decimal.Max(open, close) + 0.4m, decimal.Min(open, close) - 0.4m, close, 1m, close);
            })
            .ToArray();
    }

    private static IReadOnlyList<Candle> TrendCandles(bool isDowntrend)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-360);
        return Enumerable.Range(0, 72)
            .Select(index =>
            {
                var basePrice = isDowntrend ? 110m - index * 0.08m : 100m + index * 0.08m;
                var open = isDowntrend ? basePrice + 0.03m : basePrice - 0.03m;
                var close = isDowntrend ? basePrice - 0.04m : basePrice + 0.04m;
                return new Candle(start.AddMinutes(index * 5), open, decimal.Max(open, close) + 0.1m, decimal.Min(open, close) - 0.1m, close, 1m, close);
            })
            .ToArray();
    }
}
