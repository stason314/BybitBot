using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class MarketRegimeFilterTests
{
    [Fact]
    public void CalculateAdx_ReturnsHigherValueForTrendingSeries()
    {
        var filter = new MarketRegimeFilter();
        var trending = BuildCandles(i => 100m + (i * 2m));
        var ranging = BuildCandles(i => 100m + ((i % 2 == 0) ? 1m : -1m));

        var trendingAdx = filter.CalculateAdx(trending);
        var rangingAdx = filter.CalculateAdx(ranging);

        Assert.True(trendingAdx > rangingAdx);
    }

    [Fact]
    public void ShouldBlockNewOrders_WhenAdxExceedsThreshold()
    {
        var filter = new MarketRegimeFilter();
        var options = new GridOptions
        {
            MarketFilterEnabled = true,
            AdxMax = 10m,
            BtcFilterEnabled = false
        };

        var symbolCandles = BuildCandles(i => 100m + (i * 3m));
        var blocked = filter.ShouldBlockNewOrders(options, symbolCandles, []);

        Assert.True(blocked);
    }

    private static IReadOnlyList<Candle> BuildCandles(Func<int, decimal> closeSelector)
    {
        var candles = new List<Candle>();
        for (var index = 0; index < 40; index++)
        {
            var close = closeSelector(index);
            candles.Add(new Candle(
                DateTimeOffset.UtcNow.AddHours(-40 + index),
                close - 0.5m,
                close + 1m,
                close - 1m,
                close,
                1000m,
                1000m * close));
        }

        return candles;
    }
}
