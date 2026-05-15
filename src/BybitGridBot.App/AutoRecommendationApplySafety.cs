using BybitGridBot.Domain;
using BybitGridBot.Risk;
using BybitGridBot.Strategy;

namespace BybitGridBot.App;

internal static class AutoRecommendationApplySafety
{
    public static IReadOnlyList<string> Validate(
        GridBotSettings currentSettings,
        BotState? state,
        IReadOnlyCollection<GridOrder> activeOrders,
        AutoConfigRecommendation recommendation,
        GridBotSettings recommendedSettings,
        RiskOptions riskOptions,
        IGridTradingStrategy strategy)
    {
        var errors = new List<string>();

        if (!HasMaterialChange(currentSettings, recommendation))
        {
            errors.Add("Recommended settings are too close to the current runtime settings. Keeping the current config avoids needless order churn.");
        }

        var lastPrice = recommendation.Metrics.LastPrice > 0m
            ? recommendation.Metrics.LastPrice
            : state?.LastObservedPrice ?? 0m;
        if (state is not null && lastPrice > 0m && StrategyCanOpenBuyOrders(recommendation.StrategyType))
        {
            var currentPositionUsdt = state.BaseAssetQuantity * lastPrice;
            var remainingPositionCapacity = riskOptions.MaxPositionUsdt - currentPositionUsdt;
            if (remainingPositionCapacity < recommendation.OrderSizeUsdt)
            {
                errors.Add($"Recommendation could expand the position above MAX_POSITION_USDT. Position: {currentPositionUsdt:0.########} USDT, remaining capacity: {remainingPositionCapacity:0.########} USDT, recommended order: {recommendation.OrderSizeUsdt:0.########} USDT.");
            }
        }

        if (state is not null &&
            state.AverageEntryPrice > 0m &&
            recommendedSettings.StrategySelectionMode != StrategySelectionMode.Auto &&
            HasProfitableSellThatWouldBeCancelled(state, activeOrders, recommendation, recommendedSettings, strategy))
        {
            errors.Add("Applying this recommendation would cancel at least one profitable active sell order without preserving its price level. Leave the sell in place or cancel active orders manually if you intentionally want to rebuild.");
        }

        return errors;
    }

    private static bool HasMaterialChange(
        GridBotSettings currentSettings,
        AutoConfigRecommendation recommendation)
    {
        if (currentSettings.StrategyType != recommendation.StrategyType)
        {
            return true;
        }

        var stepReference = decimal.Max(0.00000001m, decimal.Max(currentSettings.Step, recommendation.Step));
        if (Math.Abs(currentSettings.LowerPrice - recommendation.LowerPrice) >= stepReference ||
            Math.Abs(currentSettings.UpperPrice - recommendation.UpperPrice) >= stepReference ||
            Math.Abs(currentSettings.StopLowerPrice - recommendation.StopLowerPrice) >= stepReference ||
            Math.Abs(currentSettings.StopUpperPrice - recommendation.StopUpperPrice) >= stepReference)
        {
            return true;
        }

        if (currentSettings.Step > 0m &&
            Math.Abs(currentSettings.Step - recommendation.Step) / currentSettings.Step >= 0.1m)
        {
            return true;
        }

        if (currentSettings.OrderSizeUsdt > 0m &&
            Math.Abs(currentSettings.OrderSizeUsdt - recommendation.OrderSizeUsdt) / currentSettings.OrderSizeUsdt >= 0.1m)
        {
            return true;
        }

        return false;
    }

    private static bool HasProfitableSellThatWouldBeCancelled(
        BotState state,
        IReadOnlyCollection<GridOrder> activeOrders,
        AutoConfigRecommendation recommendation,
        GridBotSettings recommendedSettings,
        IGridTradingStrategy strategy)
    {
        var profitableSellOrders = activeOrders
            .Where(order =>
                order.IsActive &&
                order.Side == TradeSide.Sell &&
                order.Price > state.AverageEntryPrice &&
                order.Quantity > order.FilledQuantity)
            .ToArray();
        if (profitableSellOrders.Length == 0)
        {
            return false;
        }

        if (PreservesProfitableSellOrders(recommendedSettings))
        {
            return false;
        }

        var recommendedSellPrices = recommendedSettings.StrategyType is TradingStrategyType.Grid
                or TradingStrategyType.Combo
                or TradingStrategyType.Hybrid
            ? BuildRecommendedSellPrices(recommendedSettings, state, strategy)
            : [];
        var tolerance = decimal.Max(0.00000001m, recommendation.Step / 2m);

        return profitableSellOrders.Any(order =>
            recommendedSellPrices.All(price => Math.Abs(price - order.Price) > tolerance));
    }

    private static bool PreservesProfitableSellOrders(GridBotSettings recommendedSettings)
    {
        return recommendedSettings.StrategySelectionMode == StrategySelectionMode.Auto &&
            recommendedSettings.StrategyType is TradingStrategyType.Btd
                or TradingStrategyType.NoTrade
                or TradingStrategyType.Pause
                or TradingStrategyType.ReduceOnly
                or TradingStrategyType.Signal
                or TradingStrategyType.TrendFollow
                or TradingStrategyType.TrendFollowing
                or TradingStrategyType.Breakout;
    }

    private static IReadOnlyList<decimal> BuildRecommendedSellPrices(
        GridBotSettings recommendedSettings,
        BotState state,
        IGridTradingStrategy strategy)
    {
        var recommendedOptions = new GridOptions
        {
            Symbol = recommendedSettings.Symbol,
            Category = recommendedSettings.Category,
            LowerPrice = recommendedSettings.LowerPrice,
            UpperPrice = recommendedSettings.UpperPrice,
            Step = recommendedSettings.Step,
            OrderSizeUsdt = recommendedSettings.OrderSizeUsdt,
            StopLowerPrice = recommendedSettings.StopLowerPrice,
            StopUpperPrice = recommendedSettings.StopUpperPrice
        };

        return strategy.BuildGrid(recommendedOptions)
            .Select(level => level.Price)
            .Where(price => price > state.AverageEntryPrice)
            .ToArray();
    }

    private static bool StrategyCanOpenBuyOrders(TradingStrategyType strategyType)
    {
        return strategyType is TradingStrategyType.Grid
            or TradingStrategyType.Dca
            or TradingStrategyType.Combo
            or TradingStrategyType.Hybrid
            or TradingStrategyType.Btd
            or TradingStrategyType.Signal
            or TradingStrategyType.TrendFollow
            or TradingStrategyType.TrendFollowing
            or TradingStrategyType.Breakout;
    }
}
