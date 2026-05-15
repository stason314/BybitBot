using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class ProfitProtectionManager
{
    public ProfitProtectionDecision Evaluate(GridOptions options, PositionSnapshot position, decimal currentPrice, decimal peakPrice, MarketPhaseResult phase)
    {
        if (!options.ProfitProtectionEnabled || position.BaseAssetQuantity <= 0m || position.AverageEntryPrice <= 0m || currentPrice <= 0m)
        {
            return ProfitProtectionDecision.Inactive("Profit protection disabled or no open position.");
        }

        var unrealizedPercent = (currentPrice - position.AverageEntryPrice) / position.AverageEntryPrice * 100m;
        var effectivePeakPrice = decimal.Max(currentPrice, peakPrice);
        var peakProfitPercent = (effectivePeakPrice - position.AverageEntryPrice) / position.AverageEntryPrice * 100m;
        var trailingStopPrice = options.TrailingStopEnabled
            ? effectivePeakPrice * (1m - options.TrailingStopPercent / 100m)
            : 0m;
        var shouldTrailExit = options.TrailingStopEnabled &&
            trailingStopPrice > 0m &&
            peakProfitPercent >= options.ProfitProtectionTriggerPercent &&
            currentPrice <= trailingStopPrice;

        if (peakProfitPercent < options.ProfitProtectionTriggerPercent)
        {
            return ProfitProtectionDecision.Inactive(
                $"Position peak profit {peakProfitPercent:F2}% is below trigger {options.ProfitProtectionTriggerPercent:F2}%.",
                unrealizedPercent,
                peakProfitPercent,
                effectivePeakPrice,
                trailingStopPrice);
        }

        var shouldReduce = phase.Phase is MarketPhase.Dump or MarketPhase.Exhaustion or MarketPhase.HighVolatility;
        var reason = shouldReduce
            ? $"Profit protection active on {phase.Phase}; partial take-profit allowed."
            : shouldTrailExit
                ? $"Profit protection trailing stop hit. Current {currentPrice}, stop {trailingStopPrice}."
            : "Profit protection active; trailing stop should guard open profit.";

        return new ProfitProtectionDecision(
            IsActive: true,
            ShouldBlockNewBuys: shouldReduce || shouldTrailExit,
            ShouldTakePartialProfit: shouldReduce && options.PartialTakeProfitEnabled,
            PartialTakeProfitPercent: shouldReduce && options.PartialTakeProfitEnabled ? options.PartialTakeProfitPercent : 0m,
            TrailingStopPercent: options.TrailingStopEnabled ? options.TrailingStopPercent : 0m,
            CurrentProfitPercent: unrealizedPercent,
            PeakProfitPercent: peakProfitPercent,
            PeakPrice: effectivePeakPrice,
            TrailingStopPrice: trailingStopPrice,
            ShouldTrailExit: shouldTrailExit,
            Reason: reason);
    }
}

public sealed record ProfitProtectionDecision(
    bool IsActive,
    bool ShouldBlockNewBuys,
    bool ShouldTakePartialProfit,
    decimal PartialTakeProfitPercent,
    decimal TrailingStopPercent,
    decimal CurrentProfitPercent,
    decimal PeakProfitPercent,
    decimal PeakPrice,
    decimal TrailingStopPrice,
    bool ShouldTrailExit,
    string Reason)
{
    public static ProfitProtectionDecision Inactive(
        string reason,
        decimal currentProfitPercent = 0m,
        decimal peakProfitPercent = 0m,
        decimal peakPrice = 0m,
        decimal trailingStopPrice = 0m)
    {
        return new ProfitProtectionDecision(false, false, false, 0m, 0m, currentProfitPercent, peakProfitPercent, peakPrice, trailingStopPrice, false, reason);
    }
}
