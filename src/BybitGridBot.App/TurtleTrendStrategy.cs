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

        var candles = context.TurtleCandles.OrderBy(candle => candle.OpenTime).ToArray();
        var minCandles = Math.Max(_options.EntrySlowPeriod, _options.AtrPeriod) + 1;
        if (candles.Length < minCandles)
        {
            return null;
        }

        var current = candles[^1];
        var signal = ResolveSignal(candles, current);
        if (signal.Side == StrategySide.None)
        {
            return null;
        }

        var nValue = TradingIndicatorMath.TurtleN(candles, _options.AtrPeriod);
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

        var exitPeriod = signal.System == "S2" ? _options.ExitSlowPeriod : _options.ExitFastPeriod;
        var channelExit = signal.Side == StrategySide.Long
            ? TradingIndicatorMath.DonchianLow(candles, exitPeriod)
            : TradingIndicatorMath.DonchianHigh(candles, exitPeriod);
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

    private readonly record struct TurtleSignalCandidate(string System, StrategySide Side, decimal BreakoutLevel);
}
