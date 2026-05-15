using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class TradingCategoryGuardTests
{
    [Fact]
    public void ValidateSpotWorkerCategory_AllowsSpotWhenFuturesDisabled()
    {
        TradingCategoryGuard.ValidateSpotWorkerCategory("spot", futuresEnabled: false);
    }

    [Fact]
    public void ValidateSpotWorkerCategory_BlocksLinearWhenFuturesDisabled()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TradingCategoryGuard.ValidateSpotWorkerCategory("linear", futuresEnabled: false));

        Assert.Contains("FUTURES_ENABLED=true", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSpotWorkerCategory_BlocksLinearInSpotWorkerEvenWhenFuturesEnabled()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TradingCategoryGuard.ValidateSpotWorkerCategory("linear", futuresEnabled: true));

        Assert.Contains("dedicated futures context", exception.Message, StringComparison.Ordinal);
    }
}
