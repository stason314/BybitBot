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

    public bool CanEnter(
        GridOptions options,
        MarketPhaseResult phase,
        IReadOnlyCollection<Candle> candles,
        decimal currentPrice)
    {
        if (phase.Phase != MarketPhase.BreakoutUp)
        {
            return false;
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < Math.Max(options.EmaSlow, options.VolumeSmaPeriod) + options.BreakoutConfirmationCandles)
        {
            return false;
        }

        var resistance = phase.KeyLevels.TryGetValue("resistance", out var phaseResistance)
            ? phaseResistance
            : ordered.TakeLast(30).Max(candle => candle.High);
        var emaFast = MarketRegimeDetector.CalculateEma(ordered, options.EmaFast);
        var emaSlow = MarketRegimeDetector.CalculateEma(ordered, options.EmaSlow);
        var volumeSma = ordered.TakeLast(options.VolumeSmaPeriod).Average(candle => candle.Volume);
        var volumeSpike = volumeSma > 0m && ordered[^1].Volume >= volumeSma * options.BreakoutVolumeMultiplier;
        var distanceFromEma = Math.Min(PercentDistance(currentPrice, emaFast), PercentDistance(currentPrice, emaSlow));

        return HasConfirmation(ordered, resistance, options.BreakoutConfirmationCandles) &&
               volumeSpike &&
               distanceFromEma <= options.BreakoutMaxDistanceFromEmaPercent;
    }

    public decimal CalculateStopLoss(GridOptions options, decimal breakoutLevel, decimal atr)
    {
        var atrStop = breakoutLevel - (atr * options.BreakoutAtrStopMultiplier);
        return decimal.Round(Math.Min(breakoutLevel, atrStop), 8, MidpointRounding.ToZero);
    }

    private static decimal PercentDistance(decimal value, decimal reference)
    {
        return reference <= 0m ? 0m : Math.Abs(value - reference) / reference * 100m;
    }
}
