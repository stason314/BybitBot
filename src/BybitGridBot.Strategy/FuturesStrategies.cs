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
        return context.Position.Size > 0m
            ? new FuturesStrategyDecision
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
            }
            : FuturesStrategyDecision.Empty("No futures position to reduce.");
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

        if (FuturesLongOnlySignals.ShouldCloseOpenLong(context, signal))
        {
            return FuturesLongOnlySignals.CloseLong(context, "exit-signal");
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

        if (FuturesLongOnlySignals.ShouldCloseOpenLong(context, signal))
        {
            return FuturesLongOnlySignals.CloseLong(context, "exit-signal");
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

        if (FuturesLongOnlySignals.ShouldCloseOpenLong(context, signal))
        {
            return FuturesLongOnlySignals.CloseLong(context, "exit-signal");
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

    public static bool ShouldCloseOpenLong(FuturesStrategyContext context, FuturesLongOnlySignal signal)
    {
        if (context.Position.Size <= 0m)
        {
            return false;
        }

        var stopPrice = context.Position.EntryPrice * (1m - context.Settings.StopLossPercent / 100m);
        var takeProfitPrice = context.Position.EntryPrice * (1m + context.Settings.TakeProfitPercent / 100m);
        return context.CurrentPrice <= stopPrice ||
            context.CurrentPrice >= takeProfitPrice ||
            signal.MovePercent < -1m;
    }

    public static FuturesStrategyDecision OpenLong(FuturesStrategyContext context)
    {
        var entryIntent = FuturesStrategyIntentFactory.OpenLong(
            context.Settings,
            context.CurrentPrice,
            context.Instrument);
        return entryIntent.Quantity > 0m
            ? new FuturesStrategyDecision { TradeIntents = [entryIntent] }
            : FuturesStrategyDecision.Empty("Futures entry quantity is below instrument precision.");
    }

    public static FuturesStrategyDecision CloseLong(FuturesStrategyContext context, string reason) => new()
    {
        TradeIntents =
        [
            FuturesStrategyIntentFactory.CloseLong(
                context.Settings,
                context.Position,
                context.CurrentPrice,
                context.Instrument,
                reason)
        ]
    };
}

internal readonly record struct FuturesLongOnlySignal(
    decimal MovePercent,
    decimal Support,
    decimal Resistance,
    decimal Range);
