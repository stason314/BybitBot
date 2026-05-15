using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class PriceActionPhaseDetector
{
    private readonly MarketRegimeFilter _marketRegimeFilter;

    public PriceActionPhaseDetector()
        : this(new MarketRegimeFilter())
    {
    }

    public PriceActionPhaseDetector(MarketRegimeFilter marketRegimeFilter)
    {
        _marketRegimeFilter = marketRegimeFilter;
    }

    public MarketPhaseResult Detect(
        GridOptions options,
        decimal currentPrice,
        IReadOnlyCollection<Candle> symbolCandles,
        IReadOnlyCollection<Candle> btcCandles,
        DateTimeOffset? detectedAt = null)
    {
        var now = detectedAt ?? DateTimeOffset.UtcNow;
        var candles = symbolCandles.OrderBy(candle => candle.OpenTime).ToArray();
        var minimumCandles = Math.Max(
            Math.Max(options.EmaSlow, options.TrendEmaSlow) + 5,
            Math.Max(options.AdxPeriod + 2, Math.Max(options.AtrPeriod * 3, options.VolumeSmaPeriod + 2)));

        if (currentPrice <= 0m || candles.Length < minimumCandles)
        {
            return MarketPhaseResult.Unknown("Not enough candles to detect market phase.", now);
        }

        var fastPeriod = options.EmaFast > 0 ? options.EmaFast : options.TrendEmaFast;
        var slowPeriod = options.EmaSlow > 0 ? options.EmaSlow : options.TrendEmaSlow;
        var emaFast = MarketRegimeDetector.CalculateEma(candles, fastPeriod);
        var emaSlow = MarketRegimeDetector.CalculateEma(candles, slowPeriod);
        var adx = _marketRegimeFilter.CalculateAdx(candles, options.AdxPeriod);
        var atr = MarketRegimeDetector.CalculateAtr(candles, options.AtrPeriod);
        var baselineAtr = CalculateBaselineAtr(candles, options.AtrPeriod);
        var volumeSma = candles.TakeLast(options.VolumeSmaPeriod).Average(candle => candle.Volume);
        var last = candles[^1];
        var lastMovePercent = PercentMove(last.Open, last.Close);
        var lastBodyPercent = last.Open > 0m ? Math.Abs(last.Close - last.Open) / last.Open * 100m : 0m;
        var closeLocation = last.High > last.Low ? (last.Close - last.Low) / (last.High - last.Low) : 0.5m;
        var recent = candles.TakeLast(Math.Max(options.DumpLookbackCandles, options.StrategyConfirmationCandles)).ToArray();
        var lookbackMovePercent = recent.Length == 0 ? 0m : PercentMove(recent[0].Open, recent[^1].Close);
        var analysisWindow = candles.TakeLast(Math.Max(30, options.VolumeSmaPeriod)).ToArray();
        var support = Math.Min(options.LowerPrice, analysisWindow.Min(candle => candle.Low));
        var resistance = Math.Max(options.UpperPrice, analysisWindow.Max(candle => candle.High));
        var previousResistance = analysisWindow.Length > 1 ? analysisWindow.Take(analysisWindow.Length - 1).Max(candle => candle.High) : resistance;
        var previousSupport = analysisWindow.Length > 1 ? analysisWindow.Take(analysisWindow.Length - 1).Min(candle => candle.Low) : support;
        var volumeSpike = volumeSma > 0m && last.Volume >= volumeSma * options.BreakoutVolumeMultiplier;
        var btcRiskOff = HasBtcRiskOff(btcCandles, options.BtcLookbackCandles, options.BtcMaxMovePercent);
        var atrSpike = baselineAtr > 0m && atr >= baselineAtr * options.AtrSpikeMultiplier;
        var distanceFromFastEma = PercentDistance(currentPrice, emaFast);
        var distanceFromSlowEma = PercentDistance(currentPrice, emaSlow);
        var rsi = CalculateRsi(candles, 14);
        var keyLevels = new Dictionary<string, decimal>
        {
            ["emaFast"] = decimal.Round(emaFast, 8, MidpointRounding.AwayFromZero),
            ["emaSlow"] = decimal.Round(emaSlow, 8, MidpointRounding.AwayFromZero),
            ["adx"] = decimal.Round(adx, 4, MidpointRounding.AwayFromZero),
            ["atr"] = decimal.Round(atr, 8, MidpointRounding.AwayFromZero),
            ["support"] = decimal.Round(support, 8, MidpointRounding.AwayFromZero),
            ["resistance"] = decimal.Round(resistance, 8, MidpointRounding.AwayFromZero),
            ["volumeRatio"] = volumeSma > 0m ? decimal.Round(last.Volume / volumeSma, 4, MidpointRounding.AwayFromZero) : 0m,
            ["rsi"] = decimal.Round(rsi, 4, MidpointRounding.AwayFromZero)
        };

        var isBigRed = lastMovePercent <= -options.BigRedCandlePercent && lastBodyPercent >= options.BigRedCandlePercent * 0.45m;
        var isLookbackDump = lookbackMovePercent <= -options.DumpMovePercent;
        if ((isBigRed || isLookbackDump) && closeLocation <= 0.35m && (volumeSpike || volumeSma <= 0m || btcRiskOff))
        {
            return Build(MarketPhase.Dump, 0.9m, 92m, $"Dump detected. Last candle: {lastMovePercent:F2}%, lookback: {lookbackMovePercent:F2}%.", now, keyLevels);
        }

        if (atrSpike || (options.BtcFilterEnabled && btcRiskOff))
        {
            return Build(MarketPhase.HighVolatility, 0.78m, 78m, "ATR spike or BTC risk-off detected.", now, keyLevels);
        }

        var risingSwingLows = HasRisingSwingLows(candles.TakeLast(18).ToArray());
        var inUptrend = emaFast > emaSlow && currentPrice >= emaSlow && adx >= options.TrendAdxMin && risingSwingLows && !btcRiskOff;
        var pullbackToEma = inUptrend &&
                            currentPrice <= emaFast * (1m + options.BtdMaxDistanceFromEmaPercent / 100m) &&
                            currentPrice >= emaSlow * (1m - options.BtdMaxDistanceFromEmaPercent / 100m) &&
                            lastMovePercent > -options.BigRedCandlePercent;

        if (currentPrice > previousResistance &&
            last.Close > previousResistance &&
            volumeSpike &&
            adx >= options.BreakoutAdxMin &&
            HasCloseConfirmation(candles, previousResistance, options.BreakoutConfirmationCandles, above: true) &&
            Math.Min(distanceFromFastEma, distanceFromSlowEma) <= options.BreakoutMaxDistanceFromEmaPercent)
        {
            return Build(MarketPhase.BreakoutUp, 0.82m, 86m, "Confirmed close above resistance with volume.", now, keyLevels);
        }

        if (currentPrice < previousSupport &&
            last.Close < previousSupport &&
            volumeSpike &&
            adx >= options.BreakoutAdxMin &&
            HasCloseConfirmation(candles, previousSupport, options.BreakoutConfirmationCandles, above: false))
        {
            return Build(MarketPhase.BreakoutDown, 0.82m, 86m, "Confirmed close below support with volume.", now, keyLevels);
        }

        if (inUptrend &&
            (Math.Min(distanceFromFastEma, distanceFromSlowEma) > options.TrendMaxDistanceFromEmaPercent || rsi >= 75m) &&
            (lastMovePercent < 0m || volumeSpike))
        {
            return Build(MarketPhase.Exhaustion, 0.72m, 72m, "Extended uptrend with overbought or red-volume pullback.", now, keyLevels);
        }

        if (pullbackToEma)
        {
            return Build(MarketPhase.PullbackInUptrend, 0.74m, 76m, "Uptrend pullback near EMA support.", now, keyLevels);
        }

        if (inUptrend)
        {
            return Build(MarketPhase.Uptrend, 0.76m, 80m, "EMA20 above EMA50, ADX confirms trend, swing lows rising.", now, keyLevels);
        }

        if (adx <= options.RangeAdxMax &&
            currentPrice >= options.LowerPrice &&
            currentPrice <= options.UpperPrice &&
            !volumeSpike &&
            IsAtrStable(atr, baselineAtr))
        {
            return Build(MarketPhase.RangeBound, 0.7m, 72m, "ADX low, price inside configured range, volume normal.", now, keyLevels);
        }

        return Build(MarketPhase.Unknown, 0.35m, 35m, "No reliable phase match.", now, keyLevels);
    }

    private static MarketPhaseResult Build(
        MarketPhase phase,
        decimal confidence,
        decimal score,
        string reason,
        DateTimeOffset detectedAt,
        IReadOnlyDictionary<string, decimal> keyLevels)
    {
        return new MarketPhaseResult
        {
            Phase = phase,
            Confidence = Clamp(confidence, 0m, 1m),
            Score = Clamp(score, 0m, 100m),
            Reason = reason,
            DetectedAt = detectedAt,
            KeyLevels = keyLevels,
            SuggestedStrategy = SuggestedStrategyFor(phase)
        };
    }

    private static StrategyType SuggestedStrategyFor(MarketPhase phase)
    {
        return phase switch
        {
            MarketPhase.Uptrend => StrategyType.TrendFollowing,
            MarketPhase.PullbackInUptrend => StrategyType.Btd,
            MarketPhase.RangeBound => StrategyType.Grid,
            MarketPhase.BreakoutUp => StrategyType.Breakout,
            _ => StrategyType.Pause
        };
    }

    private static decimal PercentMove(decimal from, decimal to) => from <= 0m ? 0m : (to - from) / from * 100m;

    private static decimal Clamp(decimal value, decimal min, decimal max)
    {
        return value < min ? min : value > max ? max : value;
    }

    private static decimal PercentDistance(decimal value, decimal reference)
    {
        return reference <= 0m ? 0m : Math.Abs(value - reference) / reference * 100m;
    }

    private static bool HasBtcRiskOff(IReadOnlyCollection<Candle> btcCandles, int lookbackCandles, decimal maxMovePercent)
    {
        var slice = btcCandles.OrderBy(candle => candle.OpenTime).TakeLast(Math.Max(1, lookbackCandles)).ToArray();
        if (slice.Length < Math.Max(1, lookbackCandles) || slice[0].Open <= 0m)
        {
            return false;
        }

        return PercentMove(slice[0].Open, slice[^1].Close) <= -Math.Abs(maxMovePercent);
    }

    private static decimal CalculateBaselineAtr(IReadOnlyList<Candle> candles, int period)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < period * 3)
        {
            return 0m;
        }

        return MarketRegimeDetector.CalculateAtr(ordered.Take(ordered.Length - period).TakeLast(period * 2 + 1).ToArray(), period);
    }

    private static bool HasRisingSwingLows(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 9)
        {
            return false;
        }

        var third = candles.Count / 3;
        var first = candles.Take(third).Min(candle => candle.Low);
        var second = candles.Skip(third).Take(third).Min(candle => candle.Low);
        var thirdLow = candles.Skip(third * 2).Min(candle => candle.Low);
        return second >= first && thirdLow >= second;
    }

    private static bool HasCloseConfirmation(IReadOnlyList<Candle> candles, decimal level, int confirmationCandles, bool above)
    {
        var slice = candles.TakeLast(Math.Max(1, confirmationCandles)).ToArray();
        return slice.Length >= Math.Max(1, confirmationCandles) &&
               slice.All(candle => above ? candle.Close > level : candle.Close < level);
    }

    private static bool IsAtrStable(decimal atr, decimal baselineAtr)
    {
        return baselineAtr <= 0m || atr <= baselineAtr * 1.4m;
    }

    private static decimal CalculateRsi(IReadOnlyList<Candle> candles, int period)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < period + 1)
        {
            return 50m;
        }

        var gains = new List<decimal>();
        var losses = new List<decimal>();
        for (var index = 1; index < ordered.Length; index++)
        {
            var change = ordered[index].Close - ordered[index - 1].Close;
            gains.Add(Math.Max(change, 0m));
            losses.Add(Math.Max(-change, 0m));
        }

        var averageGain = gains.TakeLast(period).Average();
        var averageLoss = losses.TakeLast(period).Average();
        if (averageLoss <= 0m)
        {
            return 100m;
        }

        var rs = averageGain / averageLoss;
        return 100m - (100m / (1m + rs));
    }
}
