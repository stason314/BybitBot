using BybitGridBot.Domain;

namespace BybitGridBot.App;

public sealed class NYSweepReversalStrategy
{
    private readonly NySessionBreakoutOptions _options;
    private readonly PatternConfirmationEngine _patterns;

    public const string Name = "NYSweepReversalStrategy";

    public NYSweepReversalStrategy(NySessionBreakoutOptions options, PatternConfirmationEngine patterns)
    {
        _options = options;
        _patterns = patterns;
    }

    public StrategyCandidate? BuildCandidate(
        NyStrategyContext context,
        BreakoutClassifierResult breakout)
    {
        var candles = context.FiveMinuteCandles.OrderBy(candle => candle.OpenTime).ToArray();
        if (candles.Length < 2 || context.Range.Upper <= context.Range.Lower)
        {
            return null;
        }

        var signal = FindLatestSweep(context.Symbol, candles, context.Range.Upper, context.Range.Lower);
        if (signal is null)
        {
            return null;
        }

        if (breakout.Classification == BreakoutClassification.TrueBreakout &&
            ((breakout.BreakoutSide == StrategySide.Long && signal.Side == StrategySide.Short) ||
             (breakout.BreakoutSide == StrategySide.Short && signal.Side == StrategySide.Long)))
        {
            return Candidate(signal, 0m, 0m, [], "Rejected: true breakout protection.", StrategyNoTradeReason.TrueBreakoutProtection);
        }

        var rejection = ResolveRiskRejection(signal);
        if (rejection != StrategyNoTradeReason.None)
        {
            return Candidate(signal, 0m, 0m, [], $"Rejected by risk guard: {rejection}.", rejection);
        }

        var patternSignals = _patterns.Detect(candles, context.Range.Upper, context.Range.Lower);
        var sideConfirmations = patternSignals.Where(pattern => pattern.Side == signal.Side).ToArray();
        var score = 50m;
        score += signal.ReclaimPercent >= _options.MinReclaimPercent * 2m ? 10m : 0m;
        score += signal.SweepDepthPercent >= _options.MinSweepDepthPercent * 2m ? 10m : 0m;
        score += signal.MidlineRoomR >= _options.MinMidlineRoomR ? 10m : -15m;
        score += breakout.Classification == BreakoutClassification.FalseBreakout ? 20m : 0m;
        score += breakout.ScoreModifierForSweep;
        score += _patterns.ScoreModifierForStrategy(Name, signal.Side, patternSignals);
        score = TradingIndicatorMath.Clamp(score, 0m, 100m);
        var confidence = TradingIndicatorMath.Clamp(score / 100m, 0.1m, 0.95m);

        return Candidate(
            signal,
            score,
            confidence,
            sideConfirmations,
            $"{signal.Reason} Breakout={breakout.Classification}. {breakout.Reason}",
            StrategyNoTradeReason.None);
    }

    private StrategyNoTradeReason ResolveRiskRejection(SweepSignal signal)
    {
        if (signal.SweepDepthPercent < _options.MinSweepDepthPercent)
        {
            return StrategyNoTradeReason.LowScore;
        }

        if (signal.ReclaimPercent < _options.MinReclaimPercent)
        {
            return StrategyNoTradeReason.LowScore;
        }

        if (signal.StopDistancePercent < _options.MinStopPercent)
        {
            return StrategyNoTradeReason.StopTooSmall;
        }

        if (signal.StopDistancePercent > _options.MaxStopPercent)
        {
            return StrategyNoTradeReason.StopTooLarge;
        }

        if (signal.BreakoutVolumeRatio >= _options.HighBreakoutVolumeRatio)
        {
            return StrategyNoTradeReason.HighVolumeBreakout;
        }

        return StrategyNoTradeReason.None;
    }

    private StrategyCandidate Candidate(
        SweepSignal signal,
        decimal score,
        decimal confidence,
        IReadOnlyList<PatternSignal> confirmations,
        string reason,
        StrategyNoTradeReason rejection) => new()
    {
        StrategyName = Name,
        Symbol = signal.Symbol,
        Side = signal.Side,
        Score = score,
        Confidence = confidence,
        Reason = reason,
        PatternConfirmations = confirmations,
        RejectionReason = rejection,
        TradeIntent = rejection == StrategyNoTradeReason.None
            ? new StrategyTradeIntent
            {
                StrategyName = Name,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryType = StrategyEntryType.Market,
                EntryPrice = signal.EntryPrice,
                StopLoss = signal.StopLoss,
                TakeProfit = signal.TakeProfit,
                ExpectedR = _options.RewardRisk,
                Reason = reason
            }
            : null,
        CreatedAt = signal.SignalTime
    };

    private SweepSignal? FindLatestSweep(string symbol, IReadOnlyList<Candle> candles, decimal upper, decimal lower)
    {
        SweepSignal? latest = null;
        decimal? upperStop = null;
        DateTimeOffset? upperSweepAt = null;
        decimal? lowerStop = null;
        DateTimeOffset? lowerSweepAt = null;

        foreach (var candle in candles.Skip(1))
        {
            if (candle.High > upper && candle.Close < upper)
            {
                latest = Build(symbol, "Short", candle.OpenTime, upper, upper, lower, candle.Close, candle.High, "Swept upper boundary and closed back inside.", candles);
                upperStop = null;
                upperSweepAt = null;
            }
            else if (candle.High > upper && candle.Close > upper)
            {
                upperStop = upperStop is null ? candle.High : decimal.Max(upperStop.Value, candle.High);
                upperSweepAt = candle.OpenTime;
            }
            else if (upperStop is not null && upperSweepAt is not null && candle.OpenTime > upperSweepAt && candle.Close < upper)
            {
                latest = Build(symbol, "Short", candle.OpenTime, upper, upper, lower, candle.Close, upperStop.Value, "Swept upper boundary and reclaimed back inside.", candles);
                upperStop = null;
                upperSweepAt = null;
            }

            if (candle.Low < lower && candle.Close > lower)
            {
                latest = Build(symbol, "Long", candle.OpenTime, lower, upper, lower, candle.Close, candle.Low, "Swept lower boundary and closed back inside.", candles);
                lowerStop = null;
                lowerSweepAt = null;
            }
            else if (candle.Low < lower && candle.Close < lower)
            {
                lowerStop = lowerStop is null ? candle.Low : decimal.Min(lowerStop.Value, candle.Low);
                lowerSweepAt = candle.OpenTime;
            }
            else if (lowerStop is not null && lowerSweepAt is not null && candle.OpenTime > lowerSweepAt && candle.Close > lower)
            {
                latest = Build(symbol, "Long", candle.OpenTime, lower, upper, lower, candle.Close, lowerStop.Value, "Swept lower boundary and reclaimed back inside.", candles);
                lowerStop = null;
                lowerSweepAt = null;
            }
        }

        return latest;
    }

    private SweepSignal Build(
        string symbol,
        string side,
        DateTimeOffset signalTime,
        decimal boundary,
        decimal rangeHigh,
        decimal rangeLow,
        decimal entry,
        decimal stop,
        string reason,
        IReadOnlyList<Candle> candles)
    {
        var risk = Math.Abs(entry - stop);
        var takeProfit = side == "Short"
            ? entry - risk * _options.RewardRisk
            : entry + risk * _options.RewardRisk;
        return new SweepSignal
        {
            Symbol = symbol,
            Side = Enum.Parse<StrategySide>(side),
            SignalTime = signalTime,
            Boundary = boundary,
            EntryPrice = entry,
            StopLoss = stop,
            TakeProfit = takeProfit,
            ReclaimPercent = CalculateReclaimPercent(side, boundary, entry),
            SweepDepthPercent = CalculateSweepDepthPercent(side, boundary, stop),
            StopDistancePercent = entry > 0m ? risk / entry * 100m : 0m,
            MidlineRoomR = CalculateMidlineRoomR(side, rangeHigh, rangeLow, entry, risk),
            BreakoutVolumeRatio = CalculateBreakoutVolumeRatio(candles, signalTime),
            Reason = reason
        };
    }

    private static decimal CalculateReclaimPercent(string side, decimal boundary, decimal close) =>
        boundary <= 0m ? 0m : side == "Short" ? (boundary - close) / boundary * 100m : (close - boundary) / boundary * 100m;

    private static decimal CalculateSweepDepthPercent(string side, decimal boundary, decimal extreme) =>
        boundary <= 0m ? 0m : side == "Short" ? (extreme - boundary) / boundary * 100m : (boundary - extreme) / boundary * 100m;

    private static decimal CalculateMidlineRoomR(string side, decimal high, decimal low, decimal entry, decimal risk)
    {
        if (risk <= 0m || high <= low)
        {
            return 0m;
        }

        var mid = (high + low) / 2m;
        var room = side == "Short" ? entry - mid : mid - entry;
        return room > 0m ? room / risk : 0m;
    }

    private static decimal CalculateBreakoutVolumeRatio(IReadOnlyList<Candle> candles, DateTimeOffset time)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var candle = ordered.LastOrDefault(item => item.OpenTime == time);
        if (candle is null)
        {
            return 0m;
        }

        var average = ordered.Where(item => item.OpenTime < time).TakeLast(20).DefaultIfEmpty(candle).Average(item => item.Volume);
        return average > 0m ? candle.Volume / average : 0m;
    }

    private sealed class SweepSignal
    {
        public string Symbol { get; init; } = string.Empty;
        public StrategySide Side { get; init; }
        public DateTimeOffset SignalTime { get; init; }
        public decimal Boundary { get; init; }
        public decimal EntryPrice { get; init; }
        public decimal StopLoss { get; init; }
        public decimal TakeProfit { get; init; }
        public decimal ReclaimPercent { get; init; }
        public decimal SweepDepthPercent { get; init; }
        public decimal StopDistancePercent { get; init; }
        public decimal MidlineRoomR { get; init; }
        public decimal BreakoutVolumeRatio { get; init; }
        public string Reason { get; init; } = string.Empty;
    }
}
