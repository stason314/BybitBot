using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class BreakoutStrategy : ITradingStrategy
{
    public TradingStrategyType Type => TradingStrategyType.Breakout;

    public string DisplayName => "Breakout";

    public StrategyScore Score(MarketRegime regime, IReadOnlyList<Signal> signals, GridOptions options)
    {
        var breakoutSignal = signals.FirstOrDefault(signal => signal.Type is SignalType.BreakoutUp or SignalType.VolumeSpike);
        var isAllowed = regime == MarketRegime.BreakoutUp;
        return new StrategyScore
        {
            StrategyType = StrategyType.Breakout,
            Score = isAllowed ? 82m : 20m,
            Confidence = isAllowed ? Math.Max(0.7m, breakoutSignal?.Confidence ?? 0.7m) : 0.2m,
            RequiredCapitalPercent = options.BreakoutCapitalPercent,
            IsAllowed = isAllowed,
            Reason = isAllowed ? "Confirmed breakout-up regime." : "Breakout conditions are not confirmed."
        };
    }

    public bool HasConfirmation(IReadOnlyList<Candle> candles, decimal breakoutLevel, int confirmationCandles)
    {
        var confirmed = candles.OrderBy(candle => candle.OpenTime).TakeLast(Math.Max(1, confirmationCandles)).ToArray();
        return confirmed.Length >= Math.Max(1, confirmationCandles) &&
               confirmed.All(candle => candle.Close > breakoutLevel);
    }
}
