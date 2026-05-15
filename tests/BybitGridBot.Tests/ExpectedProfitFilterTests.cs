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

    [Fact]
    public void BlocksTradeWhenExpectedProfitEqualsThreshold()
    {
        var options = new GridOptions
        {
            FeePercent = 0.1m,
            SlippagePercent = 0.05m,
            MinExpectedProfitPercent = 0.7m
        };

        var decision = new ExpectedProfitFilter().Evaluate(options, TradeSide.Sell, 0.95m, "Grid");

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void CalculatesLongRoundTripPercentFromEntryAndExit()
    {
        var decision = new ExpectedProfitFilter().EvaluateLongRoundTrip(
            new GridOptions
            {
                FeePercent = 0.1m,
                SlippagePercent = 0.05m,
                MinExpectedProfitPercent = 0.7m
            },
            TradeSide.Buy,
            100m,
            102m,
            "Signal");

        Assert.True(decision.IsAllowed);
        Assert.Equal(2m, decision.ExpectedProfitPercent);
    }
}
