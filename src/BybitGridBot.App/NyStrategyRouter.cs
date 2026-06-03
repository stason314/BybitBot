namespace BybitGridBot.App;

public sealed class NyStrategyRouter
{
    private readonly StrategyRoutingOptions _options;
    private readonly ConflictResolver _conflicts;
    private readonly PauseStrategy _pause = new();

    public NyStrategyRouter(StrategyRoutingOptions options)
    {
        _options = options;
        _conflicts = new ConflictResolver(options);
    }

    public StrategyDecision Decide(IReadOnlyList<StrategyCandidate> candidates)
    {
        var all = candidates.OrderByDescending(candidate => candidate.Score).ToArray();
        var tradable = all
            .Where(candidate => candidate.HasTradeIntent)
            .Where(candidate => candidate.RejectionReason == StrategyNoTradeReason.None)
            .ToArray();
        if (tradable.Length == 0)
        {
            return _pause.Decide(StrategyNoTradeReason.PauseStrategy, "No tradable candidates.", all);
        }

        var conflict = _conflicts.Resolve(tradable);
        if (conflict.ShouldPause)
        {
            return _pause.Decide(conflict.Reason, conflict.Details, all);
        }

        var best = conflict.SelectedCandidate ?? tradable[0];
        if (best.Score < _options.StrategyMinScore)
        {
            return _pause.Decide(StrategyNoTradeReason.LowScore, $"Best score {best.Score:F2} is below {_options.StrategyMinScore:F2}.", all);
        }

        if (best.Confidence < _options.StrategyMinConfidence)
        {
            return _pause.Decide(StrategyNoTradeReason.LowConfidence, $"Best confidence {best.Confidence:F2} is below {_options.StrategyMinConfidence:F2}.", all);
        }

        return new StrategyDecision
        {
            SelectedStrategy = best.StrategyName,
            SelectedCandidate = best,
            AllCandidates = all,
            RejectedCandidates = all.Where(candidate => !ReferenceEquals(candidate, best)).ToArray(),
            NoTradeReason = StrategyNoTradeReason.None,
            Reason = $"Selected {best.StrategyName} {best.Side} score={best.Score:F2}, confidence={best.Confidence:F2}.",
            IsTradeAllowed = true
        };
    }
}

public sealed class ConflictResolver
{
    private readonly StrategyRoutingOptions _options;

    public ConflictResolver(StrategyRoutingOptions options)
    {
        _options = options;
    }

    public ConflictResolution Resolve(IReadOnlyList<StrategyCandidate> candidates)
    {
        var ordered = candidates.OrderByDescending(candidate => candidate.Score).ToArray();
        if (ordered.Length < 2)
        {
            return ConflictResolution.Select(ordered.FirstOrDefault());
        }

        var best = ordered[0];
        var second = ordered[1];
        var opposite = best.Side != StrategySide.None && second.Side != StrategySide.None && best.Side != second.Side;
        if (!opposite)
        {
            return ConflictResolution.Select(best);
        }

        var isSweepTurtleConflict =
            IsSweep(best) && IsTurtle(second) ||
            IsTurtle(best) && IsSweep(second);
        var difference = Math.Abs(best.Score - second.Score);
        if (!_options.AllowConflictedSignals && difference < _options.MinScoreDifference)
        {
            return ConflictResolution.Pause(
                isSweepTurtleConflict ? StrategyNoTradeReason.SweepVsTurtleConflict : StrategyNoTradeReason.ConflictingSignals,
                $"Conflict {best.StrategyName} {best.Side} score={best.Score:F2} vs {second.StrategyName} {second.Side} score={second.Score:F2}; diff={difference:F2} < {_options.MinScoreDifference:F2}.");
        }

        return ConflictResolution.Select(best);
    }

    private static bool IsSweep(StrategyCandidate candidate) =>
        string.Equals(candidate.StrategyName, NYSweepReversalStrategy.Name, StringComparison.OrdinalIgnoreCase);

    private static bool IsTurtle(StrategyCandidate candidate) =>
        string.Equals(candidate.StrategyName, TurtleTrendStrategy.Name, StringComparison.OrdinalIgnoreCase);
}

public sealed class ConflictResolution
{
    public bool ShouldPause { get; private init; }

    public StrategyCandidate? SelectedCandidate { get; private init; }

    public StrategyNoTradeReason Reason { get; private init; }

    public string Details { get; private init; } = string.Empty;

    public static ConflictResolution Select(StrategyCandidate? candidate) => new()
    {
        SelectedCandidate = candidate,
        ShouldPause = false,
        Reason = StrategyNoTradeReason.None
    };

    public static ConflictResolution Pause(StrategyNoTradeReason reason, string details) => new()
    {
        ShouldPause = true,
        Reason = reason,
        Details = details
    };
}
