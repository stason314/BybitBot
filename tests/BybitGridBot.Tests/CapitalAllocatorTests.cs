using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class CapitalAllocatorTests
{
    [Fact]
    public void Allocate_BlocksStrategyAboveAllocationPercent()
    {
        var allocation = new CapitalAllocator().Allocate(Options(), StrategyType.Grid, 450m, 1000m, 800m, 0m, 0m);

        Assert.False(allocation.IsAllowed);
        Assert.Contains("Strategy allocation", allocation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Allocate_KeepsUsdtReserve()
    {
        var allocation = new CapitalAllocator().Allocate(Options(), StrategyType.Btd, 150m, 1000m, 300m, 0m, 0m);

        Assert.False(allocation.IsAllowed);
        Assert.Contains("reserve", allocation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Allocate_BlocksTotalExposureAboveMax()
    {
        var allocation = new CapitalAllocator().Allocate(Options(), StrategyType.Btd, 100m, 1000m, 500m, 750m, 0m);

        Assert.False(allocation.IsAllowed);
        Assert.Contains("MAX_TOTAL_EXPOSURE_PERCENT", allocation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Allocate_AllowsValidAllocation()
    {
        var allocation = new CapitalAllocator().Allocate(Options(), StrategyType.Btd, 100m, 1000m, 600m, 300m, 50m);

        Assert.True(allocation.IsAllowed);
        Assert.Equal(100m, allocation.AllocatedUsdt);
    }

    private static GridOptions Options() => new()
    {
        GridCapitalPercent = 40m,
        BtdCapitalPercent = 20m,
        MinUsdtReservePercent = 20m,
        MaxTotalExposurePercent = 80m
    };
}
