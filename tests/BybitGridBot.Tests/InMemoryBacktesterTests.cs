using BybitGridBot.Domain;
using BybitGridBot.Strategy;
using BybitGridBot.Strategy.Backtesting;

namespace BybitGridBot.Tests;

public sealed class InMemoryBacktesterTests
{
    [Fact]
    public void Run_ReturnsBasicMetrics()
    {
        var candles = Enumerable.Range(0, 80)
            .Select(index => new Candle(DateTimeOffset.UtcNow.AddMinutes(index), 2.40m, 2.50m, 2.35m, 2.45m, 100m, 0m))
            .ToArray();

        var result = new InMemoryBacktester().Run(candles, new GridOptions { Symbol = "TONUSDT", LowerPrice = 2.30m, UpperPrice = 2.60m, Step = 0.05m });

        Assert.True(result.StrategyUsagePercent.Count > 0);
        Assert.True(result.TotalTrades >= 0);
    }
}
