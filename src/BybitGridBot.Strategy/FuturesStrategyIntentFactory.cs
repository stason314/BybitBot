using System.Text.Json;
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
        var notional = ResolveEntryNotional(settings);
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

    private static decimal ResolveEntryNotional(FuturesBotSettings settings)
    {
        var fallbackMultiplier = settings.AggressiveModeEnabled
            ? 0.25m * decimal.Max(0.01m, settings.AggressiveEntryMultiplier)
            : 0.25m;
        var fallback = settings.MaxNotionalUsdt * fallbackMultiplier;
        if (string.IsNullOrWhiteSpace(settings.StrategyConfigJson))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(settings.StrategyConfigJson);
            if (document.RootElement.TryGetProperty("entryNotionalUsdt", out var property) &&
                property.ValueKind == JsonValueKind.Number &&
                property.TryGetDecimal(out var configured) &&
                configured > 0m)
            {
                var multiplier = settings.AggressiveModeEnabled
                    ? decimal.Max(0.01m, settings.AggressiveEntryMultiplier)
                    : 1m;
                return decimal.Min(settings.MaxNotionalUsdt, configured * multiplier);
            }
        }
        catch (JsonException)
        {
            return fallback;
        }

        return fallback;
    }
}
