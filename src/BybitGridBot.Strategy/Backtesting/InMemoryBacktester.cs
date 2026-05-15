using BybitGridBot.Domain;

namespace BybitGridBot.Strategy.Backtesting;

public sealed class InMemoryBacktester : IBacktester
{
    private readonly MarketRegimeDetector _detector = new();
    private readonly StrategyRouter _router = new();

    public BacktestResult Run(IReadOnlyList<Candle> candles, GridOptions options)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length == 0)
        {
            return new BacktestResult();
        }

        var usage = new Dictionary<StrategyType, int>();
        var equity = 1000m;
        var peak = equity;
        var maxDrawdown = 0m;
        var fees = 0m;
        var trades = 0;

        for (var index = 1; index < ordered.Length; index++)
        {
            var window = ordered.Take(index + 1).ToArray();
            var snapshot = new MarketSnapshot
            {
                Symbol = options.Symbol,
                CurrentPrice = window[^1].Close,
                Candles = window
            };
            var regime = _detector.Detect(snapshot, options);
            var scores = BuildScores(regime, options);
            var decision = _router.SelectStrategy(BotType.Auto, regime, scores, options, window[^1].OpenTime);
            usage[decision.SelectedStrategy] = usage.GetValueOrDefault(decision.SelectedStrategy) + 1;

            if (decision.SelectedStrategy == StrategyType.Pause)
            {
                continue;
            }

            var move = window[^1].Close - window[^1].Open;
            var gross = decision.SelectedStrategy == StrategyType.Grid ? Math.Abs(move) * 2m : move * 3m;
            var fee = Math.Abs(gross) * options.FeePercent / 100m;
            fees += fee;
            equity += gross - fee;
            trades++;
            peak = Math.Max(peak, equity);
            maxDrawdown = Math.Max(maxDrawdown, peak - equity);
        }

        var total = Math.Max(1, usage.Values.Sum());
        return new BacktestResult
        {
            NetPnl = equity - 1000m,
            MaxDrawdown = maxDrawdown,
            WinRate = trades == 0 ? 0m : 0.5m,
            ProfitFactor = equity >= 1000m ? 1.1m : 0.9m,
            TotalTrades = trades,
            FeesPaid = fees,
            StrategyUsagePercent = usage.ToDictionary(item => item.Key, item => item.Value * 100m / total)
        };
    }

    private static IReadOnlyList<StrategyScore> BuildScores(MarketRegime regime, GridOptions options)
    {
        return
        [
            new StrategyScore { StrategyType = StrategyType.Grid, Score = regime == MarketRegime.RangeBound ? 80m : 30m, Confidence = regime == MarketRegime.RangeBound ? 0.8m : 0.3m, RequiredCapitalPercent = options.GridCapitalPercent, IsAllowed = regime == MarketRegime.RangeBound, Reason = "Backtest grid score." },
            new StrategyScore { StrategyType = StrategyType.Breakout, Score = regime == MarketRegime.BreakoutUp ? 85m : 20m, Confidence = regime == MarketRegime.BreakoutUp ? 0.8m : 0.2m, RequiredCapitalPercent = options.BreakoutCapitalPercent, IsAllowed = regime == MarketRegime.BreakoutUp, Reason = "Backtest breakout score." },
            new StrategyScore { StrategyType = StrategyType.TrendFollowing, Score = regime == MarketRegime.Uptrend ? 78m : 20m, Confidence = regime == MarketRegime.Uptrend ? 0.75m : 0.2m, RequiredCapitalPercent = options.TrendCapitalPercent, IsAllowed = regime == MarketRegime.Uptrend, Reason = "Backtest trend score." }
        ];
    }
}
