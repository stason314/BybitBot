using BybitGridBot.Domain;
using BybitGridBot.Strategy;
using System.Text.Json;

namespace BybitGridBot.Tests;

public sealed class DcaStrategyTests
{
    [Fact]
    public void IsDueForEntry_ReturnsTrue_WhenNoPriorBuysExist()
    {
        var strategy = new DcaStrategy();

        var isDue = strategy.IsDueForEntry(new DcaStrategyConfig(), [], DateTimeOffset.UtcNow);

        Assert.True(isDue);
    }

    [Fact]
    public void IsDueForEntry_RespectsBuyInterval()
    {
        var strategy = new DcaStrategy();
        var now = DateTimeOffset.UtcNow;
        var orders = new[]
        {
            new GridOrder
            {
                OrderLinkId = "gbb-test",
                Symbol = "TONUSDT",
                Category = "spot",
                Side = TradeSide.Buy,
                Price = 2.10m,
                Quantity = 10m,
                Status = OrderStatus.New,
                TradingMode = TradingMode.Paper,
                CreatedAt = now.AddMinutes(-10),
                UpdatedAt = now.AddMinutes(-10)
            }
        };

        var isDue = strategy.IsDueForEntry(new DcaStrategyConfig { BuyIntervalMinutes = 30 }, orders, now);

        Assert.False(isDue);
    }

    [Fact]
    public void CalculateTakeProfitPrice_UsesConfiguredPercent()
    {
        var strategy = new DcaStrategy();

        var price = strategy.CalculateTakeProfitPrice(2.10m, new DcaStrategyConfig { TakeProfitPercent = 1.5m });

        Assert.Equal(2.1315m, price);
    }

    [Fact]
    public void ComboStrategyConfig_DeserializesDcaTrigger()
    {
        var config = JsonSerializer.Deserialize<ComboStrategyConfig>(
            """
            {
              "orderSizeUsdt": 20,
              "buyIntervalMinutes": 30,
              "takeProfitPercent": 1,
              "dcaBelowPrice": 2.08
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(config);
        Assert.Equal(20m, config.OrderSizeUsdt);
        Assert.Equal(2.08m, config.DcaBelowPrice);
    }
}
