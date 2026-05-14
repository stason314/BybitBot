using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class BtdStrategyTests
{
    [Fact]
    public void IsDipTriggered_ReturnsTrue_WhenDrawdownExceedsThreshold()
    {
        var strategy = new BtdStrategy();
        var now = DateTimeOffset.UtcNow;
        var candles = new[]
        {
            new Candle(now.AddMinutes(-2), 1m, 1m, 0.99m, 1m, 1m, 1m),
            new Candle(now.AddMinutes(-1), 1m, 1.02m, 0.98m, 0.99m, 1m, 1m)
        };

        var triggered = strategy.IsDipTriggered(
            new BtdStrategyConfig { DipPercent = 2m, DipLookbackCandles = 5 },
            0.99m,
            candles);

        Assert.True(triggered);
    }

    [Fact]
    public void CanOpenBuy_RespectsMaxActiveBuys()
    {
        var strategy = new BtdStrategy();
        var now = DateTimeOffset.UtcNow;
        var orders = new[]
        {
            new GridOrder
            {
                OrderLinkId = "gbb-test",
                Symbol = "BILLUSDT",
                Category = "spot",
                Side = TradeSide.Buy,
                Price = 0.19m,
                Quantity = 100m,
                Status = OrderStatus.New,
                TradingMode = TradingMode.Paper,
                CreatedAt = now.AddMinutes(-20),
                UpdatedAt = now.AddMinutes(-20)
            }
        };

        var canOpen = strategy.CanOpenBuy(new BtdStrategyConfig { MaxBuys = 1 }, orders, now);

        Assert.False(canOpen);
    }
}
