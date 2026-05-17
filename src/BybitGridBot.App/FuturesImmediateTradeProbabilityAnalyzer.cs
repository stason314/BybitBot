using BybitGridBot.Domain;
using BybitGridBot.Storage;

namespace BybitGridBot.App;

internal static class FuturesImmediateTradeProbabilityAnalyzer
{
    private const decimal DefaultFuturesFeeRatePercent = 0.06m;

    public static FuturesImmediateTradeProbabilityView Analyze(FuturesImmediateTradeProbabilityInput input)
    {
        var score = 50m;
        var reasons = new List<string>();
        var trigger = ResolveEntryTrigger(input);
        if (trigger.IsTriggered)
        {
            score += 24m;
            reasons.Add($"immediate trigger ready: {trigger.Reason}");
        }
        else
        {
            score -= Math.Clamp(trigger.DistancePercent * 25m, 8m, 35m);
            reasons.Add($"immediate trigger waiting: {trigger.Reason}");
        }

        var expectedMovePercent = ResolveExpectedMovePercent(input);
        var expectedGross = input.EntryNotionalUsdt * expectedMovePercent / 100m;
        var fees = input.EntryNotionalUsdt * DefaultFuturesFeeRatePercent / 100m * 2m;
        var spreadCost = input.EntryNotionalUsdt * input.SpreadPercent / 100m;
        var required = fees + spreadCost + input.MinNetProfitUsdt;
        if (expectedGross >= required)
        {
            score += 18m;
            reasons.Add($"immediate net ok: gross {expectedGross:0.######} >= required {required:0.######}");
        }
        else
        {
            score -= 22m;
            reasons.Add($"immediate net weak: gross {expectedGross:0.######} < required {required:0.######}");
        }

        if (input.SpreadPercent > 0.15m)
        {
            score -= 10m;
            reasons.Add($"immediate spread wide {input.SpreadPercent:0.###}%");
        }

        if (input.Position is { Size: > 0m } position)
        {
            if (IsOppositePosition(position.Side, input.Direction))
            {
                score -= 45m;
                reasons.Add($"immediate blocked: opposite position {position.Side}");
            }
            else if (input.MaxNotionalUsdt > 0m && position.PositionValueUsdt + input.EntryNotionalUsdt > input.MaxNotionalUsdt)
            {
                score -= 35m;
                reasons.Add("immediate blocked: position capacity full");
            }
            else
            {
                score += 5m;
                reasons.Add($"immediate compatible with open position {position.Side}");
            }
        }
        else
        {
            reasons.Add("immediate no open position");
        }

        var activeBlock = input.RecentRiskDecisions
            .Where(decision => !decision.IsAllowed)
            .Where(decision => DateTimeOffset.UtcNow - decision.CreatedAt <= TimeSpan.FromMinutes(2))
            .Where(IsBlockingSource)
            .OrderByDescending(decision => decision.CreatedAt)
            .FirstOrDefault();
        if (activeBlock is not null)
        {
            score -= 35m;
            reasons.Add($"immediate blocked by {activeBlock.Source}: {activeBlock.Reason}");
        }

        score = Math.Clamp(decimal.Round(score, 2, MidpointRounding.AwayFromZero), 0m, 100m);
        return new FuturesImmediateTradeProbabilityView(score, ResolveLabel(score), reasons);
    }

    private static FuturesEntryTriggerView ResolveEntryTrigger(FuturesImmediateTradeProbabilityInput input)
    {
        if (input.LastPrice <= 0m)
        {
            return new FuturesEntryTriggerView(false, 100m, "no last price");
        }

        return input.Strategy switch
        {
            FuturesStrategyType.GridLongOnly => DistanceTrigger(input.LastPrice <= input.Support + input.Range * 0.35m, input.LastPrice, input.Support + input.Range * 0.35m, "grid long entry band"),
            FuturesStrategyType.GridShortOnly => DistanceTrigger(input.LastPrice >= input.Resistance - input.Range * 0.35m, input.LastPrice, input.Resistance - input.Range * 0.35m, "grid short entry band"),
            FuturesStrategyType.Breakout => DistanceTrigger(input.LastPrice >= input.Resistance, input.LastPrice, input.Resistance, "breakout above resistance"),
            FuturesStrategyType.BreakdownShort => DistanceTrigger(input.LastPrice <= input.Support, input.LastPrice, input.Support, "breakdown below support"),
            FuturesStrategyType.TrendFollow => MomentumTrigger(input.MomentumPercent >= 0.8m, input.MomentumPercent, 0.8m, "long trend continuation"),
            FuturesStrategyType.TrendFollowShortOnly => MomentumTrigger(input.MomentumPercent <= -0.8m, -input.MomentumPercent, 0.8m, "short trend continuation"),
            _ => new FuturesEntryTriggerView(false, 100m, "strategy has no immediate entry trigger")
        };
    }

    private static FuturesEntryTriggerView DistanceTrigger(bool triggered, decimal lastPrice, decimal triggerPrice, string reason)
    {
        var distance = lastPrice > 0m ? Math.Abs(lastPrice - triggerPrice) / lastPrice * 100m : 100m;
        return new FuturesEntryTriggerView(triggered, distance, $"{reason}; distance {distance:0.###}%");
    }

    private static FuturesEntryTriggerView MomentumTrigger(bool triggered, decimal currentMomentum, decimal requiredMomentum, string reason)
    {
        var distance = decimal.Max(0m, requiredMomentum - currentMomentum);
        return new FuturesEntryTriggerView(triggered, distance, $"{reason}; momentum {currentMomentum:0.###}% / {requiredMomentum:0.###}%");
    }

    private static decimal ResolveExpectedMovePercent(FuturesImmediateTradeProbabilityInput input)
    {
        var baseline = decimal.Max(input.AtrPercent, input.VolatilityPercent * 0.25m);
        var directionalMomentum = IsShortStrategy(input.Strategy) ? -input.MomentumPercent : input.MomentumPercent;
        if (input.Strategy is FuturesStrategyType.Breakout or FuturesStrategyType.BreakdownShort or
            FuturesStrategyType.TrendFollow or FuturesStrategyType.TrendFollowShortOnly)
        {
            return decimal.Max(baseline, decimal.Max(0m, directionalMomentum) * 0.85m);
        }

        return baseline;
    }

    private static bool IsOppositePosition(string side, FuturesDirection direction) =>
        (string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) && direction == FuturesDirection.ShortOnly) ||
        (string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) && direction == FuturesDirection.LongOnly);

    private static bool IsBlockingSource(FuturesRiskDecisionRecord decision) =>
        string.Equals(decision.Source, "Risk", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(decision.Source, "StrategyFilter", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(decision.Source, "AggressiveGuard", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(decision.Source, "AggressiveNoTrade", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(decision.Source, "AutoRecommendationSkipped", StringComparison.OrdinalIgnoreCase);

    private static bool IsShortStrategy(FuturesStrategyType strategy) =>
        strategy is FuturesStrategyType.GridShortOnly or FuturesStrategyType.TrendFollowShortOnly or FuturesStrategyType.BreakdownShort;

    private static string ResolveLabel(decimal score) =>
        score switch
        {
            >= 75m => "READY",
            >= 55m => "LIKELY",
            >= 35m => "WAIT",
            _ => "BLOCKED"
        };
}

internal sealed record FuturesImmediateTradeProbabilityInput(
    FuturesStrategyType Strategy,
    FuturesDirection Direction,
    FuturesPositionSnapshot? Position,
    decimal MaxNotionalUsdt,
    decimal EntryNotionalUsdt,
    decimal LastPrice,
    decimal Support,
    decimal Resistance,
    decimal Range,
    decimal AtrPercent,
    decimal VolatilityPercent,
    decimal MomentumPercent,
    decimal SpreadPercent,
    decimal MinNetProfitUsdt,
    IReadOnlyCollection<FuturesRiskDecisionRecord> RecentRiskDecisions);

internal sealed record FuturesImmediateTradeProbabilityView(
    decimal Score,
    string Label,
    IReadOnlyList<string> Reasons);

internal readonly record struct FuturesEntryTriggerView(
    bool IsTriggered,
    decimal DistancePercent,
    string Reason);
