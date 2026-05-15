using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class StrategyRouter
{
    private StrategyType? _currentStrategy;
    private DateTimeOffset? _lastSwitchAt;

    public BybitGridBot.Domain.StrategyDecision SelectStrategy(
        BotType botType,
        MarketRegime regime,
        IReadOnlyList<StrategyScore> scores,
        GridOptions options,
        DateTimeOffset now)
    {
        var target = ResolveTarget(botType, regime, options.SpotOnly);
        var candidates = scores
            .Where(score => score.IsAllowed &&
                            score.Score >= options.StrategyMinScore &&
                            score.Confidence >= options.StrategyMinConfidence)
            .OrderByDescending(score => score.Score)
            .ThenByDescending(score => score.Confidence)
            .ToArray();

        var selected = botType is BotType.Hybrid or BotType.Combo
            ? candidates.FirstOrDefault()?.StrategyType ?? StrategyType.Pause
            : target == StrategyType.Pause
            ? StrategyType.Pause
            : candidates.FirstOrDefault(score => score.StrategyType == target)?.StrategyType
              ?? candidates.FirstOrDefault()?.StrategyType
              ?? StrategyType.Pause;

        var selectedScore = scores.FirstOrDefault(score => score.StrategyType == selected);
        if (selected != StrategyType.Pause &&
            selectedScore is not null &&
            (selectedScore.Score < options.StrategyMinScore || selectedScore.Confidence < options.StrategyMinConfidence))
        {
            selected = StrategyType.Pause;
        }

        var reason = ResolveReason(botType, regime, selected, selectedScore);
        var isSwitch = _currentStrategy.HasValue && _currentStrategy.Value != selected;
        if (isSwitch && IsCooldownActive(options, now))
        {
            var currentScore = scores.FirstOrDefault(score => score.StrategyType == _currentStrategy.Value);
            if (currentScore is not null &&
                currentScore.IsAllowed &&
                currentScore.Score >= options.StrategyMinScore &&
                selectedScore is not null &&
                selectedScore.Score < currentScore.Score + 15m)
            {
                selected = _currentStrategy.Value;
                isSwitch = false;
                reason = $"Strategy switch blocked by cooldown. Keeping {selected}.";
            }
        }

        if (!_currentStrategy.HasValue || _currentStrategy.Value != selected)
        {
            isSwitch = _currentStrategy.HasValue;
            _currentStrategy = selected;
            _lastSwitchAt = now;
        }

        return new BybitGridBot.Domain.StrategyDecision
        {
            SelectedStrategy = selected,
            MarketRegime = regime,
            MarketPhase = MarketPhase.Unknown,
            Scores = scores,
            Reason = reason,
            IsSwitch = isSwitch,
            CreatedAt = now
        };
    }

    public BybitGridBot.Domain.StrategyDecision SelectStrategy(
        BotType botType,
        MarketPhaseResult phase,
        IReadOnlyList<StrategyScore> scores,
        GridOptions options,
        DateTimeOffset now)
    {
        var target = ResolveTarget(botType, phase, options.SpotOnly);
        var minScore = EffectiveMinStrategyScore(options);
        var minConfidence = Math.Max(options.StrategyMinConfidence, options.MinPhaseConfidence);
        var candidates = scores
            .Where(score => score.IsAllowed &&
                            score.Score >= minScore &&
                            score.Confidence >= minConfidence)
            .OrderByDescending(score => score.Score)
            .ThenByDescending(score => score.Confidence)
            .ToArray();

        var selected = botType is BotType.Hybrid or BotType.Combo
            ? target == StrategyType.Pause ? StrategyType.Pause : target
            : target;

        var selectedScore = scores.FirstOrDefault(score => score.StrategyType == selected);
        if (selected != StrategyType.Pause &&
            (phase.Confidence < options.MinPhaseConfidence ||
             phase.Score < minScore ||
             selectedScore is null ||
             selectedScore.Score < minScore ||
             selectedScore.Confidence < options.StrategyMinConfidence))
        {
            selected = candidates.FirstOrDefault()?.StrategyType ?? StrategyType.Pause;
            selectedScore = scores.FirstOrDefault(score => score.StrategyType == selected);
        }

        var reason = ResolveReason(botType, phase, selected, selectedScore);
        var isSwitch = _currentStrategy.HasValue && _currentStrategy.Value != selected;
        var immediatePause = phase.Phase is MarketPhase.Dump or MarketPhase.HighVolatility or MarketPhase.BreakoutDown;
        if (isSwitch && !immediatePause && IsCooldownActive(options, now))
        {
            var currentScore = scores.FirstOrDefault(score => score.StrategyType == _currentStrategy.Value);
            if (currentScore is not null &&
                currentScore.IsAllowed &&
                currentScore.Score >= minScore &&
                selectedScore is not null &&
                selectedScore.Score < currentScore.Score + 15m)
            {
                selected = _currentStrategy.Value;
                isSwitch = false;
                reason = $"Strategy switch blocked by cooldown. Keeping {selected}. Phase: {phase.Phase}.";
            }
        }

        if (!_currentStrategy.HasValue || _currentStrategy.Value != selected)
        {
            isSwitch = _currentStrategy.HasValue;
            _currentStrategy = selected;
            _lastSwitchAt = now;
        }

        return new BybitGridBot.Domain.StrategyDecision
        {
            SelectedStrategy = selected,
            MarketRegime = ToLegacyRegime(phase.Phase),
            MarketPhase = phase.Phase,
            Scores = scores,
            Reason = reason,
            IsSwitch = isSwitch,
            CreatedAt = now
        };
    }

    private static StrategyType ResolveTarget(BotType botType, MarketRegime regime, bool spotOnly)
    {
        return botType switch
        {
            BotType.Dca => StrategyType.Dca,
            BotType.Btd => StrategyType.Btd,
            BotType.Grid => StrategyType.Grid,
            BotType.Breakout => StrategyType.Breakout,
            BotType.Trend => StrategyType.TrendFollowing,
            BotType.Pause => StrategyType.Pause,
            BotType.Signal => StrategyType.Pause,
            BotType.Auto => regime switch
            {
                MarketRegime.RangeBound => StrategyType.Grid,
                MarketRegime.Uptrend => StrategyType.TrendFollowing,
                MarketRegime.BreakoutUp => StrategyType.Breakout,
                MarketRegime.Downtrend when spotOnly => StrategyType.Pause,
                MarketRegime.BreakoutDown when spotOnly => StrategyType.Pause,
                MarketRegime.HighVolatility => StrategyType.Pause,
                MarketRegime.Unknown => StrategyType.Pause,
                _ => StrategyType.Pause
            },
            BotType.Hybrid or BotType.Combo => StrategyType.Pause,
            _ => StrategyType.Pause
        };
    }

    private static StrategyType ResolveTarget(BotType botType, MarketPhaseResult phase, bool spotOnly)
    {
        return botType switch
        {
            BotType.Dca => StrategyType.Dca,
            BotType.Btd => StrategyType.Btd,
            BotType.Grid => StrategyType.Grid,
            BotType.Breakout => StrategyType.Breakout,
            BotType.Trend => StrategyType.TrendFollowing,
            BotType.Pause => StrategyType.Pause,
            BotType.Signal => StrategyType.Pause,
            BotType.Auto or BotType.Hybrid or BotType.Combo => phase.Phase switch
            {
                MarketPhase.Uptrend => StrategyType.TrendFollowing,
                MarketPhase.PullbackInUptrend => StrategyType.Btd,
                MarketPhase.RangeBound => StrategyType.Grid,
                MarketPhase.BreakoutUp => StrategyType.Breakout,
                MarketPhase.BreakoutDown when spotOnly => StrategyType.Pause,
                MarketPhase.Dump => StrategyType.Pause,
                MarketPhase.HighVolatility => StrategyType.Pause,
                MarketPhase.Exhaustion => StrategyType.Pause,
                MarketPhase.Unknown => StrategyType.Pause,
                _ => StrategyType.Pause
            },
            _ => StrategyType.Pause
        };
    }

    private bool IsCooldownActive(GridOptions options, DateTimeOffset now)
    {
        return _lastSwitchAt.HasValue &&
               now - _lastSwitchAt.Value < TimeSpan.FromMinutes(Math.Max(0, options.StrategySwitchCooldownMinutes));
    }

    private static string ResolveReason(BotType botType, MarketRegime regime, StrategyType selected, StrategyScore? selectedScore)
    {
        if (botType == BotType.Signal)
        {
            return "Signal mode is diagnostics-only by default; no strategy selected.";
        }

        if (selected == StrategyType.Pause)
        {
            return $"Paused for regime {regime} or insufficient strategy score.";
        }

        return selectedScore is null
            ? $"Selected {selected} for regime {regime}."
            : $"Selected {selected} for regime {regime}. Score: {selectedScore.Score}, confidence: {selectedScore.Confidence}.";
    }

    private static string ResolveReason(BotType botType, MarketPhaseResult phase, StrategyType selected, StrategyScore? selectedScore)
    {
        if (botType == BotType.Signal)
        {
            return "Signal mode is diagnostics-only by default; no strategy selected.";
        }

        if (selected == StrategyType.Pause)
        {
            return $"Paused for phase {phase.Phase}. Confidence: {phase.Confidence}, score: {phase.Score}. Reason: {phase.Reason}";
        }

        return selectedScore is null
            ? $"Selected {selected} for phase {phase.Phase}. Reason: {phase.Reason}"
            : $"Selected {selected} for phase {phase.Phase}. Phase score: {phase.Score}, strategy score: {selectedScore.Score}, confidence: {selectedScore.Confidence}.";
    }

    private static decimal EffectiveMinStrategyScore(GridOptions options)
    {
        return Math.Max(options.StrategyMinScore, options.MinStrategyScore);
    }

    private static MarketRegime ToLegacyRegime(MarketPhase phase)
    {
        return phase switch
        {
            MarketPhase.RangeBound => MarketRegime.RangeBound,
            MarketPhase.Uptrend or MarketPhase.PullbackInUptrend or MarketPhase.Exhaustion => MarketRegime.Uptrend,
            MarketPhase.BreakoutUp => MarketRegime.BreakoutUp,
            MarketPhase.BreakoutDown or MarketPhase.Dump => MarketRegime.BreakoutDown,
            MarketPhase.HighVolatility => MarketRegime.HighVolatility,
            _ => MarketRegime.Unknown
        };
    }
}
