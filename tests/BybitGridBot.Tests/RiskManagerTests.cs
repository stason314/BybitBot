using BybitGridBot.Domain;
using BybitGridBot.Risk;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class RiskManagerTests
{
    [Fact]
    public void ValidateOrderPlacement_ReturnsViolation_WhenDailyLossExceeded()
    {
        var manager = new RiskManager();
        var riskOptions = new RiskOptions { MaxDailyLossUsdt = 20m };
        var gridOptions = new GridOptions { LowerPrice = 2.30m, UpperPrice = 2.60m };
        var state = new BotState { DailyRealizedPnl = -25m };

        var violations = manager.ValidateOrderPlacement(
            riskOptions,
            gridOptions,
            state,
            [],
            2.40m,
            20m,
            5m,
            100m);

        Assert.Contains(violations, violation => violation.Contains("MAX_DAILY_LOSS_USDT", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateOrderPlacement_ReturnsViolation_WhenOpenOrderLimitReached()
    {
        var manager = new RiskManager();
        var riskOptions = new RiskOptions { MaxOpenOrders = 1 };
        var gridOptions = new GridOptions { LowerPrice = 2.30m, UpperPrice = 2.60m };
        var state = new BotState();
        var activeOrders = new[]
        {
            new GridOrder
            {
                OrderLinkId = "gb123",
                Symbol = "TONUSDT",
                Category = "spot",
                Side = TradeSide.Buy,
                Price = 2.35m,
                Quantity = 10m,
                Status = OrderStatus.New,
                TradingMode = TradingMode.Paper,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        var violations = manager.ValidateOrderPlacement(
            riskOptions,
            gridOptions,
            state,
            activeOrders,
            2.40m,
            20m,
            5m,
            100m);

        Assert.Contains(violations, violation => violation.Contains("MAX_OPEN_ORDERS", StringComparison.Ordinal));
    }
}
