using BybitGridBot.Domain;

namespace BybitGridBot.App;

public sealed class TurtleTrendStrategy
{
    private readonly TurtleTrendOptions _options;
    private readonly PatternConfirmationEngine _patterns;

    public const string Name = "TurtleTrendStrategy";

    public TurtleTrendStrategy(TurtleTrendOptions options, PatternConfirmationEngine patterns)
    {
        _options = options;
        _patterns = patterns;
    }

    public StrategyCandidate? BuildCandidate(
        NyStrategyContext context,
        BreakoutClassifierResult breakout)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var snapshot = context.TurtleIndicators;
        Candle[] candles = snapshot is null
            ? context.TurtleCandles.OrderBy(candle => candle.OpenTime).ToArray()
            : [];
        var minCandles = Math.Max(_options.EntrySlowPeriod, _options.AtrPeriod) + 1;
        if (snapshot is null && candles.Length < minCandles)
        {
            return null;
        }

        var current = snapshot?.Current ?? candles[^1];
        var signal = snapshot is not null
            ? ResolveSignal(snapshot, current)
            : ResolveSignal(candles, current);
        if (signal.Side == StrategySide.None)
        {
            return null;
        }

        var nValue = snapshot?.TurtleN ?? TradingIndicatorMath.TurtleN(candles, _options.AtrPeriod);
        if (nValue <= 0m)
        {
            return null;
        }

        var stopLoss = signal.Side == StrategySide.Long
            ? current.Close - nValue * _options.StopAtrMultiplier
            : current.Close + nValue * _options.StopAtrMultiplier;
        var risk = Math.Abs(current.Close - stopLoss);
        if (risk <= 0m)
        {
            return null;
        }

        var channelExit = ResolveChannelExit(candles, snapshot, signal);
        var score = signal.System == "S2" ? 100m : 90m;
        var confidence = signal.System == "S2" ? 0.95m : 0.9m;
        var reason = $"Turtle {signal.System} Donchian {signal.Side} breakout. Close={current.Close:F8}, breakoutLevel={signal.BreakoutLevel:F8}, N={nValue:F8}, channelExit={channelExit:F8}.";
        return new StrategyCandidate
        {
            StrategyName = Name,
            Symbol = context.Symbol,
            Side = signal.Side,
            Score = score,
            Confidence = confidence,
            Reason = reason,
            PatternConfirmations = [],
            RejectionReason = StrategyNoTradeReason.None,
            TradeIntent = new StrategyTradeIntent
            {
                StrategyName = Name,
                Symbol = context.Symbol,
                Side = signal.Side,
                EntryType = StrategyEntryType.DonchianBreakout,
                EntryPrice = current.Close,
                StopLoss = stopLoss,
                TakeProfit = null,
                ExpectedR = 0m,
                TurtleSystem = signal.System,
                TurtleN = nValue,
                TurtleBreakoutLevel = signal.BreakoutLevel,
                Reason = reason
            },
            CreatedAt = current.OpenTime
        };
    }

    private TurtleSignalCandidate ResolveSignal(IReadOnlyList<Candle> candles, Candle current)
    {
        var slowHigh = TradingIndicatorMath.DonchianHigh(candles, _options.EntrySlowPeriod);
        var slowLow = TradingIndicatorMath.DonchianLow(candles, _options.EntrySlowPeriod);
        if (slowHigh > 0m && current.Close > slowHigh)
        {
            return new TurtleSignalCandidate("S2", StrategySide.Long, slowHigh);
        }

        if (slowLow > 0m && current.Close < slowLow)
        {
            return new TurtleSignalCandidate("S2", StrategySide.Short, slowLow);
        }

        var fastHigh = TradingIndicatorMath.DonchianHigh(candles, _options.EntryFastPeriod);
        var fastLow = TradingIndicatorMath.DonchianLow(candles, _options.EntryFastPeriod);
        if (fastHigh > 0m && current.Close > fastHigh)
        {
            return new TurtleSignalCandidate("S1", StrategySide.Long, fastHigh);
        }

        if (fastLow > 0m && current.Close < fastLow)
        {
            return new TurtleSignalCandidate("S1", StrategySide.Short, fastLow);
        }

        return new TurtleSignalCandidate(string.Empty, StrategySide.None, 0m);
    }

    private TurtleSignalCandidate ResolveSignal(TurtleIndicatorSnapshot snapshot, Candle current)
    {
        if (snapshot.EntrySlowHigh > 0m && current.Close > snapshot.EntrySlowHigh)
        {
            return new TurtleSignalCandidate("S2", StrategySide.Long, snapshot.EntrySlowHigh);
        }

        if (snapshot.EntrySlowLow > 0m && current.Close < snapshot.EntrySlowLow)
        {
            return new TurtleSignalCandidate("S2", StrategySide.Short, snapshot.EntrySlowLow);
        }

        if (snapshot.EntryFastHigh > 0m && current.Close > snapshot.EntryFastHigh)
        {
            return new TurtleSignalCandidate("S1", StrategySide.Long, snapshot.EntryFastHigh);
        }

        if (snapshot.EntryFastLow > 0m && current.Close < snapshot.EntryFastLow)
        {
            return new TurtleSignalCandidate("S1", StrategySide.Short, snapshot.EntryFastLow);
        }

        return new TurtleSignalCandidate(string.Empty, StrategySide.None, 0m);
    }

    private decimal ResolveChannelExit(
        IReadOnlyList<Candle> candles,
        TurtleIndicatorSnapshot? snapshot,
        TurtleSignalCandidate signal)
    {
        if (snapshot is not null)
        {
            if (signal.Side == StrategySide.Long)
            {
                return signal.System == "S2" ? snapshot.ExitSlowLow : snapshot.ExitFastLow;
            }

            return signal.System == "S2" ? snapshot.ExitSlowHigh : snapshot.ExitFastHigh;
        }

        var exitPeriod = signal.System == "S2" ? _options.ExitSlowPeriod : _options.ExitFastPeriod;
        return signal.Side == StrategySide.Long
            ? TradingIndicatorMath.DonchianLow(candles, exitPeriod)
            : TradingIndicatorMath.DonchianHigh(candles, exitPeriod);
    }

    private readonly record struct TurtleSignalCandidate(string System, StrategySide Side, decimal BreakoutLevel);
}
