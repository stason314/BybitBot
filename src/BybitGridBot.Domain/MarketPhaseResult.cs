namespace BybitGridBot.Domain;

public sealed class MarketPhaseResult
{
    public MarketPhase Phase { get; init; } = MarketPhase.Unknown;
    public decimal Confidence { get; init; }
    public decimal Score { get; init; }
    public string Reason { get; init; } = "Not evaluated.";
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, decimal> KeyLevels { get; init; } = new Dictionary<string, decimal>();
    public StrategyType SuggestedStrategy { get; init; } = StrategyType.Pause;

    public static MarketPhaseResult Unknown(string reason, DateTimeOffset detectedAt)
    {
        return new MarketPhaseResult
        {
            Phase = MarketPhase.Unknown,
            Confidence = 0m,
            Score = 0m,
            Reason = reason,
            DetectedAt = detectedAt,
            SuggestedStrategy = StrategyType.Pause
        };
    }
}
