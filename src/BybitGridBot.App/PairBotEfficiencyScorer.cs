using BybitGridBot.Domain;

namespace BybitGridBot.App;

internal static class PairBotEfficiencyScorer
{
    public static PairBotEfficiencyScoreView ScoreFutures(
        string symbol,
        FuturesStrategyType strategy,
        IReadOnlyCollection<FuturesFillRecord> fills)
    {
        var closes = fills
            .Where(fill => fill.Quantity > 0m)
            .Where(fill => fill.Action is FuturesTradeAction.CloseLong or FuturesTradeAction.CloseShort or FuturesTradeAction.ReduceOnlyClose)
            .OrderBy(fill => fill.CreatedAt)
            .TakeLast(20)
            .ToArray();
        if (closes.Length == 0)
        {
            return new PairBotEfficiencyScoreView(50m, "NO_HISTORY", [$"{symbol}+{strategy} no closed futures trades yet"]);
        }

        var recent = closes.TakeLast(Math.Min(8, closes.Length)).ToArray();
        var netPnl = closes.Sum(fill => fill.RealizedPnl);
        var recentNetPnl = recent.Sum(fill => fill.RealizedPnl);
        var wins = closes.Count(fill => fill.RealizedPnl > 0m);
        var recentWins = recent.Count(fill => fill.RealizedPnl > 0m);
        var winRate = wins / (decimal)closes.Length * 100m;
        var recentWinRate = recentWins / (decimal)recent.Length * 100m;
        var averageNet = closes.Average(fill => fill.RealizedPnl);
        var recentAverageNet = recent.Average(fill => fill.RealizedPnl);
        var recentLosses = CountRecentLosses(closes.Select(fill => fill.RealizedPnl));

        var score = 50m;
        score += Math.Clamp(netPnl * 100m, -20m, 20m);
        score += Math.Clamp(recentNetPnl * 180m, -25m, 25m);
        score += Math.Clamp((winRate - 50m) * 0.2m, -10m, 10m);
        score += Math.Clamp((recentWinRate - 50m) * 0.35m, -18m, 18m);
        score += Math.Clamp(averageNet * 220m, -12m, 12m);
        score += Math.Clamp(recentAverageNet * 350m, -18m, 18m);
        score -= recentLosses * 8m;
        score = Math.Clamp(decimal.Round(score, 2, MidpointRounding.AwayFromZero), 0m, 100m);

        var reasons = new[]
        {
            $"{symbol}+{strategy} efficiency {ResolveLabel(score)}",
            $"recent net {recentNetPnl:0.####}, recent win {recentWinRate:0.#}%",
            $"all net {netPnl:0.####}, win {winRate:0.#}%, avg {averageNet:0.####}",
            $"recent losses {recentLosses}"
        };
        return new PairBotEfficiencyScoreView(score, ResolveLabel(score), reasons);
    }

    public static PairBotEfficiencyScoreView ScoreSpot(
        string symbol,
        string strategyType,
        IReadOnlyCollection<GridOrder> orders)
    {
        var closes = orders
            .Where(order => order.Status == OrderStatus.Filled)
            .Where(order => order.Side == TradeSide.Sell)
            .Where(order => string.Equals(order.StrategySource, strategyType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(strategyType, "Grid", StringComparison.OrdinalIgnoreCase))
            .OrderBy(order => order.FilledAt ?? order.UpdatedAt)
            .TakeLast(20)
            .ToArray();
        if (closes.Length == 0)
        {
            return new PairBotEfficiencyScoreView(50m, "NO_HISTORY", [$"{symbol}+{strategyType} no closed spot trades yet"]);
        }

        var net = closes.Select(order => order.RealizedPnl - order.FeePaid).ToArray();
        var recent = net.TakeLast(Math.Min(8, net.Length)).ToArray();
        var score = 50m;
        score += Math.Clamp(net.Sum() * 80m, -20m, 20m);
        score += Math.Clamp(recent.Sum() * 150m, -25m, 25m);
        score += Math.Clamp((net.Count(value => value > 0m) / (decimal)net.Length * 100m - 50m) * 0.2m, -10m, 10m);
        score += Math.Clamp((recent.Count(value => value > 0m) / (decimal)recent.Length * 100m - 50m) * 0.35m, -18m, 18m);
        score += Math.Clamp(net.Average() * 180m, -12m, 12m);
        score += Math.Clamp(recent.Average() * 300m, -18m, 18m);
        score -= CountRecentLosses(net) * 8m;
        score = Math.Clamp(decimal.Round(score, 2, MidpointRounding.AwayFromZero), 0m, 100m);

        var reasons = new[]
        {
            $"{symbol}+{strategyType} spot efficiency {ResolveLabel(score)}",
            $"recent net {recent.Sum():0.####}, all net {net.Sum():0.####}",
            $"recent losses {CountRecentLosses(net)}"
        };
        return new PairBotEfficiencyScoreView(score, ResolveLabel(score), reasons);
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
            >= 75m => "HOT",
            >= 55m => "OK",
            >= 35m => "COOLING",
            _ => "AVOID"
        };
}

internal sealed record PairBotEfficiencyScoreView(
    decimal Score,
    string Label,
    IReadOnlyList<string> Reasons);
