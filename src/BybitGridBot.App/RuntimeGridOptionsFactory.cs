using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.App;

internal static class RuntimeGridOptionsFactory
{
    public static GridBotSettings ToRuntimeSettings(GridOptions options)
    {
        var (selectionMode, strategyType) = ResolveInitialStrategy(options.BotType);
        return new GridBotSettings
        {
            Symbol = options.Symbol,
            Category = options.Category,
            StrategySelectionMode = selectionMode,
            StrategyType = strategyType,
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
            BotType = defaults.BotType,
            SpotOnly = defaults.SpotOnly,
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
            RangeAdxMax = defaults.RangeAdxMax,
            BreakoutAdxMin = defaults.BreakoutAdxMin,
            TrendAdxMin = defaults.TrendAdxMin,
            AdxPeriod = defaults.AdxPeriod,
            AtrPeriod = defaults.AtrPeriod,
            VolumeSmaPeriod = defaults.VolumeSmaPeriod,
            VolumeSpikeMultiplier = defaults.VolumeSpikeMultiplier,
            AtrSpikeMultiplier = defaults.AtrSpikeMultiplier,
            BtcFilterEnabled = defaults.BtcFilterEnabled,
            BtcMaxMovePercent = defaults.BtcMaxMovePercent,
            BtcLookbackCandles = defaults.BtcLookbackCandles,
            StrategyMinScore = defaults.StrategyMinScore,
            MinStrategyScore = defaults.MinStrategyScore,
            StrategyMinConfidence = defaults.StrategyMinConfidence,
            MinPhaseConfidence = defaults.MinPhaseConfidence,
            StrategySwitchCooldownMinutes = defaults.StrategySwitchCooldownMinutes,
            StrategyConfirmationCandles = defaults.StrategyConfirmationCandles,
            PhaseConfirmationCandles = defaults.PhaseConfirmationCandles,
            AggressiveModeEnabled = defaults.AggressiveModeEnabled,
            AggressiveModeCooldownMinutes = defaults.AggressiveModeCooldownMinutes,
            AggressiveStopLossPercent = defaults.AggressiveStopLossPercent,
            AutoRecommendationApplyIntervalMinutes = defaults.AutoRecommendationApplyIntervalMinutes,
            GridCapitalPercent = defaults.GridCapitalPercent,
            DcaOrderSizeUsdt = defaults.DcaOrderSizeUsdt,
            DcaMaxPositionUsdt = defaults.DcaMaxPositionUsdt,
            DcaAllowDowntrend = defaults.DcaAllowDowntrend,
            DcaCapitalPercent = defaults.DcaCapitalPercent,
            BtdDipPercent = defaults.BtdDipPercent,
            BtdMinTrendScore = defaults.BtdMinTrendScore,
            BtdCapitalPercent = defaults.BtdCapitalPercent,
            BtdRequireUptrend = defaults.BtdRequireUptrend,
            BtdMaxDistanceFromEmaPercent = defaults.BtdMaxDistanceFromEmaPercent,
            BtdMinPullbackPercent = defaults.BtdMinPullbackPercent,
            BtdBlockOnBtcRiskOff = defaults.BtdBlockOnBtcRiskOff,
            BreakoutConfirmationCandles = defaults.BreakoutConfirmationCandles,
            BreakoutVolumeMultiplier = defaults.BreakoutVolumeMultiplier,
            BreakoutAtrStopMultiplier = defaults.BreakoutAtrStopMultiplier,
            BreakoutRiskReward = defaults.BreakoutRiskReward,
            BreakoutMaxDistanceFromEmaPercent = defaults.BreakoutMaxDistanceFromEmaPercent,
            BreakoutUseRetest = defaults.BreakoutUseRetest,
            BreakoutCapitalPercent = defaults.BreakoutCapitalPercent,
            TrendEmaFast = defaults.TrendEmaFast,
            TrendEmaSlow = defaults.TrendEmaSlow,
            EmaFast = defaults.EmaFast,
            EmaSlow = defaults.EmaSlow,
            TrendMaxDistanceFromEmaPercent = defaults.TrendMaxDistanceFromEmaPercent,
            TrendTrailingStopAtrMultiplier = defaults.TrendTrailingStopAtrMultiplier,
            TrendPartialTakeProfitPercent = defaults.TrendPartialTakeProfitPercent,
            TrendPartialTakeProfitAtR = defaults.TrendPartialTakeProfitAtR,
            TrendCapitalPercent = defaults.TrendCapitalPercent,
            MinUsdtReservePercent = defaults.MinUsdtReservePercent,
            MaxTotalExposurePercent = defaults.MaxTotalExposurePercent,
            FeePercent = defaults.FeePercent,
            SlippagePercent = defaults.SlippagePercent,
            MinExpectedProfitPercent = defaults.MinExpectedProfitPercent,
            BotLoopIntervalSeconds = defaults.BotLoopIntervalSeconds,
            PaperInitialUsdt = defaults.PaperInitialUsdt,
            PaperInitialBaseAssetQuantity = defaults.PaperInitialBaseAssetQuantity,
            PaperBootstrapInventoryEnabled = defaults.PaperBootstrapInventoryEnabled,
            BigRedCandlePercent = defaults.BigRedCandlePercent,
            DumpMovePercent = defaults.DumpMovePercent,
            DumpLookbackCandles = defaults.DumpLookbackCandles,
            DumpPauseCandles = defaults.DumpPauseCandles,
            CancelGridBuysOnDump = defaults.CancelGridBuysOnDump,
            GridDynamicStepEnabled = defaults.GridDynamicStepEnabled,
            GridStepAtrMultiplier = defaults.GridStepAtrMultiplier,
            GridMinStepPercent = defaults.GridMinStepPercent,
            GridMaxStepPercent = defaults.GridMaxStepPercent,
            GridCancelBuysOnDump = defaults.GridCancelBuysOnDump,
            GridHandoffToBreakout = defaults.GridHandoffToBreakout,
            EnableBreakoutHandoff = defaults.EnableBreakoutHandoff,
            ProfitProtectionEnabled = defaults.ProfitProtectionEnabled,
            ProfitProtectionTriggerPercent = defaults.ProfitProtectionTriggerPercent,
            TrailingStopEnabled = defaults.TrailingStopEnabled,
            TrailingStopPercent = defaults.TrailingStopPercent,
            PartialTakeProfitEnabled = defaults.PartialTakeProfitEnabled,
            PartialTakeProfitPercent = defaults.PartialTakeProfitPercent,
            EnableNoTradeReasonTracking = defaults.EnableNoTradeReasonTracking,
            EnableStrategyPerformanceAnalytics = defaults.EnableStrategyPerformanceAnalytics,
            AutoRecenterEnabled = defaults.AutoRecenterEnabled,
            AutoRecenterCandleInterval = defaults.AutoRecenterCandleInterval,
            AutoRecenterLookbackCandles = defaults.AutoRecenterLookbackCandles,
            AutoRecenterPaddingSteps = defaults.AutoRecenterPaddingSteps,
            AutoRecenterMinShiftSteps = defaults.AutoRecenterMinShiftSteps,
            TrailingProtectionEnabled = defaults.TrailingProtectionEnabled,
            TrailingProtectionCandleInterval = defaults.TrailingProtectionCandleInterval,
            TrailingProtectionLookbackCandles = defaults.TrailingProtectionLookbackCandles,
            TrailingProtectionPumpPercent = defaults.TrailingProtectionPumpPercent,
            TrailingProtectionPullbackPercent = defaults.TrailingProtectionPullbackPercent,
            CandleInterval = defaults.CandleInterval
        };
    }

    private static (StrategySelectionMode SelectionMode, TradingStrategyType StrategyType) ResolveInitialStrategy(BotType botType)
    {
        return botType switch
        {
            BotType.Auto => (StrategySelectionMode.Auto, TradingStrategyType.Pause),
            BotType.Dca => (StrategySelectionMode.Manual, TradingStrategyType.Dca),
            BotType.Btd => (StrategySelectionMode.Manual, TradingStrategyType.Btd),
            BotType.Combo => (StrategySelectionMode.Manual, TradingStrategyType.Combo),
            BotType.Signal => (StrategySelectionMode.Manual, TradingStrategyType.Signal),
            BotType.Hybrid => (StrategySelectionMode.Manual, TradingStrategyType.Hybrid),
            BotType.Grid => (StrategySelectionMode.Manual, TradingStrategyType.Grid),
            BotType.Breakout => (StrategySelectionMode.Manual, TradingStrategyType.Breakout),
            BotType.Trend => (StrategySelectionMode.Manual, TradingStrategyType.TrendFollowing),
            BotType.Pause => (StrategySelectionMode.Manual, TradingStrategyType.Pause),
            _ => (StrategySelectionMode.Auto, TradingStrategyType.Pause)
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
