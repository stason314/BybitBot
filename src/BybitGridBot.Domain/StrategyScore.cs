namespace BybitGridBot.Domain;

public sealed class StrategyScore
{
    public StrategyType StrategyType { get; init; }

    public decimal Score { get; init; }

    public string Reason { get; init; } = string.Empty;

    public decimal Confidence { get; init; }

    public decimal RequiredCapitalPercent { get; init; }

    public bool IsAllowed { get; init; } = true;
}
