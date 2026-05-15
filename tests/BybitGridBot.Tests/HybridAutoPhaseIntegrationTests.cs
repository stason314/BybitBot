using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class HybridAutoPhaseIntegrationTests
{
    [Fact]
    public void HybridAutoShouldSwitchStrategiesAcrossMarketPhases()
    {
        var router = new StrategyRouter();
        var options = PriceActionPhaseDetectorTests.Options(0.2m, 0.222m);
        var now = DateTimeOffset.UtcNow;
        var scores = Scores();

        var trend = router.SelectStrategy(BotType.Auto, Phase(MarketPhase.Uptrend, 80m, 0.75m), scores, options, now);
        var range = router.SelectStrategy(BotType.Auto, Phase(MarketPhase.RangeBound, 78m, 0.7m), scores, options, now.AddMinutes(31));
        var breakout = router.SelectStrategy(BotType.Auto, Phase(MarketPhase.BreakoutUp, 88m, 0.82m), scores, options, now.AddMinutes(62));
        var dump = router.SelectStrategy(BotType.Auto, Phase(MarketPhase.Dump, 95m, 0.9m), scores, options, now.AddMinutes(63));

        Assert.Equal(StrategyType.TrendFollowing, trend.SelectedStrategy);
        Assert.Equal(StrategyType.Grid, range.SelectedStrategy);
        Assert.Equal(StrategyType.Breakout, breakout.SelectedStrategy);
        Assert.Equal(StrategyType.Pause, dump.SelectedStrategy);
    }

    private static MarketPhaseResult Phase(MarketPhase phase, decimal score, decimal confidence) => new()
    {
        Phase = phase,
        Score = score,
        Confidence = confidence,
        Reason = phase.ToString(),
        DetectedAt = DateTimeOffset.UtcNow,
        SuggestedStrategy = phase switch
        {
            MarketPhase.Uptrend => StrategyType.TrendFollowing,
            MarketPhase.PullbackInUptrend => StrategyType.Btd,
            MarketPhase.RangeBound => StrategyType.Grid,
            MarketPhase.BreakoutUp => StrategyType.Breakout,
            _ => StrategyType.Pause
        }
    };

    private static IReadOnlyList<StrategyScore> Scores() =>
    [
        new StrategyScore { StrategyType = StrategyType.Grid, Score = 80m, Confidence = 0.8m, IsAllowed = true, Reason = "grid" },
        new StrategyScore { StrategyType = StrategyType.Btd, Score = 78m, Confidence = 0.8m, IsAllowed = true, Reason = "btd" },
        new StrategyScore { StrategyType = StrategyType.Breakout, Score = 88m, Confidence = 0.8m, IsAllowed = true, Reason = "breakout" },
        new StrategyScore { StrategyType = StrategyType.TrendFollowing, Score = 82m, Confidence = 0.8m, IsAllowed = true, Reason = "trend" }
    ];
}
