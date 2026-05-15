namespace BybitGridBot.Domain;

public sealed class StrategyDecision
{
    public StrategyType SelectedStrategy { get; init; } = StrategyType.Pause;

    public MarketRegime MarketRegime { get; init; } = MarketRegime.Unknown;

    public MarketPhase MarketPhase { get; init; } = MarketPhase.Unknown;

    public IReadOnlyList<StrategyScore> Scores { get; init; } = [];

    public string Reason { get; init; } = string.Empty;

    public bool IsSwitch { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
