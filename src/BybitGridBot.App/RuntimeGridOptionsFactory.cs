using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.App;

internal static class RuntimeGridOptionsFactory
{
    public static GridBotSettings ToRuntimeSettings(GridOptions options)
    {
        return new GridBotSettings
        {
            Symbol = options.Symbol,
            Category = options.Category,
            StrategySelectionMode = StrategySelectionMode.Manual,
            StrategyType = TradingStrategyType.Grid,
            StrategyConfigJson = "{}",
            LowerPrice = options.LowerPrice,
            UpperPrice = options.UpperPrice,
            Step = options.Step,
            OrderSizeUsdt = options.OrderSizeUsdt,
            StopLowerPrice = options.StopLowerPrice,
            StopUpperPrice = options.StopUpperPrice,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public static GridOptions ToGridOptions(GridBotSettings settings, GridOptions defaults)
    {
        return new GridOptions
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            LowerPrice = settings.LowerPrice,
            UpperPrice = settings.UpperPrice,
            Step = settings.Step,
            OrderSizeUsdt = settings.OrderSizeUsdt,
            MinOrderSizeUsdt = defaults.MinOrderSizeUsdt,
            MinNetProfitUsdt = defaults.MinNetProfitUsdt,
            DynamicOrderSizeEnabled = defaults.DynamicOrderSizeEnabled,
            DynamicLowerOrderMultiplier = defaults.DynamicLowerOrderMultiplier,
            DynamicUpperOrderMultiplier = defaults.DynamicUpperOrderMultiplier,
            DailyTakeProfitUsdt = defaults.DailyTakeProfitUsdt,
            DailyTakeProfitOrderMultiplier = defaults.DailyTakeProfitOrderMultiplier,
            StopLowerPrice = settings.StopLowerPrice,
            StopUpperPrice = settings.StopUpperPrice,
            MarketFilterEnabled = defaults.MarketFilterEnabled,
            AdxMax = defaults.AdxMax,
            BtcFilterEnabled = defaults.BtcFilterEnabled,
            BtcMaxMovePercent = defaults.BtcMaxMovePercent,
            BtcLookbackCandles = defaults.BtcLookbackCandles,
            FeePercent = defaults.FeePercent,
            BotLoopIntervalSeconds = defaults.BotLoopIntervalSeconds,
            PaperInitialUsdt = defaults.PaperInitialUsdt,
            PaperInitialBaseAssetQuantity = defaults.PaperInitialBaseAssetQuantity,
            PaperBootstrapInventoryEnabled = defaults.PaperBootstrapInventoryEnabled,
            AutoRecenterEnabled = defaults.AutoRecenterEnabled,
            AutoRecenterCandleInterval = defaults.AutoRecenterCandleInterval,
            AutoRecenterLookbackCandles = defaults.AutoRecenterLookbackCandles,
            AutoRecenterPaddingSteps = defaults.AutoRecenterPaddingSteps,
            AutoRecenterMinShiftSteps = defaults.AutoRecenterMinShiftSteps,
            CandleInterval = defaults.CandleInterval
        };
    }

    public static bool IsSameTradingConfiguration(GridOptions left, GridOptions right)
    {
        return string.Equals(left.Symbol, right.Symbol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Category, right.Category, StringComparison.OrdinalIgnoreCase)
            && left.LowerPrice == right.LowerPrice
            && left.UpperPrice == right.UpperPrice
            && left.Step == right.Step
            && left.OrderSizeUsdt == right.OrderSizeUsdt
            && left.StopLowerPrice == right.StopLowerPrice
            && left.StopUpperPrice == right.StopUpperPrice;
    }

    public static bool IsSameTradingConfiguration(GridBotSettings left, GridBotSettings right)
    {
        return string.Equals(left.Symbol, right.Symbol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Category, right.Category, StringComparison.OrdinalIgnoreCase)
            && left.StrategySelectionMode == right.StrategySelectionMode
            && left.StrategyType == right.StrategyType
            && string.Equals(left.StrategyConfigJson, right.StrategyConfigJson, StringComparison.Ordinal)
            && left.LowerPrice == right.LowerPrice
            && left.UpperPrice == right.UpperPrice
            && left.Step == right.Step
            && left.OrderSizeUsdt == right.OrderSizeUsdt
            && left.StopLowerPrice == right.StopLowerPrice
            && left.StopUpperPrice == right.StopUpperPrice;
    }
}
