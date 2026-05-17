using BybitGridBot.Domain;

namespace BybitGridBot.App;

internal static class StrategyPerformanceScorer
{
    public static StrategyPerformanceScoreView ScoreFutures(
        FuturesStrategyType strategy,
        IReadOnlyCollection<FuturesFillRecord> fills)
    {
        var closingFills = fills
            .Where(fill => fill.Quantity > 0m)
            .Where(fill => fill.Action is FuturesTradeAction.CloseLong or FuturesTradeAction.CloseShort or FuturesTradeAction.ReduceOnlyClose)
            .OrderBy(fill => fill.CreatedAt)
            .TakeLast(50)
            .ToArray();
        if (closingFills.Length == 0)
        {
            return new StrategyPerformanceScoreView(50m, "NO_HISTORY", ["strategy performance no futures closes yet"]);
        }

        var windowStart = closingFills[0].CreatedAt;
        var windowFills = fills
            .Where(fill => fill.Quantity > 0m)
            .Where(fill => fill.Action != FuturesTradeAction.Funding)
            .Where(fill => fill.CreatedAt >= windowStart)
            .ToArray();
        var realizedTradingPnl = closingFills.Sum(fill => fill.RealizedPnl + fill.Fee - fill.Funding);
        var feesPaid = windowFills.Sum(fill => fill.Fee);
        var netPnl = closingFills.Sum(fill => fill.RealizedPnl);
        var wins = closingFills.Count(fill => fill.RealizedPnl > 0m);
        var losses = closingFills.Count(fill => fill.RealizedPnl < 0m);
        var winRate = closingFills.Length > 0 ? wins / (decimal)closingFills.Length * 100m : 0m;
        var grossProfit = closingFills.Where(fill => fill.RealizedPnl > 0m).Sum(fill => fill.RealizedPnl);
        var grossLoss = Math.Abs(closingFills.Where(fill => fill.RealizedPnl < 0m).Sum(fill => fill.RealizedPnl));
        var profitFactor = grossLoss > 0m ? grossProfit / grossLoss : grossProfit > 0m ? 10m : 0m;
        var averageNetTrade = closingFills.Average(fill => fill.RealizedPnl);
        var feeToTradingPnl = realizedTradingPnl > 0m ? feesPaid / realizedTradingPnl * 100m : 100m;
        var maxDrawdown = CalculateMaxDrawdown(closingFills.Select(fill => fill.RealizedPnl));
        var recentLosses = CountRecentLosses(closingFills.Select(fill => fill.RealizedPnl));

        var score = 50m;
        score += Math.Clamp(netPnl * 120m, -25m, 25m);
        score += Math.Clamp((winRate - 50m) * 0.35m, -18m, 18m);
        score += Math.Clamp((profitFactor - 1m) * 12m, -18m, 24m);
        score += Math.Clamp(averageNetTrade * 300m, -15m, 15m);
        score -= Math.Clamp(feeToTradingPnl - 25m, 0m, 22m);
        score -= Math.Clamp(maxDrawdown * 100m, 0m, 18m);
        score -= recentLosses * 6m;
        score = Math.Clamp(decimal.Round(score, 2, MidpointRounding.AwayFromZero), 0m, 100m);

        var reasons = new[]
        {
            $"strategy {strategy} futures performance {ResolveLabel(score)}",
            $"net {netPnl:0.####}, win {winRate:0.#}%, PF {profitFactor:0.##}",
            $"avg net {averageNetTrade:0.####}, fees/trading {feeToTradingPnl:0.#}%",
            $"drawdown {maxDrawdown:0.####}, recent losses {recentLosses}"
        };
        return new StrategyPerformanceScoreView(score, ResolveLabel(score), reasons);
    }

    public static StrategyPerformanceScoreView ScoreSpot(
        string strategyType,
        IReadOnlyCollection<GridOrder> orders)
    {
        var closed = orders
            .Where(order => order.Status == OrderStatus.Filled)
            .Where(order => order.Side == TradeSide.Sell)
            .OrderBy(order => order.FilledAt ?? order.UpdatedAt)
            .TakeLast(50)
            .ToArray();
        if (closed.Length == 0)
        {
            return new StrategyPerformanceScoreView(50m, "NO_HISTORY", ["strategy performance no spot closes yet"]);
        }

        var netTrades = closed.Select(order => order.RealizedPnl - order.FeePaid).ToArray();
        var netPnl = netTrades.Sum();
        var wins = netTrades.Count(value => value > 0m);
        var winRate = wins / (decimal)netTrades.Length * 100m;
        var grossProfit = netTrades.Where(value => value > 0m).Sum();
        var grossLoss = Math.Abs(netTrades.Where(value => value < 0m).Sum());
        var profitFactor = grossLoss > 0m ? grossProfit / grossLoss : grossProfit > 0m ? 10m : 0m;
        var averageNetTrade = netTrades.Average();
        var feesPaid = closed.Sum(order => order.FeePaid);
        var feeToPnl = netPnl > 0m ? feesPaid / netPnl * 100m : 100m;
        var maxDrawdown = CalculateMaxDrawdown(netTrades);
        var recentLosses = CountRecentLosses(netTrades);

        var score = 50m;
        score += Math.Clamp(netPnl * 80m, -25m, 25m);
        score += Math.Clamp((winRate - 50m) * 0.35m, -18m, 18m);
        score += Math.Clamp((profitFactor - 1m) * 12m, -18m, 24m);
        score += Math.Clamp(averageNetTrade * 220m, -15m, 15m);
        score -= Math.Clamp(feeToPnl - 25m, 0m, 22m);
        score -= Math.Clamp(maxDrawdown * 80m, 0m, 18m);
        score -= recentLosses * 6m;
        score = Math.Clamp(decimal.Round(score, 2, MidpointRounding.AwayFromZero), 0m, 100m);

        var reasons = new[]
        {
            $"strategy {strategyType} spot performance {ResolveLabel(score)}",
            $"net {netPnl:0.####}, win {winRate:0.#}%, PF {profitFactor:0.##}",
            $"avg net {averageNetTrade:0.####}, fees/net {feeToPnl:0.#}%",
            $"drawdown {maxDrawdown:0.####}, recent losses {recentLosses}"
        };
        return new StrategyPerformanceScoreView(score, ResolveLabel(score), reasons);
    }

    private static decimal CalculateMaxDrawdown(IEnumerable<decimal> pnl)
    {
        var equity = 0m;
        var peak = 0m;
        var maxDrawdown = 0m;
        foreach (var value in pnl)
        {
            equity += value;
            peak = decimal.Max(peak, equity);
            maxDrawdown = decimal.Max(maxDrawdown, peak - equity);
        }

        return maxDrawdown;
    }

    private static int CountRecentLosses(IEnumerable<decimal> pnl)
    {
        var count = 0;
        foreach (var value in pnl.Reverse())
        {
            if (value >= 0m)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static string ResolveLabel(decimal score) =>
        score switch
        {
            >= 75m => "STRONG",
            >= 55m => "OK",
            >= 35m => "WEAK",
            _ => "BAD"
        };
}

internal sealed record StrategyPerformanceScoreView(
    decimal Score,
    string Label,
    IReadOnlyList<string> Reasons);
