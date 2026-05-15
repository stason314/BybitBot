using BybitGridBot.Strategy;
using BybitGridBot.Domain;

namespace BybitGridBot.Tests;

public sealed class GridStrategyTests
{
    [Fact]
    public void BuildGrid_ReturnsExpectedLevels()
    {
        var strategy = new GridStrategy();
        var options = new GridOptions
        {
            LowerPrice = 2.30m,
            UpperPrice = 2.60m,
            Step = 0.05m
        };

        var levels = strategy.BuildGrid(options);

        Assert.Equal([2.30m, 2.35m, 2.40m, 2.45m, 2.50m, 2.55m, 2.60m], levels.Select(level => level.Price).ToArray());
    }

    [Fact]
    public void GetNextUpperAndLowerLevels_WorkAsExpected()
    {
        var strategy = new GridStrategy();
        var options = new GridOptions
        {
            LowerPrice = 2.30m,
            UpperPrice = 2.60m,
            Step = 0.05m
        };

        var levels = strategy.BuildGrid(options);

        Assert.Equal(2.50m, strategy.GetNextUpperLevel(levels, 2.45m)?.Price);
        Assert.Equal(2.40m, strategy.GetNextLowerLevel(levels, 2.45m)?.Price);
    }

    [Fact]
    public void CreatesGridIntentsOnlyInRangeBound()
    {
        var strategy = new GridStrategy();
        var options = new GridOptions { LowerPrice = 2.30m, UpperPrice = 2.60m, Step = 0.05m, OrderSizeUsdt = 20m, StopLowerPrice = 2.20m, StopUpperPrice = 2.70m };
        var levels = strategy.BuildGrid(options);

        var rangeIntents = strategy.BuildRebalanceIntents(options, levels, 2.45m, [], Phase(MarketPhase.RangeBound), bigRedGuardActive: false);
        var trendIntents = strategy.BuildRebalanceIntents(options, levels, 2.45m, [], Phase(MarketPhase.Uptrend), bigRedGuardActive: false);

        Assert.NotEmpty(rangeIntents);
        Assert.Empty(trendIntents);
    }

    [Fact]
    public void DoesNotCreateBuyIntentsDuringDump()
    {
        var strategy = new GridStrategy();
        var options = new GridOptions { LowerPrice = 2.30m, UpperPrice = 2.60m, Step = 0.05m, OrderSizeUsdt = 20m, StopLowerPrice = 2.20m, StopUpperPrice = 2.70m };
        var levels = strategy.BuildGrid(options);

        var intents = strategy.BuildRebalanceIntents(options, levels, 2.45m, [], Phase(MarketPhase.Dump), bigRedGuardActive: true);

        Assert.Empty(intents);
    }

    [Fact]
    public void DoesHandoffToBreakoutOnBreakoutUp()
    {
        var strategy = new GridStrategy();
        var options = new GridOptions { LowerPrice = 2.30m, UpperPrice = 2.60m, Step = 0.05m, GridHandoffToBreakout = true, EnableBreakoutHandoff = true };

        var canCreate = strategy.CanCreateGridIntents(options, Phase(MarketPhase.BreakoutUp), 2.61m, bigRedGuardActive: false);

        Assert.False(canCreate);
    }

    [Fact]
    public void UsesDynamicStepWhenEnabled()
    {
        var strategy = new GridStrategy();
        var options = new GridOptions
        {
            LowerPrice = 2.0m,
            UpperPrice = 2.2m,
            Step = 0.01m,
            GridDynamicStepEnabled = true,
            GridStepAtrMultiplier = 1m,
            GridMinStepPercent = 1m,
            GridMaxStepPercent = 3m
        };

        var step = strategy.CalculateDynamicStep(options, atr: 0.001m, referencePrice: 2m);

        Assert.Equal(0.02m, step);
    }

    private static MarketPhaseResult Phase(MarketPhase phase) => new()
    {
        Phase = phase,
        Confidence = 0.8m,
        Score = 80m,
        Reason = phase.ToString(),
        SuggestedStrategy = phase == MarketPhase.RangeBound ? StrategyType.Grid : StrategyType.Pause
    };
}
