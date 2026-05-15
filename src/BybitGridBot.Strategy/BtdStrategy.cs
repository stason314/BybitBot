using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class BtdStrategy : ITradingStrategy
{
    public TradingStrategyType Type => TradingStrategyType.Btd;

    public string DisplayName => "Buy The Dip";

    public bool IsDipTriggered(
        BtdStrategyConfig config,
        decimal currentPrice,
        IReadOnlyCollection<Candle> candles)
    {
        if (config.DipPercent <= 0m || candles.Count == 0)
        {
            return false;
        }

        var lookback = Math.Max(1, config.DipLookbackCandles);
        var high = candles
            .OrderByDescending(candle => candle.OpenTime)
            .Take(lookback)
            .Max(candle => candle.High);
        if (high <= 0m)
        {
            return false;
        }

        var drawdownPercent = (high - currentPrice) / high * 100m;
        return drawdownPercent >= config.DipPercent;
    }

    public bool IsDipAllowedByRegime(MarketRegime regime, IReadOnlyList<Signal> signals)
    {
        return regime == MarketRegime.Uptrend &&
               signals.All(signal => signal.Type is not SignalType.BtcRiskOff and not SignalType.BreakoutDown);
    }

    public bool IsDipAllowedByPhase(
        GridOptions options,
        MarketPhaseResult phase,
        decimal currentPrice,
        IReadOnlyCollection<Candle> candles,
        IReadOnlyCollection<Candle> btcCandles)
    {
        if (phase.Phase is MarketPhase.Dump or MarketPhase.HighVolatility or MarketPhase.BreakoutDown)
        {
            return false;
        }

        if (options.BtdRequireUptrend && phase.Phase != MarketPhase.PullbackInUptrend)
        {
            return false;
        }

        if (options.BtdBlockOnBtcRiskOff && IsBtcRiskOff(btcCandles, options.BtcLookbackCandles, options.BtcMaxMovePercent))
        {
            return false;
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < Math.Max(options.EmaSlow, options.TrendEmaSlow) + 1)
        {
            return false;
        }

        var emaFast = MarketRegimeDetector.CalculateEma(ordered, options.EmaFast);
        var emaSlow = MarketRegimeDetector.CalculateEma(ordered, options.EmaSlow);
        if (emaFast <= emaSlow)
        {
            return false;
        }

        var distanceToEma = Math.Min(PercentDistance(currentPrice, emaFast), PercentDistance(currentPrice, emaSlow));
        var recentHigh = ordered.TakeLast(Math.Max(1, options.DumpLookbackCandles * 10)).Max(candle => candle.High);
        var pullbackPercent = recentHigh <= 0m ? 0m : (recentHigh - currentPrice) / recentHigh * 100m;
        return distanceToEma <= options.BtdMaxDistanceFromEmaPercent &&
               pullbackPercent >= options.BtdMinPullbackPercent;
    }

    public bool CanOpenBuy(
        BtdStrategyConfig config,
        IReadOnlyCollection<GridOrder> orders,
        DateTimeOffset now)
    {
        var maxBuys = Math.Max(1, config.MaxBuys);
        var activeBuyCount = orders.Count(order => order.Side == TradeSide.Buy && order.IsActive);
        if (activeBuyCount >= maxBuys)
        {
            return false;
        }

        var minInterval = TimeSpan.FromMinutes(Math.Max(1, config.MinMinutesBetweenBuys));
        var latestBuy = orders
            .Where(order => order.Side == TradeSide.Buy)
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault();

        return latestBuy is null || now - latestBuy.CreatedAt >= minInterval;
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
