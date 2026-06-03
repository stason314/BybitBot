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
        return _router.Decide(candidates);
    }

    private static void AddIfNotNull(List<StrategyCandidate> candidates, StrategyCandidate? candidate)
    {
        if (candidate is not null)
        {
            candidates.Add(candidate);
        }
    }
}
