namespace BybitGridBot.App;

public sealed class ScoreBasedSignalEngine
{
    private readonly BreakoutClassifier _classifier;
    private readonly NYSweepReversalStrategy _sweep;
    private readonly TurtleTrendStrategy _turtle;
    private readonly BreakoutRetestStrategy _retest;
    private readonly NyStrategyRouter _router;

    public ScoreBasedSignalEngine(
        BreakoutClassifier classifier,
        NYSweepReversalStrategy sweep,
        TurtleTrendStrategy turtle,
        BreakoutRetestStrategy retest,
        NyStrategyRouter router)
    {
        _classifier = classifier;
        _sweep = sweep;
        _turtle = turtle;
        _retest = retest;
        _router = router;
    }

    public StrategyDecision Decide(NyStrategyContext context)
    {
        if (context.FiveMinuteCandles.Count < 2)
        {
            return new PauseStrategy().Decide(StrategyNoTradeReason.NotEnoughData, "Not enough 5m candles.", []);
        }

        var breakout = _classifier.Classify(context);
        var candidates = new List<StrategyCandidate>();
        AddIfNotNull(candidates, _sweep.BuildCandidate(context, breakout));
        AddIfNotNull(candidates, _turtle.BuildCandidate(context, breakout));
        AddIfNotNull(candidates, _retest.BuildCandidate(context, breakout));
        return AttachBreakout(_router.Decide(candidates), breakout);
    }

    private static StrategyDecision AttachBreakout(StrategyDecision decision, BreakoutClassifierResult breakout) => new()
    {
        SelectedStrategy = decision.SelectedStrategy,
        SelectedCandidate = decision.SelectedCandidate,
        AllCandidates = decision.AllCandidates,
        RejectedCandidates = decision.RejectedCandidates,
        NoTradeReason = decision.NoTradeReason,
        Reason = decision.Reason,
        IsTradeAllowed = decision.IsTradeAllowed,
        CreatedAt = decision.CreatedAt,
        BreakoutClassification = breakout.Classification,
        BreakoutSide = breakout.BreakoutSide
    };

    private static void AddIfNotNull(List<StrategyCandidate> candidates, StrategyCandidate? candidate)
    {
        if (candidate is not null)
        {
            candidates.Add(candidate);
        }
    }
}
