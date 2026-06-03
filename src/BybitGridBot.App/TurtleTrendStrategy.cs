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
        var minCandles = Math.Max(_options.EntryFastPeriod, Math.Max(_options.AtrPeriod, 50)) + 1;
        if (candles.Length < minCandles)
        {
            return null;
        }

        var current = candles[^1];
        var donchianHigh = TradingIndicatorMath.DonchianHigh(candles, _options.EntryFastPeriod);
        var donchianLow = TradingIndicatorMath.DonchianLow(candles, _options.EntryFastPeriod);
        var side = StrategySide.None;
        if (donchianHigh > 0m && current.Close > donchianHigh)
        {
            side = StrategySide.Long;
        }
        else if (donchianLow > 0m && current.Close < donchianLow)
        {
            side = StrategySide.Short;
        }

        if (side == StrategySide.None)
        {
            return null;
        }

        var closes = candles.Select(candle => candle.Close).ToArray();
        var ema20 = TradingIndicatorMath.Ema(closes, 20);
        var ema50 = TradingIndicatorMath.Ema(closes, 50);
        var emaConfirmed = side == StrategySide.Long ? ema20 >= ema50 : ema20 <= ema50;
        var adx = TradingIndicatorMath.Adx(candles, 14);
        var volumeSma = TradingIndicatorMath.VolumeSma(candles.SkipLast(1).ToArray(), 20);
        var volumeConfirmed = !_options.RequireVolumeConfirmation ||
            volumeSma <= 0m ||
            current.Volume >= volumeSma * _options.VolumeMultiplier;
        var btcRejected = _options.UseBtcFilter && IsBtcAgainst(context.BtcFifteenMinuteCandles, side);
        var atr = TradingIndicatorMath.Atr(candles, _options.AtrPeriod);
        if (atr <= 0m)
        {
            return null;
        }

        var stopLoss = side == StrategySide.Long
            ? current.Close - atr * _options.StopAtrMultiplier
            : current.Close + atr * _options.StopAtrMultiplier;
        var risk = Math.Abs(current.Close - stopLoss);
        if (risk <= 0m)
        {
            return null;
        }

        var score = 50m;
        score += adx >= _options.MinAdx ? 15m : -25m;
        score += emaConfirmed ? 10m : -20m;
        score += volumeConfirmed ? 10m : -15m;
        score += breakout.Classification == BreakoutClassification.TrueBreakout && breakout.BreakoutSide == side ? 25m : 0m;
        score += breakout.ScoreModifierForTurtle;
        var patternSignals = _patterns.Detect(context.FiveMinuteCandles, context.Range.Upper, context.Range.Lower);
        score += _patterns.ScoreModifierForStrategy(Name, side, patternSignals);
        score = TradingIndicatorMath.Clamp(score, 0m, 100m);
        var confidence = TradingIndicatorMath.Clamp(score / 100m, 0.1m, 0.95m);
        var rejection = ResolveRejection(adx, volumeConfirmed, emaConfirmed, btcRejected);

        var channelExit = side == StrategySide.Long
            ? TradingIndicatorMath.DonchianLow(candles, _options.ExitFastPeriod)
            : TradingIndicatorMath.DonchianHigh(candles, _options.ExitFastPeriod);
        var reason = $"Donchian {side} breakout. Close={current.Close:F8}, entryHigh={donchianHigh:F8}, entryLow={donchianLow:F8}, ATR={atr:F8}, ADX={adx:F2}, channelExit={channelExit:F8}.";
        return new StrategyCandidate
        {
            StrategyName = Name,
            Symbol = context.Symbol,
            Side = side,
            Score = rejection == StrategyNoTradeReason.None ? score : 0m,
            Confidence = rejection == StrategyNoTradeReason.None ? confidence : 0m,
            Reason = rejection == StrategyNoTradeReason.None ? reason : $"Rejected: {rejection}. {reason}",
            PatternConfirmations = patternSignals.Where(pattern => pattern.Side == side).ToArray(),
            RejectionReason = rejection,
            TradeIntent = rejection == StrategyNoTradeReason.None
                ? new StrategyTradeIntent
                {
                    StrategyName = Name,
                    Symbol = context.Symbol,
                    Side = side,
                    EntryType = StrategyEntryType.DonchianBreakout,
                    EntryPrice = current.Close,
                    StopLoss = stopLoss,
                    TakeProfit = _options.UseFixedTakeProfit
                        ? side == StrategySide.Long
                            ? current.Close + risk * 2m
                            : current.Close - risk * 2m
                        : null,
                    ExpectedR = _options.UseFixedTakeProfit ? 2m : 0m,
                    Reason = reason
                }
                : null,
            CreatedAt = current.OpenTime
        };
    }

    private StrategyNoTradeReason ResolveRejection(decimal adx, bool volumeConfirmed, bool emaConfirmed, bool btcRejected)
    {
        if (adx < _options.MinAdx)
        {
            return StrategyNoTradeReason.LowScore;
        }

        if (!volumeConfirmed)
        {
            return StrategyNoTradeReason.LowScore;
        }

        if (!emaConfirmed)
        {
            return StrategyNoTradeReason.LowScore;
        }

        if (btcRejected)
        {
            return StrategyNoTradeReason.BtcFilterRejected;
        }

        return StrategyNoTradeReason.None;
    }

    private static bool IsBtcAgainst(IReadOnlyList<Candle> btcCandles, StrategySide side)
    {
        var closed = btcCandles.OrderBy(candle => candle.OpenTime).TakeLast(12).ToArray();
        if (closed.Length < 6)
        {
            return false;
        }

        var movePercent = closed[0].Open > 0m ? (closed[^1].Close - closed[0].Open) / closed[0].Open * 100m : 0m;
        return side == StrategySide.Long && movePercent < -0.8m ||
            side == StrategySide.Short && movePercent > 0.8m;
    }
}
