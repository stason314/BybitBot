namespace BybitGridBot.App;

public sealed class StrategyPerformanceTracker
{
    public IReadOnlyList<StrategyPerformanceSnapshot> Build(IReadOnlyList<FuturesBacktestTrade> trades)
    {
        return trades
            .GroupBy(trade => new
            {
                Strategy = string.IsNullOrWhiteSpace(trade.StrategyName) ? trade.Pattern : trade.StrategyName,
                trade.Symbol,
                Direction = trade.Side
            })
            .Select(group => BuildSnapshot(group.Key.Strategy, group.Key.Symbol, group.Key.Direction, group.ToArray()))
            .OrderBy(snapshot => snapshot.StrategyName)
            .ThenBy(snapshot => snapshot.Symbol)
            .ThenBy(snapshot => snapshot.Direction)
            .ToArray();
    }

    private static StrategyPerformanceSnapshot BuildSnapshot(
        string strategy,
        string symbol,
        string direction,
        IReadOnlyList<FuturesBacktestTrade> trades)
    {
        var wins = trades.Where(trade => trade.NetPnl > 0m).ToArray();
        var losses = trades.Where(trade => trade.NetPnl < 0m).ToArray();
        var grossProfit = wins.Sum(trade => trade.NetPnl);
        var grossLoss = Math.Abs(losses.Sum(trade => trade.NetPnl));
        return new StrategyPerformanceSnapshot
        {
            StrategyName = strategy,
            Symbol = symbol,
            Direction = direction,
            GrossPnl = trades.Sum(trade => trade.GrossPnl),
            Fees = trades.Sum(trade => trade.Fees),
            NetPnl = trades.Sum(trade => trade.NetPnl),
            WinRate = trades.Count > 0 ? (decimal)wins.Length / trades.Count * 100m : 0m,
            ProfitFactor = grossLoss > 0m ? grossProfit / grossLoss : grossProfit > 0m ? 999m : 0m,
            AverageR = trades.Count > 0 ? trades.Average(trade => trade.RMultiple) : 0m,
            TradesCount = trades.Count,
            LongTrades = trades.Count(trade => string.Equals(trade.Side, "Long", StringComparison.OrdinalIgnoreCase)),
            ShortTrades = trades.Count(trade => string.Equals(trade.Side, "Short", StringComparison.OrdinalIgnoreCase)),
            AverageWin = wins.Length > 0 ? wins.Average(trade => trade.NetPnl) : 0m,
            AverageLoss = losses.Length > 0 ? losses.Average(trade => trade.NetPnl) : 0m
        };
    }
}

public sealed class StrategyPerformanceSnapshot
{
    public string StrategyName { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public string Direction { get; init; } = string.Empty;

    public decimal GrossPnl { get; init; }

    public decimal Fees { get; init; }

    public decimal NetPnl { get; init; }

    public decimal UnrealizedPnl { get; init; }

    public decimal RealizedPnl { get; init; }

    public decimal WinRate { get; init; }

    public decimal ProfitFactor { get; init; }

    public decimal AverageR { get; init; }

    public decimal MaxDrawdown { get; init; }

    public int TradesCount { get; init; }

    public int LongTrades { get; init; }

    public int ShortTrades { get; init; }

    public decimal AverageWin { get; init; }

    public decimal AverageLoss { get; init; }
}
