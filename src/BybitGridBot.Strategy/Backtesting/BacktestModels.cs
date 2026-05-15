using BybitGridBot.Domain;

namespace BybitGridBot.Strategy.Backtesting;

public sealed class BacktestResult
{
    public decimal NetPnl { get; init; }

    public decimal MaxDrawdown { get; init; }

    public decimal WinRate { get; init; }

    public decimal ProfitFactor { get; init; }

    public int TotalTrades { get; init; }

    public decimal FeesPaid { get; init; }

    public IReadOnlyDictionary<StrategyType, decimal> StrategyUsagePercent { get; init; } = new Dictionary<StrategyType, decimal>();
}

public interface IBacktester
{
    BacktestResult Run(IReadOnlyList<Candle> candles, GridOptions options);
}
