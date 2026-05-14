using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class SignalAnalyzerTests
{
    [Fact]
    public void Analyze_ReturnsAvoidWhenCandlesAreInsufficient()
    {
        var analyzer = new SignalAnalyzer();
        var candles = Enumerable.Range(0, 10)
            .Select(index => BuildCandle(index, 100m, 1000m))
            .ToArray();

        var result = analyzer.Analyze(candles);

        Assert.Equal(SignalType.Avoid, result.Signal);
        Assert.True(result.Confidence <= 0.1m);
    }

    [Fact]
    public void Analyze_ReturnsBuyForBullishTrendWithNeutralRsi()
    {
        var analyzer = new SignalAnalyzer();
        var closes = Enumerable.Range(0, 45)
            .Select(index => 100m + index * 0.03m + (index % 2 == 0 ? -0.08m : 0.08m))
            .ToArray();
        var candles = closes
            .Select((close, index) => BuildCandle(index, close, 1000m))
            .ToArray();

        var result = analyzer.Analyze(candles);

        Assert.Equal(SignalType.Buy, result.Signal);
        Assert.True(result.EmaFast > result.EmaSlow);
        Assert.True(result.Confidence >= 0.5m);
    }

    [Fact]
    public void Analyze_ReturnsSellForOverboughtUpperBand()
    {
        var analyzer = new SignalAnalyzer();
        var closes = Enumerable.Range(0, 45)
            .Select(index => index < 35 ? 100m : 100m + (index - 34) * 0.5m)
            .ToArray();
        var candles = closes
            .Select((close, index) => BuildCandle(index, close, index >= 40 ? 2500m : 1000m))
            .ToArray();

        var result = analyzer.Analyze(candles);

        Assert.Equal(SignalType.Sell, result.Signal);
        Assert.True(result.Rsi >= 70m);
        Assert.True(result.BollingerPosition >= 0.75m);
    }

    private static Candle BuildCandle(int index, decimal close, decimal volume)
    {
        return new Candle(
            DateTimeOffset.UnixEpoch.AddMinutes(index),
            close,
            close + 0.05m,
            close - 0.05m,
            close,
            volume,
            volume * close);
    }
}
