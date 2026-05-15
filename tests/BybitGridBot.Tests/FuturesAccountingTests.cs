using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class FuturesAccountingTests
{
    [Fact]
    public void ApplyFill_OpensLongAndMarksUnrealizedPnl()
    {
        var accounting = new FuturesAccounting();
        var position = new FuturesPositionSnapshot
        {
            Symbol = "BTCUSDT",
            Category = "linear"
        };

        var result = accounting.ApplyFill(position, new FuturesFill
        {
            Action = FuturesTradeAction.OpenLong,
            Quantity = 0.01m,
            Price = 50000m,
            MarkPrice = 51000m,
            Fee = 0.3m,
            Leverage = 2m
        });

        Assert.Equal("Buy", result.Side);
        Assert.Equal(0.01m, result.Size);
        Assert.Equal(50000m, result.EntryPrice);
        Assert.Equal(10m, result.UnrealizedPnl);
        Assert.Equal(-0.3m, result.RealizedPnl);
        Assert.Equal(255m, result.MarginUsedUsdt);
    }

    [Fact]
    public void ApplyFill_ClosesLongAndRealizesPnl()
    {
        var accounting = new FuturesAccounting();
        var position = new FuturesPositionSnapshot
        {
            Symbol = "BTCUSDT",
            Category = "linear",
            Side = "Buy",
            Size = 0.01m,
            EntryPrice = 50000m,
            MarkPrice = 50000m,
            Leverage = 2m
        };

        var result = accounting.ApplyFill(position, new FuturesFill
        {
            Action = FuturesTradeAction.CloseLong,
            Quantity = 0.01m,
            Price = 51000m,
            MarkPrice = 51000m,
            Fee = 0.3m,
            Leverage = 2m
        });

        Assert.Equal("None", result.Side);
        Assert.Equal(0m, result.Size);
        Assert.Equal(0m, result.EntryPrice);
        Assert.Equal(9.7m, result.RealizedPnl);
        Assert.Equal(0m, result.UnrealizedPnl);
    }

    [Fact]
    public void ApplyFill_RejectsOppositeOpenBeforeClose()
    {
        var accounting = new FuturesAccounting();
        var position = new FuturesPositionSnapshot
        {
            Symbol = "BTCUSDT",
            Category = "linear",
            Side = "Buy",
            Size = 0.01m,
            EntryPrice = 50000m,
            MarkPrice = 50000m,
            Leverage = 2m
        };

        Assert.Throws<InvalidOperationException>(() => accounting.ApplyFill(position, new FuturesFill
        {
            Action = FuturesTradeAction.OpenShort,
            Quantity = 0.01m,
            Price = 50000m,
            MarkPrice = 50000m,
            Leverage = 2m
        }));
    }
}
