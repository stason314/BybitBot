using BybitGridBot.Bybit;
using BybitGridBot.Domain;

namespace BybitGridBot.Tests;

public sealed class FuturesOrderRequestFactoryTests
{
    [Fact]
    public void Create_MapsOpenLongWithoutReduceOnly()
    {
        var request = FuturesOrderRequestFactory.Create(Intent(FuturesTradeAction.OpenLong));

        Assert.Equal("Buy", request.Side);
        Assert.False(request.ReduceOnly.GetValueOrDefault());
        Assert.Equal(0, request.PositionIdx);
    }

    [Fact]
    public void Create_MapsCloseLongWithReduceOnly()
    {
        var request = FuturesOrderRequestFactory.Create(Intent(FuturesTradeAction.CloseLong));

        Assert.Equal("Sell", request.Side);
        Assert.True(request.ReduceOnly.GetValueOrDefault());
        Assert.Equal(0, request.PositionIdx);
    }

    [Fact]
    public void Create_MapsStopLossAndTakeProfit()
    {
        var request = FuturesOrderRequestFactory.Create(Intent(
            FuturesTradeAction.OpenLong,
            stopLossPrice: 49000m,
            takeProfitPrice: 52000m));

        Assert.Equal("49000", request.StopLoss);
        Assert.Equal("52000", request.TakeProfit);
        Assert.Equal("MarkPrice", request.StopLossTriggerBy);
        Assert.Equal("MarkPrice", request.TakeProfitTriggerBy);
        Assert.Equal("Full", request.TakeProfitStopLossMode);
    }

    private static FuturesTradeIntent Intent(
        FuturesTradeAction action,
        decimal? stopLossPrice = null,
        decimal? takeProfitPrice = null) => new()
    {
        Symbol = "BTCUSDT",
        Category = "linear",
        Action = action,
        Price = 50000m,
        Quantity = 0.01m,
        Leverage = 2m,
        StopLossPrice = stopLossPrice,
        TakeProfitPrice = takeProfitPrice,
        PositionIdx = 0,
        OrderLinkId = "futures-test"
    };
}
