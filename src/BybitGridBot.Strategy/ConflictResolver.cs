using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class ConflictResolver
{
    public IReadOnlyList<TradeIntent> Resolve(IReadOnlyList<TradeIntent> intents, IReadOnlyList<StrategyScore> scores)
    {
        return intents
            .GroupBy(intent => intent.Symbol, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var sides = group.Select(intent => intent.Side).Distinct().ToArray();
                if (sides.Length <= 1)
                {
                    return group;
                }

                var bestScore = scores
                    .Where(score => group.Any(intent => intent.StrategyType == score.StrategyType))
                    .OrderByDescending(score => score.Score)
                    .FirstOrDefault();

                return bestScore is null
                    ? Array.Empty<TradeIntent>()
                    : group.Where(intent => intent.StrategyType == bestScore.StrategyType);
            })
            .ToArray();
    }
}
