using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class FuturesPaperSimulatorTests
{
    [Fact]
    public void Simulate_OpenLong_AppliesMarginFeeFundingAndUnrealizedPnl()
    {
        var simulator = new FuturesPaperSimulator(new FuturesAccounting());

        var result = simulator.Simulate(new FuturesPaperSimulationRequest
        {
            Position = new FuturesPositionSnapshot
            {
                Symbol = "BTCUSDT",
                Category = "linear"
            },
            Intent = new FuturesTradeIntent
            {
                Symbol = "BTCUSDT",
                Category = "linear",
                Action = FuturesTradeAction.OpenLong,
                Price = 50000m,
                Quantity = 0.01m,
                Leverage = 2m,
                LiquidationPrice = 25000m
            },
            MarkPrice = 51000m,
            FeeRatePercent = 0.06m,
            FundingCostUsdt = 0.2m
        });

        Assert.False(result.IsLiquidated);
        Assert.Equal(0.3m, result.FeePaid);
        Assert.Equal(0.2m, result.FundingPaid);
        Assert.Equal(10m, result.Position.UnrealizedPnl);
        Assert.Equal(255m, result.Position.MarginUsedUsdt);
        Assert.Equal(-0.3m, result.Position.RealizedPnl);
        Assert.Equal(-0.2m, result.Position.Funding);
    }

    [Fact]
    public void Simulate_MarkAtLiquidationPrice_ClosesPosition()
    {
        var simulator = new FuturesPaperSimulator(new FuturesAccounting());

        var result = simulator.Simulate(new FuturesPaperSimulationRequest
        {
            Position = new FuturesPositionSnapshot
            {
                Symbol = "BTCUSDT",
                Category = "linear",
                Side = "Buy",
                Size = 0.01m,
                EntryPrice = 50000m,
                MarkPrice = 50000m,
                LiquidationPrice = 25000m,
                PositionValueUsdt = 500m,
                MarginUsedUsdt = 250m,
                Leverage = 2m
            },
            MarkPrice = 25000m,
            FeeRatePercent = 0.06m
        });

        Assert.True(result.IsLiquidated);
        Assert.Equal("None", result.Position.Side);
        Assert.Equal(0m, result.Position.Size);
        Assert.True(result.Position.RealizedPnl < 0m);
    }
}
