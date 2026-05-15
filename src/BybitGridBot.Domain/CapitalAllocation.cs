namespace BybitGridBot.Domain;

public sealed class CapitalAllocation
{
    public StrategyType StrategyType { get; init; }

    public bool IsAllowed { get; init; }

    public decimal RequestedUsdt { get; init; }

    public decimal AllocatedUsdt { get; init; }

    public decimal StrategyLimitUsdt { get; init; }

    public decimal AvailableUsdt { get; init; }

    public decimal ReservedUsdt { get; init; }

    public string Reason { get; init; } = string.Empty;
}
