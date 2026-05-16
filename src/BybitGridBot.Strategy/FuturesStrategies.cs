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

        if (FuturesLongOnlySignals.ShouldOpenAggressiveTestLong(context, signal))
        {
            return FuturesLongOnlySignals.OpenLong(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty("Existing futures long remains open.");
        }

        return signal.MovePercent >= 0.8m
            ? FuturesLongOnlySignals.OpenLong(context)
            : FuturesStrategyDecision.Empty("No futures trend-follow long entry signal.");
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

        if (FuturesLongOnlySignals.ShouldOpenAggressiveTestLong(context, signal))
        {
            return FuturesLongOnlySignals.OpenLong(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty("Existing futures long remains open.");
        }

        return context.CurrentPrice >= signal.Resistance
            ? FuturesLongOnlySignals.OpenLong(context)
            : FuturesStrategyDecision.Empty("No futures breakout long entry signal.");
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

        if (FuturesLongOnlySignals.ShouldOpenAggressiveTestLong(context, signal))
        {
            return FuturesLongOnlySignals.OpenLong(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty("Existing futures long remains open.");
        }

        var entryBand = signal.Support + (signal.Range * 0.35m);
        return context.CurrentPrice <= entryBand
            ? FuturesLongOnlySignals.OpenLong(context)
            : FuturesStrategyDecision.Empty("No futures grid long entry signal.");
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

        if (FuturesShortOnlySignals.ShouldOpenAggressiveTestShort(context, signal))
        {
            return FuturesShortOnlySignals.OpenShort(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty("Existing futures short remains open.");
        }

        return signal.MovePercent <= -0.8m
            ? FuturesShortOnlySignals.OpenShort(context)
            : FuturesStrategyDecision.Empty("No futures trend-follow short entry signal.");
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

        if (FuturesShortOnlySignals.ShouldOpenAggressiveTestShort(context, signal))
        {
            return FuturesShortOnlySignals.OpenShort(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty("Existing futures short remains open.");
        }

        return context.CurrentPrice <= signal.Support
            ? FuturesShortOnlySignals.OpenShort(context)
            : FuturesStrategyDecision.Empty("No futures breakdown short entry signal.");
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

        if (FuturesShortOnlySignals.ShouldOpenAggressiveTestShort(context, signal))
        {
            return FuturesShortOnlySignals.OpenShort(context);
        }

        if (context.Position.Size > 0m && !context.Settings.AggressiveModeEnabled)
        {
            return FuturesStrategyDecision.Empty("Existing futures short remains open.");
        }

        var entryBand = signal.Resistance - (signal.Range * 0.35m);
        return context.CurrentPrice >= entryBand
            ? FuturesShortOnlySignals.OpenShort(context)
            : FuturesStrategyDecision.Empty("No futures grid short entry signal.");
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
            reason = "take-profit";
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

        var profitPercent = (context.CurrentPrice - context.Position.EntryPrice) / context.Position.EntryPrice * 100m;
        var retracementFromResistance = signal.Resistance > 0m
            ? (signal.Resistance - context.CurrentPrice) / signal.Resistance * 100m
            : 0m;
        var adaptiveTakeProfit = decimal.Min(context.Settings.TakeProfitPercent, 1.2m);
        return profitPercent >= adaptiveTakeProfit ||
            (profitPercent >= 0.6m && retracementFromResistance >= 0.35m);
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

        if (FuturesLongOnlySignals.TryAnalyze(context, out var signal) &&
            IsLongScaleInAfterRejection(context, signal))
        {
            return FuturesStrategyDecision.Empty("Futures long scale-in blocked after rejection from resistance.");
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

    private static bool IsLongScaleInAfterRejection(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        var range = signal.Range <= 0m ? context.Instrument.TickSize : signal.Range;
        var rejectionFromResistance = signal.Resistance - context.CurrentPrice;
        return context.Position.Size > 0m &&
            context.CurrentPrice < signal.Resistance - range * 0.35m &&
            rejectionFromResistance / context.CurrentPrice * 100m >= 0.35m;
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
            reason = "take-profit";
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

        var profitPercent = (context.Position.EntryPrice - context.CurrentPrice) / context.Position.EntryPrice * 100m;
        var reboundFromSupport = signal.Support > 0m
            ? (context.CurrentPrice - signal.Support) / signal.Support * 100m
            : 0m;
        var adaptiveTakeProfit = decimal.Min(context.Settings.TakeProfitPercent, 1.2m);
        return profitPercent >= adaptiveTakeProfit ||
            (profitPercent >= 0.6m && reboundFromSupport >= 0.35m);
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

        if (TryAnalyze(context, out var signal) &&
            IsShortScaleInAfterRebound(context, signal))
        {
            return FuturesStrategyDecision.Empty("Futures short scale-in blocked after rebound from support.");
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
}
