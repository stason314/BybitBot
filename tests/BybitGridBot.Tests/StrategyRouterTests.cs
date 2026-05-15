using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class StrategyRouterTests
{
    [Fact]
    public void Auto_SelectsGrid_ForRangeBound()
    {
        var decision = Select(MarketRegime.RangeBound);

        Assert.Equal(StrategyType.Grid, decision.SelectedStrategy);
    }

    [Fact]
    public void Auto_SelectsBreakout_ForBreakoutUp()
    {
        var decision = Select(MarketRegime.BreakoutUp);

        Assert.Equal(StrategyType.Breakout, decision.SelectedStrategy);
    }

    [Fact]
    public void Auto_SelectsTrend_ForUptrend()
    {
        var decision = Select(MarketRegime.Uptrend);

        Assert.Equal(StrategyType.TrendFollowing, decision.SelectedStrategy);
    }

    [Fact]
    public void Auto_SelectsPause_ForDowntrendInSpotOnly()
    {
        var decision = Select(MarketRegime.Downtrend);

        Assert.Equal(StrategyType.Pause, decision.SelectedStrategy);
    }

    [Fact]
    public void DoesNotSwitchDuringCooldown_WhenNewScoreIsNotMuchBetter()
    {
        var router = new StrategyRouter();
        var options = WithSwitchCooldown(Options(), 30);
        var now = DateTimeOffset.UtcNow;
        router.SelectStrategy(BotType.Auto, MarketRegime.RangeBound, Scores(grid: 80m, breakout: 70m, trend: 70m), options, now);

        var decision = router.SelectStrategy(BotType.Auto, MarketRegime.BreakoutUp, Scores(grid: 75m, breakout: 85m, trend: 70m), options, now.AddMinutes(5));

        Assert.Equal(StrategyType.Grid, decision.SelectedStrategy);
    }

    [Fact]
    public void SwitchesAfterCooldown_WhenNewScoreIsBetter()
    {
        var router = new StrategyRouter();
        var options = WithSwitchCooldown(Options(), 30);
        var now = DateTimeOffset.UtcNow;
        router.SelectStrategy(BotType.Auto, MarketRegime.RangeBound, Scores(grid: 80m, breakout: 70m, trend: 70m), options, now);

        var decision = router.SelectStrategy(BotType.Auto, MarketRegime.BreakoutUp, Scores(grid: 70m, breakout: 90m, trend: 70m), options, now.AddMinutes(31));

        Assert.Equal(StrategyType.Breakout, decision.SelectedStrategy);
    }

    private static BybitGridBot.Domain.StrategyDecision Select(MarketRegime regime)
    {
        return new StrategyRouter().SelectStrategy(BotType.Auto, regime, Scores(), Options(), DateTimeOffset.UtcNow);
    }

    private static GridOptions Options() => new()
    {
        StrategyMinScore = 65m,
        StrategyMinConfidence = 0.6m,
        StrategySwitchCooldownMinutes = 30,
        SpotOnly = true
    };

    private static GridOptions WithSwitchCooldown(GridOptions options, int minutes) => new()
    {
        StrategyMinScore = options.StrategyMinScore,
        StrategyMinConfidence = options.StrategyMinConfidence,
        StrategySwitchCooldownMinutes = minutes,
        SpotOnly = options.SpotOnly
    };

    private static IReadOnlyList<StrategyScore> Scores(decimal grid = 80m, decimal breakout = 85m, decimal trend = 78m) =>
    [
        new StrategyScore { StrategyType = StrategyType.Grid, Score = grid, Confidence = 0.8m, IsAllowed = true, Reason = "grid" },
        new StrategyScore { StrategyType = StrategyType.Breakout, Score = breakout, Confidence = 0.8m, IsAllowed = true, Reason = "breakout" },
        new StrategyScore { StrategyType = StrategyType.TrendFollowing, Score = trend, Confidence = 0.8m, IsAllowed = true, Reason = "trend" }
    ];
}
