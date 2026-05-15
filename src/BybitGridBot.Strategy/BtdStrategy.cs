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
}
