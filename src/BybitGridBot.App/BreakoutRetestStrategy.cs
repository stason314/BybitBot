using BybitGridBot.Domain;

namespace BybitGridBot.App;

public sealed class BreakoutRetestStrategy
{
    public const string Name = "BreakoutRetestStrategy";

    public StrategyCandidate? BuildCandidate(NyStrategyContext context, BreakoutClassifierResult breakout)
    {
        if (breakout.Classification != BreakoutClassification.TrueBreakout ||
            breakout.BreakoutSide == StrategySide.None)
        {
            return null;
        }

        var candles = context.FiveMinuteCandles.OrderBy(candle => candle.OpenTime).ToArray();
        if (candles.Length < 3)
        {
            return null;
        }

        var current = candles[^1];
        var side = breakout.BreakoutSide;
        var boundary = side == StrategySide.Long ? context.Range.Upper : context.Range.Lower;
        var retested = side == StrategySide.Long
            ? current.Low <= boundary && current.Close > boundary
            : current.High >= boundary && current.Close < boundary;
        if (!retested)
        {
            return null;
        }

        var stop = side == StrategySide.Long
            ? candles.TakeLast(3).Min(candle => candle.Low)
            : candles.TakeLast(3).Max(candle => candle.High);
        var risk = Math.Abs(current.Close - stop);
        if (risk <= 0m)
        {
            return null;
        }

        var takeProfit = side == StrategySide.Long
            ? current.Close + risk * 2m
            : current.Close - risk * 2m;
        return new StrategyCandidate
        {
            StrategyName = Name,
            Symbol = context.Symbol,
            Side = side,
            Score = 72m,
            Confidence = 0.62m,
            Reason = $"Breakout retest after true breakout. Boundary={boundary:F8}.",
            TradeIntent = new StrategyTradeIntent
            {
                StrategyName = Name,
                Symbol = context.Symbol,
                Side = side,
                EntryType = StrategyEntryType.FixedRetest,
                EntryPrice = current.Close,
                StopLoss = stop,
                TakeProfit = takeProfit,
                ExpectedR = 2m,
                Reason = $"Retested breakout boundary {boundary:F8}."
            },
            CreatedAt = current.OpenTime
        };
    }
}

public sealed class PauseStrategy
{
    public const string Name = "PauseStrategy";

    public StrategyDecision Decide(StrategyNoTradeReason reason, string details, IReadOnlyList<StrategyCandidate> candidates) => new()
    {
        SelectedStrategy = Name,
        AllCandidates = candidates,
        RejectedCandidates = candidates.Where(candidate => candidate.RejectionReason != StrategyNoTradeReason.None).ToArray(),
        NoTradeReason = reason,
        Reason = details,
        IsTradeAllowed = false
    };
}
