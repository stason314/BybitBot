using System.Globalization;
using BybitGridBot.Domain;

namespace BybitGridBot.Bybit;

public static class FuturesOrderRequestFactory
{
    public static BybitCreateOrderRequest Create(FuturesTradeIntent intent)
    {
        var side = ResolveSide(intent.Action);
        var reduceOnly = intent.IsReduceOnly;

        return new BybitCreateOrderRequest
        {
            Category = intent.Category,
            Symbol = intent.Symbol,
            Side = side.ToString(),
            OrderType = intent.OrderType.ToString(),
            Qty = FormatDecimal(intent.Quantity),
            Price = intent.OrderType == OrderType.Limit ? FormatDecimal(intent.Price) : null,
            TimeInForce = intent.OrderType == OrderType.Limit ? "GTC" : "IOC",
            IsLeverage = 0,
            ReduceOnly = reduceOnly,
            PositionIdx = intent.PositionIdx,
            TakeProfit = intent.TakeProfitPrice is { } takeProfitPrice ? FormatDecimal(takeProfitPrice) : null,
            StopLoss = intent.StopLossPrice is { } stopLossPrice ? FormatDecimal(stopLossPrice) : null,
            TakeProfitTriggerBy = intent.TakeProfitPrice is null ? null : "MarkPrice",
            StopLossTriggerBy = intent.StopLossPrice is null ? null : "MarkPrice",
            TakeProfitStopLossMode = intent.TakeProfitPrice is null && intent.StopLossPrice is null ? null : "Full",
            OrderLinkId = intent.OrderLinkId
        };
    }

    public static TradeSide ResolveSide(FuturesTradeAction action) =>
        action switch
        {
            FuturesTradeAction.OpenLong => TradeSide.Buy,
            FuturesTradeAction.CloseLong => TradeSide.Sell,
            FuturesTradeAction.OpenShort => TradeSide.Sell,
            FuturesTradeAction.CloseShort => TradeSide.Buy,
            FuturesTradeAction.ReduceOnlyClose => TradeSide.Sell,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported futures trade action.")
        };

    private static string FormatDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
