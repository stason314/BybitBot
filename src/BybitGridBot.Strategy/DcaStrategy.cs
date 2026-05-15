using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class DcaStrategy : ITradingStrategy
{
    public TradingStrategyType Type => TradingStrategyType.Dca;

    public string DisplayName => "DCA";

    public bool IsDueForEntry(
        DcaStrategyConfig config,
        IReadOnlyCollection<GridOrder> orders,
        DateTimeOffset now)
    {
        var intervalMinutes = Math.Max(1, config.BuyIntervalMinutes);
        var latestBuy = orders
            .Where(order => order.Side == TradeSide.Buy)
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault();

        return latestBuy is null || now - latestBuy.CreatedAt >= TimeSpan.FromMinutes(intervalMinutes);
    }

    public decimal CalculateLimitBuyPrice(decimal currentPrice, DcaStrategyConfig config)
    {
        var offset = Math.Max(0m, config.LimitOffsetPercent) / 100m;
        return currentPrice * (1m - offset);
    }

    public decimal CalculateTakeProfitPrice(decimal entryPrice, DcaStrategyConfig config)
    {
        var takeProfit = Math.Max(0m, config.TakeProfitPercent) / 100m;
        return entryPrice * (1m + takeProfit);
    }

    public bool IsDipAllowed(
        DcaStrategyConfig config,
        decimal currentPrice,
        IReadOnlyCollection<Candle> candles)
    {
        if (config.DipPercent <= 0m)
        {
            return true;
        }

        if (candles.Count == 0)
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

    public bool IsAllocationAllowed(decimal currentPositionUsdt, decimal nextOrderUsdt, decimal maxAllocatedUsdt)
    {
        return nextOrderUsdt > 0m &&
               maxAllocatedUsdt > 0m &&
               currentPositionUsdt + nextOrderUsdt <= maxAllocatedUsdt;
    }
}
