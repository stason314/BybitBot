using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class ProfitProtectionManager
{
    public ProfitProtectionDecision Evaluate(GridOptions options, PositionSnapshot position, decimal currentPrice, MarketPhaseResult phase)
    {
        if (!options.ProfitProtectionEnabled || position.BaseAssetQuantity <= 0m || position.AverageEntryPrice <= 0m || currentPrice <= 0m)
        {
            return ProfitProtectionDecision.Inactive("Profit protection disabled or no open position.");
        }

        var unrealizedPercent = (currentPrice - position.AverageEntryPrice) / position.AverageEntryPrice * 100m;
        if (unrealizedPercent < options.ProfitProtectionTriggerPercent)
        {
            return ProfitProtectionDecision.Inactive($"Position profit {unrealizedPercent:F2}% is below trigger {options.ProfitProtectionTriggerPercent:F2}%.");
        }

        var shouldReduce = phase.Phase is MarketPhase.Dump or MarketPhase.Exhaustion or MarketPhase.HighVolatility;
        var reason = shouldReduce
            ? $"Profit protection active on {phase.Phase}; partial take-profit allowed."
            : "Profit protection active; trailing stop should guard open profit.";

        return new ProfitProtectionDecision(
            IsActive: true,
            ShouldBlockNewBuys: shouldReduce,
            ShouldTakePartialProfit: shouldReduce && options.PartialTakeProfitEnabled,
            PartialTakeProfitPercent: shouldReduce && options.PartialTakeProfitEnabled ? options.PartialTakeProfitPercent : 0m,
            TrailingStopPercent: options.TrailingStopEnabled ? options.TrailingStopPercent : 0m,
            Reason: reason);
    }
}

public sealed record ProfitProtectionDecision(
    bool IsActive,
    bool ShouldBlockNewBuys,
    bool ShouldTakePartialProfit,
    decimal PartialTakeProfitPercent,
    decimal TrailingStopPercent,
    string Reason)
{
    public static ProfitProtectionDecision Inactive(string reason)
    {
        return new ProfitProtectionDecision(false, false, false, 0m, 0m, reason);
    }
}
