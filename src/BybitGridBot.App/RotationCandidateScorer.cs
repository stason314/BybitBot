namespace BybitGridBot.App;

internal static class RotationCandidateScorer
{
    public static RotationCandidateScoreView Score(RotationCandidateScoreInput input)
    {
        var score =
            input.ActionabilityScore * 0.30m +
            input.ImmediateTradeProbabilityScore * 0.25m +
            input.MarketFitScore * 0.20m +
            input.StrategyPerformanceScore * 0.125m +
            input.PairEfficiencyScore * 0.125m;

        var reasons = new List<string>
        {
            $"rotation weighted score: action {input.ActionabilityScore:0.#}, immediate {input.ImmediateTradeProbabilityScore:0.#}, market {input.MarketFitScore:0.#}, strategy {input.StrategyPerformanceScore:0.#}, pair {input.PairEfficiencyScore:0.#}"
        };

        if (input.ActionabilityScore < 35m)
        {
            score -= 12m;
            reasons.Add("rotation rejected: actionability too weak");
        }

        if (input.ImmediateTradeProbabilityScore < 35m)
        {
            score -= 10m;
            reasons.Add("rotation rejected: immediate trade probability too low");
        }

        if (input.PairEfficiencyScore < 35m)
        {
            score -= 8m;
            reasons.Add("rotation penalty: pair strategy efficiency cooling");
        }

        if (input.MarketFitScore >= 75m &&
            input.ActionabilityScore >= 55m &&
            input.ImmediateTradeProbabilityScore >= 55m)
        {
            score += 6m;
            reasons.Add("rotation boost: market is tradable now");
        }

        score = Math.Clamp(decimal.Round(score, 2, MidpointRounding.AwayFromZero), 0m, 100m);
        var label = ResolveLabel(score);
        reasons.Add(label switch
        {
            "ROTATE_NOW" => "rotation selected: strong candidate",
            "READY" => "rotation selected: usable candidate",
            "WATCH" => "rotation watch: wait for cleaner trigger",
            _ => "rotation rejected: do not switch yet"
        });

        return new RotationCandidateScoreView(score, label, reasons);
    }

    private static string ResolveLabel(decimal score) =>
        score switch
        {
            >= 80m => "ROTATE_NOW",
            >= 65m => "READY",
            >= 45m => "WATCH",
            _ => "REJECT"
        };
}

internal sealed record RotationCandidateScoreInput(
    decimal MarketFitScore,
    decimal ActionabilityScore,
    decimal ImmediateTradeProbabilityScore,
    decimal StrategyPerformanceScore,
    decimal PairEfficiencyScore);

internal sealed record RotationCandidateScoreView(
    decimal Score,
    string Label,
    IReadOnlyList<string> Reasons);
