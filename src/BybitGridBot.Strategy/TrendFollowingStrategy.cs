using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class TrendFollowingStrategy : ITradingStrategy
{
    public TradingStrategyType Type => TradingStrategyType.TrendFollowing;

    public string DisplayName => "Trend Following";

    public StrategyScore Score(MarketRegime regime, GridOptions options)
    {
        var isAllowed = regime == MarketRegime.Uptrend;
        return new StrategyScore
        {
            StrategyType = StrategyType.TrendFollowing,
            Score = isAllowed ? 78m : 25m,
            Confidence = isAllowed ? 0.72m : 0.25m,
            RequiredCapitalPercent = options.TrendCapitalPercent,
            IsAllowed = isAllowed,
            Reason = isAllowed ? "Uptrend regime supports trend-following long exposure." : "Trend-following is disabled outside uptrend."
        };
    }

    public bool CanEnter(
        GridOptions options,
        MarketPhaseResult phase,
        IReadOnlyCollection<Candle> candles,
        decimal currentPrice,
        IReadOnlyCollection<Candle> btcCandles)
    {
        if (phase.Phase != MarketPhase.Uptrend)
        {
            return false;
        }

        if (IsBtcRiskOff(btcCandles, options.BtcLookbackCandles, options.BtcMaxMovePercent))
        {
            return false;
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < Math.Max(options.EmaSlow, options.AdxPeriod) + 2)
        {
            return false;
        }

        var emaFast = MarketRegimeDetector.CalculateEma(ordered, options.EmaFast);
        var emaSlow = MarketRegimeDetector.CalculateEma(ordered, options.EmaSlow);
        var distanceFromEma = Math.Min(PercentDistance(currentPrice, emaFast), PercentDistance(currentPrice, emaSlow));

        return emaFast > emaSlow &&
               distanceFromEma <= options.TrendMaxDistanceFromEmaPercent;
    }

    private static decimal PercentDistance(decimal value, decimal reference)
    {
        return reference <= 0m ? 0m : Math.Abs(value - reference) / reference * 100m;
    }

    private static bool IsBtcRiskOff(IReadOnlyCollection<Candle> btcCandles, int lookbackCandles, decimal maxMovePercent)
    {
        var slice = btcCandles.OrderBy(candle => candle.OpenTime).TakeLast(Math.Max(1, lookbackCandles)).ToArray();
        if (slice.Length < Math.Max(1, lookbackCandles) || slice[0].Open <= 0m)
        {
            return false;
        }

        return (slice[^1].Close - slice[0].Open) / slice[0].Open * 100m <= -Math.Abs(maxMovePercent);
    }
}
