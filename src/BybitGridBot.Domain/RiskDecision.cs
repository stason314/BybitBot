namespace BybitGridBot.Domain;

public enum RiskSeverity
{
    Info,
    Warning,
    Critical
}

public enum RiskSuggestedAction
{
    Allow,
    BlockNewOrders,
    CancelBuyOrders,
    CancelSellOrders,
    CancelAllOrders,
    PauseBot
}

public sealed class RiskDecision
{
    public bool IsAllowed { get; init; } = true;

    public string Reason { get; init; } = "Allowed.";

    public RiskSeverity Severity { get; init; } = RiskSeverity.Info;

    public RiskSuggestedAction SuggestedAction { get; init; } = RiskSuggestedAction.Allow;
}
