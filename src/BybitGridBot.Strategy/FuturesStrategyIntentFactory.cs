using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

internal static class FuturesStrategyIntentFactory
{
    public static FuturesTradeIntent OpenLong(
        FuturesBotSettings settings,
        decimal currentPrice,
        FuturesInstrumentRules instrument)
    {
        var price = instrument.RoundPrice(currentPrice);
        var notional = settings.MaxNotionalUsdt * 0.25m;
        var quantity = instrument.RoundQuantity(notional / price);
        var stopLossPrice = instrument.RoundPrice(price * (1m - settings.StopLossPercent / 100m));
        var takeProfitPrice = instrument.RoundPrice(price * (1m + settings.TakeProfitPercent / 100m));
        return new FuturesTradeIntent
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            Action = FuturesTradeAction.OpenLong,
            Price = price,
            Quantity = quantity,
            Leverage = settings.Leverage,
            StopLossPrice = stopLossPrice,
            TakeProfitPrice = takeProfitPrice,
            LiquidationPrice = EstimateLongLiquidationPrice(price, settings.Leverage),
            PositionIdx = 0,
            OrderLinkId = FuturesOrderLinkIds.Create(FuturesTradeAction.OpenLong),
            Reason = "futures-long-entry"
        };
    }

    public static FuturesTradeIntent CloseLong(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        decimal currentPrice,
        FuturesInstrumentRules instrument,
        string reason) => new()
    {
        Symbol = settings.Symbol,
        Category = settings.Category,
        Action = FuturesTradeAction.CloseLong,
        Price = instrument.RoundPrice(currentPrice),
        Quantity = instrument.RoundQuantity(position.Size),
        Leverage = settings.Leverage,
        PositionIdx = 0,
        OrderLinkId = FuturesOrderLinkIds.Create(FuturesTradeAction.CloseLong),
        Reason = reason
    };

    private static decimal EstimateLongLiquidationPrice(decimal entryPrice, decimal leverage) =>
        leverage > 0m ? decimal.Max(0m, entryPrice * (1m - (1m / leverage))) : 0m;
}
