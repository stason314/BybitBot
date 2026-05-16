using BybitGridBot.Domain;

namespace BybitGridBot.App;

internal sealed class FuturesFillLedger
{
    public const int QueryLimit = 100_000;

    public decimal TotalRealizedPnl { get; init; }

    public decimal DailyRealizedPnl { get; init; }

    public decimal TotalFunding { get; init; }

    public decimal DailyFunding { get; init; }

    public decimal FeesPaid { get; init; }

    public decimal DailyFeesPaid { get; init; }

    public int TradeFillCount { get; init; }

    public static FuturesFillLedger Build(IReadOnlyCollection<FuturesFillRecord> fills, DateOnly today)
    {
        var totalRealized = 0m;
        var dailyRealized = 0m;
        var totalFunding = 0m;
        var dailyFunding = 0m;
        var fees = 0m;
        var dailyFees = 0m;
        var tradeFillCount = 0;

        foreach (var fill in fills)
        {
            var isToday = DateOnly.FromDateTime(fill.CreatedAt.UtcDateTime) == today;
            totalRealized += fill.RealizedPnl;
            totalFunding += fill.Funding;
            fees += fill.Fee;
            if (fill.Quantity > 0m && fill.Action != FuturesTradeAction.Funding)
            {
                tradeFillCount++;
            }

            if (!isToday)
            {
                continue;
            }

            dailyRealized += fill.RealizedPnl;
            dailyFunding += fill.Funding;
            dailyFees += fill.Fee;
        }

        return new FuturesFillLedger
        {
            TotalRealizedPnl = totalRealized,
            DailyRealizedPnl = dailyRealized,
            TotalFunding = totalFunding,
            DailyFunding = dailyFunding,
            FeesPaid = fees,
            DailyFeesPaid = dailyFees,
            TradeFillCount = tradeFillCount
        };
    }
}
