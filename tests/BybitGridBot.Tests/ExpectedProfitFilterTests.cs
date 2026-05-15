using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class ExpectedProfitFilterTests
{
    [Fact]
    public void BlocksTradeWhenExpectedProfitBelowFees()
    {
        var options = new GridOptions
        {
            FeePercent = 0.1m,
            SlippagePercent = 0.05m,
            MinExpectedProfitPercent = 0.7m
        };

        var decision = new ExpectedProfitFilter().Evaluate(options, TradeSide.Buy, 0.5m, "Grid");

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void AllowsTradeWhenExpectedProfitAboveThreshold()
    {
        var options = new GridOptions
        {
            FeePercent = 0.1m,
            SlippagePercent = 0.05m,
            MinExpectedProfitPercent = 0.7m
        };

        var decision = new ExpectedProfitFilter().Evaluate(options, TradeSide.Buy, 1.2m, "Breakout");

        Assert.True(decision.IsAllowed);
    }
}
