using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class StrategyDecision
{
    public IReadOnlyList<OrderIntent> OrderIntents { get; init; } = [];

    public IReadOnlyList<CancelOrderIntent> CancelOrderIntents { get; init; } = [];

    public string? Reason { get; init; }

    public static StrategyDecision Empty(string? reason = null) => new()
    {
        Reason = reason
    };
}

public sealed record OrderIntent(
    TradeSide Side,
    decimal Price,
    decimal Quantity,
    string? ParentOrderLinkId = null,
    string? StrategySource = null);

public sealed record CancelOrderIntent(string OrderLinkId, string? Reason = null);
