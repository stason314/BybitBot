using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class FuturesPause : IFuturesStrategy
{
    public FuturesStrategyType StrategyType => FuturesStrategyType.Pause;

    public FuturesStrategyDecision Decide(FuturesStrategyContext context) =>
        FuturesStrategyDecision.Empty("Futures strategy paused.");
}

public sealed class FuturesReduceOnly : IFuturesStrategy
{
    public FuturesStrategyType StrategyType => FuturesStrategyType.ReduceOnly;

    public FuturesStrategyDecision Decide(FuturesStrategyContext context)
    {
        if (context.Position.Size <= 0m)
        {
            return FuturesStrategyDecision.Empty("No futures position to reduce.");
        }

        return FuturesShortOnlySignals.IsShort(context.Position.Side)
            ? new FuturesStrategyDecision
            {
                TradeIntents =
                [
                    FuturesStrategyIntentFactory.CloseShort(
                        context.Settings,
                        context.Position,
                        context.CurrentPrice,
                        context.Instrument,
                        "reduce-only")
                ]
            }
            : new FuturesStrategyDecision
            {
                TradeIntents =
                [
                    FuturesStrategyIntentFactory.CloseLong(
                        context.Settings,
                        context.Position,
                        context.CurrentPrice,
                        context.Instrument,
                        "reduce-only")
                ]
            };
    }
}

public sealed class FuturesTrendFollowLongOnly : IFuturesStrategy
{
    public FuturesStrategyType StrategyType => FuturesStrategyType.TrendFollow;

    public FuturesStrategyDecision Decide(FuturesStrategyContext context)
    {
        if (!FuturesLongOnlySignals.TryAnalyze(context, out var signal))
        {
            return FuturesStrategyDecision.Empty("Futures trend-follow market data unavailable.");
        }

        if (FuturesLongOnlySignals.TryResolveCloseOpenLongReason(context, signal, out var closeReason))
        {
            return FuturesLongOnlySignals.CloseLong(context, closeReason);
        }

        if (FuturesLongOnlySignals.ShouldTakePartialProfit(context, signal))
        {
            return FuturesLongOnlySignals.CloseLong(context, "partial-take-profit", quantityMultiplier: 0.5m);
        }

        if (FuturesLongOnlySignals.ShouldCloseTrailingProfit(context, signal))
        {
            return FuturesLongOnlySignals.CloseLong(context, "trailing-profit");
        }

        if (FuturesLongOnlySignals.ShouldOpenAggressiveTestLong(context, signal))
        {
            return FuturesLongOnlySignals.OpenLong(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty(FuturesLongOnlySignals.OpenLongHoldReason(context, signal));
        }

        return signal.MovePercent >= 0.8m
            ? FuturesLongOnlySignals.OpenLong(context)
            : FuturesStrategyDecision.Empty($"Waiting long trend continuation. Move={signal.MovePercent:F2}%, required=0.80%.");
    }
}

public sealed class FuturesBreakoutLongOnly : IFuturesStrategy
{
    public FuturesStrategyType StrategyType => FuturesStrategyType.Breakout;

    public FuturesStrategyDecision Decide(FuturesStrategyContext context)
    {
        if (!FuturesLongOnlySignals.TryAnalyze(context, out var signal))
        {
            return FuturesStrategyDecision.Empty("Futures breakout market data unavailable.");
        }

        if (FuturesLongOnlySignals.TryResolveCloseOpenLongReason(context, signal, out var closeReason))
        {
            return FuturesLongOnlySignals.CloseLong(context, closeReason);
        }

        if (FuturesLongOnlySignals.ShouldTakePartialProfit(context, signal))
        {
            return FuturesLongOnlySignals.CloseLong(context, "partial-take-profit", quantityMultiplier: 0.5m);
        }

        if (FuturesLongOnlySignals.ShouldCloseTrailingProfit(context, signal))
        {
            return FuturesLongOnlySignals.CloseLong(context, "trailing-profit");
        }

        if (FuturesLongOnlySignals.ShouldOpenAggressiveTestLong(context, signal))
        {
            return FuturesLongOnlySignals.OpenLong(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty(FuturesLongOnlySignals.OpenLongHoldReason(context, signal));
        }

        return context.CurrentPrice >= signal.Resistance
            ? FuturesLongOnlySignals.OpenLong(context)
            : FuturesStrategyDecision.Empty($"Waiting breakout continuation. Price={context.CurrentPrice:F8}, resistance={signal.Resistance:F8}.");
    }
}

public sealed class FuturesGridLongOnly : IFuturesStrategy
{
    public FuturesStrategyType StrategyType => FuturesStrategyType.GridLongOnly;

    public FuturesStrategyDecision Decide(FuturesStrategyContext context)
    {
        if (!FuturesLongOnlySignals.TryAnalyze(context, out var signal))
        {
            return FuturesStrategyDecision.Empty("Futures grid market data unavailable.");
        }

        if (FuturesLongOnlySignals.TryResolveCloseOpenLongReason(context, signal, out var closeReason))
        {
            return FuturesLongOnlySignals.CloseLong(context, closeReason);
        }

        if (FuturesLongOnlySignals.ShouldTakePartialProfit(context, signal))
        {
            return FuturesLongOnlySignals.CloseLong(context, "partial-take-profit", quantityMultiplier: 0.5m);
        }

        if (FuturesLongOnlySignals.ShouldCloseTrailingProfit(context, signal))
        {
            return FuturesLongOnlySignals.CloseLong(context, "trailing-profit");
        }

        if (FuturesLongOnlySignals.ShouldOpenAggressiveTestLong(context, signal))
        {
            return FuturesLongOnlySignals.OpenLong(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty(FuturesLongOnlySignals.OpenLongHoldReason(context, signal));
        }

        var entryBand = signal.Support + (signal.Range * 0.35m);
        return context.CurrentPrice <= entryBand
            ? FuturesLongOnlySignals.OpenLong(context)
            : FuturesStrategyDecision.Empty($"Waiting pullback into long grid entry band. Price={context.CurrentPrice:F8}, entryBand={entryBand:F8}.");
    }
}

public sealed class FuturesTrendFollowShortOnly : IFuturesStrategy
{
    public FuturesStrategyType StrategyType => FuturesStrategyType.TrendFollowShortOnly;

    public FuturesStrategyDecision Decide(FuturesStrategyContext context)
    {
        if (!FuturesShortOnlySignals.TryAnalyze(context, out var signal))
        {
            return FuturesStrategyDecision.Empty("Futures short trend-follow market data unavailable.");
        }

        if (FuturesShortOnlySignals.TryResolveCloseOpenShortReason(context, signal, out var closeReason))
        {
            return FuturesShortOnlySignals.CloseShort(context, closeReason);
        }

        if (FuturesShortOnlySignals.ShouldTakePartialProfit(context, signal))
        {
            return FuturesShortOnlySignals.CloseShort(context, "partial-take-profit", quantityMultiplier: 0.5m);
        }

        if (FuturesShortOnlySignals.ShouldCloseTrailingProfit(context, signal))
        {
            return FuturesShortOnlySignals.CloseShort(context, "trailing-profit");
        }

        if (FuturesShortOnlySignals.ShouldOpenAggressiveTestShort(context, signal))
        {
            return FuturesShortOnlySignals.OpenShort(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty(FuturesShortOnlySignals.OpenShortHoldReason(context, signal));
        }

        return signal.MovePercent <= -0.8m
            ? FuturesShortOnlySignals.OpenShort(context)
            : FuturesStrategyDecision.Empty($"Waiting short trend continuation. Move={signal.MovePercent:F2}%, required=-0.80%.");
    }
}

public sealed class FuturesBreakdownShortOnly : IFuturesStrategy
{
    public FuturesStrategyType StrategyType => FuturesStrategyType.BreakdownShort;

    public FuturesStrategyDecision Decide(FuturesStrategyContext context)
    {
        if (!FuturesShortOnlySignals.TryAnalyze(context, out var signal))
        {
            return FuturesStrategyDecision.Empty("Futures breakdown market data unavailable.");
        }

        if (FuturesShortOnlySignals.TryResolveCloseOpenShortReason(context, signal, out var closeReason))
        {
            return FuturesShortOnlySignals.CloseShort(context, closeReason);
        }

        if (FuturesShortOnlySignals.ShouldTakePartialProfit(context, signal))
        {
            return FuturesShortOnlySignals.CloseShort(context, "partial-take-profit", quantityMultiplier: 0.5m);
        }

        if (FuturesShortOnlySignals.ShouldCloseTrailingProfit(context, signal))
        {
            return FuturesShortOnlySignals.CloseShort(context, "trailing-profit");
        }

        if (FuturesShortOnlySignals.ShouldOpenAggressiveTestShort(context, signal))
        {
            return FuturesShortOnlySignals.OpenShort(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty(FuturesShortOnlySignals.OpenShortHoldReason(context, signal));
        }

        return context.CurrentPrice <= signal.Support
            ? FuturesShortOnlySignals.OpenShort(context)
            : FuturesStrategyDecision.Empty($"Waiting breakdown continuation. Price={context.CurrentPrice:F8}, support={signal.Support:F8}.");
    }
}

public sealed class FuturesGridShortOnly : IFuturesStrategy
{
    public FuturesStrategyType StrategyType => FuturesStrategyType.GridShortOnly;

    public FuturesStrategyDecision Decide(FuturesStrategyContext context)
    {
        if (!FuturesShortOnlySignals.TryAnalyze(context, out var signal))
        {
            return FuturesStrategyDecision.Empty("Futures short grid market data unavailable.");
        }

        if (FuturesShortOnlySignals.TryResolveCloseOpenShortReason(context, signal, out var closeReason))
        {
            return FuturesShortOnlySignals.CloseShort(context, closeReason);
        }

        if (FuturesShortOnlySignals.ShouldTakePartialProfit(context, signal))
        {
            return FuturesShortOnlySignals.CloseShort(context, "partial-take-profit", quantityMultiplier: 0.5m);
        }

        if (FuturesShortOnlySignals.ShouldCloseTrailingProfit(context, signal))
        {
            return FuturesShortOnlySignals.CloseShort(context, "trailing-profit");
        }

        if (FuturesShortOnlySignals.ShouldOpenAggressiveTestShort(context, signal))
        {
            return FuturesShortOnlySignals.OpenShort(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty(FuturesShortOnlySignals.OpenShortHoldReason(context, signal));
        }

        var entryBand = signal.Resistance - (signal.Range * 0.35m);
        return context.CurrentPrice >= entryBand
            ? FuturesShortOnlySignals.OpenShort(context)
            : FuturesStrategyDecision.Empty($"Waiting rebound into short grid entry band. Price={context.CurrentPrice:F8}, entryBand={entryBand:F8}.");
    }
}

internal static class FuturesLongOnlySignals
{
    public static bool TryAnalyze(FuturesStrategyContext context, out FuturesLongOnlySignal signal)
    {
        signal = default;
        if (context.Candles.Count < 2 || context.CurrentPrice <= 0m)
        {
            return false;
        }

        var ordered = context.Candles.OrderBy(candle => candle.OpenTime).ToArray();
        var lookback = ordered.TakeLast(Math.Min(60, ordered.Length)).ToArray();
        var first = lookback[0].Open > 0m ? lookback[0].Open : lookback[0].Close;
        var movePercent = first > 0m ? (context.CurrentPrice - first) / first * 100m : 0m;
        var resistance = lookback.Take(Math.Max(1, lookback.Length - 1)).Max(candle => candle.High);
        var support = lookback.Min(candle => candle.Low);
        var range = decimal.Max(context.Instrument.TickSize, resistance - support);
        signal = new FuturesLongOnlySignal(movePercent, support, resistance, range);
        return true;
    }

    public static bool TryResolveCloseOpenLongReason(
        FuturesStrategyContext context,
        FuturesLongOnlySignal signal,
        out string reason)
    {
        reason = string.Empty;
        if (context.Position.Size <= 0m || !IsLong(context.Position.Side))
        {
            return false;
        }

        var stopPrice = context.Position.EntryPrice * (1m - context.Settings.StopLossPercent / 100m);
        var takeProfitPrice = context.Position.EntryPrice * (1m + context.Settings.TakeProfitPercent / 100m);
        if (context.CurrentPrice <= stopPrice)
        {
            reason = "stop-loss";
            return true;
        }

        if (context.CurrentPrice >= takeProfitPrice)
        {
            reason = "final-tp";
            return true;
        }

        if (IsGridLongInvalidated(context, signal))
        {
            reason = "grid-invalidation";
            return true;
        }

        if (signal.MovePercent < -1m)
        {
            reason = "exit-signal";
            return true;
        }

        return false;
    }

    public static bool ShouldTakePartialProfit(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        if (context.Position.Size <= 0m || !IsLong(context.Position.Side) || context.Position.EntryPrice <= 0m)
        {
            return false;
        }

        if (!CanPartialClose(context.Position.Size, context.Instrument))
        {
            return false;
        }

        if (HasCloseAfterLastEntry(context, FuturesTradeAction.OpenLong, FuturesTradeAction.CloseLong))
        {
            return false;
        }

        var profitPercent = (context.CurrentPrice - context.Position.EntryPrice) / context.Position.EntryPrice * 100m;
        var retracementFromResistance = signal.Resistance > 0m
            ? (signal.Resistance - context.CurrentPrice) / signal.Resistance * 100m
            : 0m;
        var minimumRewardExit = MinimumRewardExitPercent(context);
        var adaptiveTakeProfit = decimal.Min(context.Settings.TakeProfitPercent, minimumRewardExit);
        return profitPercent >= adaptiveTakeProfit ||
            (profitPercent >= minimumRewardExit && retracementFromResistance >= 0.35m);
    }

    public static bool ShouldCloseTrailingProfit(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        if (context.Position.Size <= 0m || !IsLong(context.Position.Side) || context.Position.EntryPrice <= 0m)
        {
            return false;
        }

        if (!HasCloseAfterLastEntry(context, FuturesTradeAction.OpenLong, FuturesTradeAction.CloseLong))
        {
            return false;
        }

        var profitPercent = (context.CurrentPrice - context.Position.EntryPrice) / context.Position.EntryPrice * 100m;
        var minimumRewardExit = MinimumRewardExitPercent(context);
        if (profitPercent < minimumRewardExit)
        {
            return false;
        }

        var retracementFromResistance = signal.Resistance > 0m
            ? (signal.Resistance - context.CurrentPrice) / signal.Resistance * 100m
            : 0m;
        return retracementFromResistance >= 0.25m ||
            (profitPercent >= minimumRewardExit && signal.MovePercent < 0m);
    }

    public static bool HasCloseAfterLastEntry(
        FuturesStrategyContext context,
        FuturesTradeAction entryAction,
        FuturesTradeAction closeAction)
    {
        var lastEntry = context.RecentFills
            .Where(fill => fill.Action == entryAction)
            .OrderByDescending(fill => fill.CreatedAt)
            .FirstOrDefault();
        if (lastEntry is null)
        {
            return false;
        }

        return context.RecentFills.Any(fill =>
            fill.CreatedAt > lastEntry.CreatedAt &&
            (fill.Action == closeAction || fill.Action == FuturesTradeAction.ReduceOnlyClose));
    }

    public static bool ShouldOpenAggressiveTestLong(FuturesStrategyContext context, FuturesLongOnlySignal signal) =>
        context.Settings.AggressiveModeEnabled &&
        context.Settings.AggressiveModeKind == FuturesAggressiveModeKind.Test &&
        !FuturesShortOnlySignals.IsShort(context.Position.Side) &&
        signal.MovePercent > -1.2m &&
        !IsLongScaleInAfterRejection(context, signal);

    public static FuturesStrategyDecision OpenLong(FuturesStrategyContext context)
    {
        if (FuturesShortOnlySignals.IsShort(context.Position.Side))
        {
            return FuturesShortOnlySignals.CloseShort(context, "position-side-recovery");
        }

        var capacityReason = GetPositionFullReason(context);
        if (capacityReason is not null)
        {
            return FuturesStrategyDecision.Empty(capacityReason);
        }

        if (FuturesLongOnlySignals.TryAnalyze(context, out var signal) &&
            IsLongScaleInAfterRejection(context, signal))
        {
            return FuturesStrategyDecision.Empty("Rejection risk: long scale-in blocked below resistance; waiting breakout continuation.");
        }

        var entryIntent = FuturesStrategyIntentFactory.OpenLong(
            context.Settings,
            context.CurrentPrice,
            context.Instrument);
        return entryIntent.Quantity > 0m
            ? new FuturesStrategyDecision { TradeIntents = [entryIntent] }
            : FuturesStrategyDecision.Empty("Futures entry quantity is below instrument precision.");
    }

    public static FuturesStrategyDecision CloseLong(
        FuturesStrategyContext context,
        string reason,
        decimal quantityMultiplier = 1m) => new()
    {
        TradeIntents =
        [
            FuturesStrategyIntentFactory.CloseLong(
                context.Settings,
                ScalePositionQuantity(context.Position, quantityMultiplier),
                context.CurrentPrice,
                context.Instrument,
                reason)
        ]
    };

    public static string OpenLongHoldReason(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        var currentR = CalculateCurrentR(context, isShort: false);
        return $"Existing futures long remains open; waiting 1R profit, grid continuation, or protective exit. CurrentR={currentR:F2}, support={signal.Support:F8}, resistance={signal.Resistance:F8}.";
    }

    private static bool IsGridLongInvalidated(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        if (context.Settings.StrategyType != FuturesStrategyType.GridLongOnly ||
            context.Position.Size <= 0m ||
            !IsLong(context.Position.Side) ||
            context.Position.EntryPrice <= 0m ||
            context.Settings.StopLossPercent <= 0m)
        {
            return false;
        }

        var lossPercent = (context.Position.EntryPrice - context.CurrentPrice) / context.Position.EntryPrice * 100m;
        if (lossPercent / context.Settings.StopLossPercent < 0.35m)
        {
            return false;
        }

        var range = signal.Range <= 0m ? context.Instrument.TickSize : signal.Range;
        var midpoint = signal.Support + range * 0.5m;
        return context.CurrentPrice <= midpoint && signal.Resistance < context.Position.EntryPrice;
    }

    public static decimal CalculateCurrentR(FuturesStrategyContext context, bool isShort)
    {
        if (context.Position.EntryPrice <= 0m || context.CurrentPrice <= 0m || context.Settings.StopLossPercent <= 0m)
        {
            return 0m;
        }

        var profitPercent = isShort
            ? (context.Position.EntryPrice - context.CurrentPrice) / context.Position.EntryPrice * 100m
            : (context.CurrentPrice - context.Position.EntryPrice) / context.Position.EntryPrice * 100m;
        return profitPercent / context.Settings.StopLossPercent;
    }

    private static bool IsLongScaleInAfterRejection(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        var range = signal.Range <= 0m ? context.Instrument.TickSize : signal.Range;
        var rejectionFromResistance = signal.Resistance - context.CurrentPrice;
        return context.Position.Size > 0m &&
            context.CurrentPrice < signal.Resistance - range * 0.35m &&
            rejectionFromResistance / context.CurrentPrice * 100m >= 0.35m;
    }

    public static string? GetPositionFullReason(FuturesStrategyContext context)
    {
        if (context.Position.Size <= 0m || context.Settings.MaxNotionalUsdt <= 0m)
        {
            return null;
        }

        var currentNotional = decimal.Max(0m, context.Position.PositionValueUsdt);
        var nextOrderNotional = EstimateNextOrderNotional(context);
        return currentNotional + nextOrderNotional > context.Settings.MaxNotionalUsdt
            ? $"Position is full; waiting for net-positive exit or breakout continuation. Current notional={currentNotional:F4}, next order={nextOrderNotional:F4}, max={context.Settings.MaxNotionalUsdt:F4}."
            : null;
    }

    private static decimal EstimateNextOrderNotional(FuturesStrategyContext context)
    {
        var price = context.CurrentPrice > 0m ? context.CurrentPrice : context.Position.MarkPrice;
        if (price <= 0m)
        {
            return 0m;
        }

        var requestedNotional = ResolveEntryNotional(context.Settings);
        var requestedQuantity = requestedNotional / price;
        var minimumQuantity = context.Instrument.MinOrderQty;
        if (context.Instrument.MinOrderAmount > 0m)
        {
            minimumQuantity = decimal.Max(minimumQuantity, context.Instrument.MinOrderAmount / price);
        }

        var quantity = RoundQuantityUp(decimal.Max(requestedQuantity, minimumQuantity), context.Instrument);
        return price * quantity;
    }

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
            using var document = System.Text.Json.JsonDocument.Parse(settings.StrategyConfigJson);
            if (document.RootElement.TryGetProperty("entryNotionalUsdt", out var property) &&
                property.ValueKind == System.Text.Json.JsonValueKind.Number &&
                property.TryGetDecimal(out var configured) &&
                configured > 0m)
            {
                var multiplier = settings.AggressiveModeEnabled
                    ? decimal.Max(0.01m, settings.AggressiveEntryMultiplier)
                    : 1m;
                return decimal.Min(settings.MaxNotionalUsdt, configured * multiplier);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return fallback;
        }

        return fallback;
    }

    private static decimal RoundQuantityUp(decimal quantity, FuturesInstrumentRules instrument)
    {
        var step = instrument.QtyStep > 0m ? instrument.QtyStep : instrument.BasePrecision;
        return step > 0m ? Math.Ceiling(quantity / step) * step : quantity;
    }

    private static FuturesPositionSnapshot ScalePositionQuantity(FuturesPositionSnapshot position, decimal multiplier)
    {
        if (multiplier >= 1m)
        {
            return position;
        }

        return new FuturesPositionSnapshot
        {
            Symbol = position.Symbol,
            Category = position.Category,
            Side = position.Side,
            Size = decimal.Max(0m, position.Size * decimal.Max(0.01m, multiplier)),
            EntryPrice = position.EntryPrice,
            MarkPrice = position.MarkPrice,
            LiquidationPrice = position.LiquidationPrice,
            PositionValueUsdt = position.PositionValueUsdt,
            MarginUsedUsdt = position.MarginUsedUsdt,
            Leverage = position.Leverage,
            UnrealizedPnl = position.UnrealizedPnl,
            RealizedPnl = position.RealizedPnl,
            Funding = position.Funding,
            PositionIdx = position.PositionIdx,
            UpdatedAt = position.UpdatedAt
        };
    }

    private static bool CanPartialClose(decimal positionSize, FuturesInstrumentRules instrument)
    {
        var minCloseQuantity = instrument.MinOrderQty;
        var step = instrument.QtyStep > 0m ? instrument.QtyStep : instrument.BasePrecision;
        if (step > 0m)
        {
            minCloseQuantity = decimal.Max(minCloseQuantity, step);
        }

        return minCloseQuantity > 0m && positionSize >= minCloseQuantity * 2m;
    }

    private static bool IsLong(string side) =>
        string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Long", StringComparison.OrdinalIgnoreCase);

    private static decimal MinimumRewardExitPercent(FuturesStrategyContext context) =>
        decimal.Max(0.6m, context.Settings.StopLossPercent);
}

internal readonly record struct FuturesLongOnlySignal(
    decimal MovePercent,
    decimal Support,
    decimal Resistance,
    decimal Range);

internal static class FuturesShortOnlySignals
{
    public static bool TryAnalyze(FuturesStrategyContext context, out FuturesLongOnlySignal signal) =>
        FuturesLongOnlySignals.TryAnalyze(context, out signal);

    public static bool TryResolveCloseOpenShortReason(
        FuturesStrategyContext context,
        FuturesLongOnlySignal signal,
        out string reason)
    {
        reason = string.Empty;
        if (context.Position.Size <= 0m || !IsShort(context.Position.Side))
        {
            return false;
        }

        var stopPrice = context.Position.EntryPrice * (1m + context.Settings.StopLossPercent / 100m);
        var takeProfitPrice = context.Position.EntryPrice * (1m - context.Settings.TakeProfitPercent / 100m);
        if (context.CurrentPrice >= stopPrice)
        {
            reason = "stop-loss";
            return true;
        }

        if (context.CurrentPrice <= takeProfitPrice)
        {
            reason = "final-tp";
            return true;
        }

        if (IsGridShortInvalidated(context, signal))
        {
            reason = "grid-invalidation";
            return true;
        }

        if (signal.MovePercent > 1m)
        {
            reason = "exit-signal";
            return true;
        }

        return false;
    }

    public static bool ShouldTakePartialProfit(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        if (context.Position.Size <= 0m || !IsShort(context.Position.Side) || context.Position.EntryPrice <= 0m)
        {
            return false;
        }

        if (!CanPartialClose(context.Position.Size, context.Instrument))
        {
            return false;
        }

        if (FuturesLongOnlySignals.HasCloseAfterLastEntry(context, FuturesTradeAction.OpenShort, FuturesTradeAction.CloseShort))
        {
            return false;
        }

        var profitPercent = (context.Position.EntryPrice - context.CurrentPrice) / context.Position.EntryPrice * 100m;
        var reboundFromSupport = signal.Support > 0m
            ? (context.CurrentPrice - signal.Support) / signal.Support * 100m
            : 0m;
        var minimumRewardExit = MinimumRewardExitPercent(context);
        var adaptiveTakeProfit = decimal.Min(context.Settings.TakeProfitPercent, minimumRewardExit);
        return profitPercent >= adaptiveTakeProfit ||
            (profitPercent >= minimumRewardExit && reboundFromSupport >= 0.35m);
    }

    public static bool ShouldCloseTrailingProfit(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        if (context.Position.Size <= 0m || !IsShort(context.Position.Side) || context.Position.EntryPrice <= 0m)
        {
            return false;
        }

        if (!FuturesLongOnlySignals.HasCloseAfterLastEntry(context, FuturesTradeAction.OpenShort, FuturesTradeAction.CloseShort))
        {
            return false;
        }

        var profitPercent = (context.Position.EntryPrice - context.CurrentPrice) / context.Position.EntryPrice * 100m;
        var minimumRewardExit = MinimumRewardExitPercent(context);
        if (profitPercent < minimumRewardExit)
        {
            return false;
        }

        var reboundFromSupport = signal.Support > 0m
            ? (context.CurrentPrice - signal.Support) / signal.Support * 100m
            : 0m;
        return reboundFromSupport >= 0.25m ||
            (profitPercent >= minimumRewardExit && signal.MovePercent > 0m);
    }

    public static bool ShouldOpenAggressiveTestShort(FuturesStrategyContext context, FuturesLongOnlySignal signal) =>
        context.Settings.AggressiveModeEnabled &&
        context.Settings.AggressiveModeKind == FuturesAggressiveModeKind.Test &&
        !IsLong(context.Position.Side) &&
        signal.MovePercent < 1.2m &&
        !IsShortScaleInAfterRebound(context, signal);

    public static FuturesStrategyDecision OpenShort(FuturesStrategyContext context)
    {
        if (IsLong(context.Position.Side))
        {
            return FuturesLongOnlySignals.CloseLong(context, "position-side-recovery");
        }

        var capacityReason = FuturesLongOnlySignals.GetPositionFullReason(context);
        if (capacityReason is not null)
        {
            return FuturesStrategyDecision.Empty(capacityReason);
        }

        if (TryAnalyze(context, out var signal) &&
            IsShortScaleInAfterRebound(context, signal))
        {
            return FuturesStrategyDecision.Empty("Rebound risk: short scale-in blocked above support; waiting breakdown continuation.");
        }

        var entryIntent = FuturesStrategyIntentFactory.OpenShort(
            context.Settings,
            context.CurrentPrice,
            context.Instrument);
        return entryIntent.Quantity > 0m
            ? new FuturesStrategyDecision { TradeIntents = [entryIntent] }
            : FuturesStrategyDecision.Empty("Futures short entry quantity is below instrument precision.");
    }

    public static FuturesStrategyDecision CloseShort(
        FuturesStrategyContext context,
        string reason,
        decimal quantityMultiplier = 1m) => new()
    {
        TradeIntents =
        [
            FuturesStrategyIntentFactory.CloseShort(
                context.Settings,
                ScalePositionQuantity(context.Position, quantityMultiplier),
                context.CurrentPrice,
                context.Instrument,
                reason)
        ]
    };

    public static string OpenShortHoldReason(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        var currentR = FuturesLongOnlySignals.CalculateCurrentR(context, isShort: true);
        return $"Existing futures short remains open; waiting 1R profit, grid continuation, or protective exit. CurrentR={currentR:F2}, support={signal.Support:F8}, resistance={signal.Resistance:F8}.";
    }

    private static bool IsGridShortInvalidated(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        if (context.Settings.StrategyType != FuturesStrategyType.GridShortOnly ||
            context.Position.Size <= 0m ||
            !IsShort(context.Position.Side) ||
            context.Position.EntryPrice <= 0m ||
            context.Settings.StopLossPercent <= 0m)
        {
            return false;
        }

        var lossPercent = (context.CurrentPrice - context.Position.EntryPrice) / context.Position.EntryPrice * 100m;
        if (lossPercent / context.Settings.StopLossPercent < 0.35m)
        {
            return false;
        }

        var range = signal.Range <= 0m ? context.Instrument.TickSize : signal.Range;
        var midpoint = signal.Support + range * 0.5m;
        return context.CurrentPrice >= midpoint && signal.Support > context.Position.EntryPrice;
    }

    private static bool IsShortScaleInAfterRebound(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        var range = signal.Range <= 0m ? context.Instrument.TickSize : signal.Range;
        var reboundFromSupport = context.CurrentPrice - signal.Support;
        return context.Position.Size > 0m &&
            context.CurrentPrice > signal.Support + range * 0.35m &&
            reboundFromSupport / context.CurrentPrice * 100m >= 0.35m;
    }

    private static FuturesPositionSnapshot ScalePositionQuantity(FuturesPositionSnapshot position, decimal multiplier)
    {
        if (multiplier >= 1m)
        {
            return position;
        }

        return new FuturesPositionSnapshot
        {
            Symbol = position.Symbol,
            Category = position.Category,
            Side = position.Side,
            Size = decimal.Max(0m, position.Size * decimal.Max(0.01m, multiplier)),
            EntryPrice = position.EntryPrice,
            MarkPrice = position.MarkPrice,
            LiquidationPrice = position.LiquidationPrice,
            PositionValueUsdt = position.PositionValueUsdt,
            MarginUsedUsdt = position.MarginUsedUsdt,
            Leverage = position.Leverage,
            UnrealizedPnl = position.UnrealizedPnl,
            RealizedPnl = position.RealizedPnl,
            Funding = position.Funding,
            PositionIdx = position.PositionIdx,
            UpdatedAt = position.UpdatedAt
        };
    }

    private static bool CanPartialClose(decimal positionSize, FuturesInstrumentRules instrument)
    {
        var minCloseQuantity = instrument.MinOrderQty;
        var step = instrument.QtyStep > 0m ? instrument.QtyStep : instrument.BasePrecision;
        if (step > 0m)
        {
            minCloseQuantity = decimal.Max(minCloseQuantity, step);
        }

        return minCloseQuantity > 0m && positionSize >= minCloseQuantity * 2m;
    }

    public static bool IsShort(string side) =>
        string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Short", StringComparison.OrdinalIgnoreCase);

    private static bool IsLong(string side) =>
        string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Long", StringComparison.OrdinalIgnoreCase);

    private static decimal MinimumRewardExitPercent(FuturesStrategyContext context) =>
        decimal.Max(0.6m, context.Settings.StopLossPercent);
}
