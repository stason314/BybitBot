using BybitGridBot.Strategy;

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
}
