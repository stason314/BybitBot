using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class StrategyIntentTests
{
    [Fact]
    public void Grid_CreatesBuyAndSellIntents()
    {
        var strategy = new GridStrategy();
        var options = Options();
        var levels = strategy.BuildGrid(options);

        var intents = strategy.BuildRebalanceIntents(options, levels, 2.45m, []);

        Assert.Contains(intents, intent => intent.Side == TradeSide.Buy && intent.Price == 2.40m);
        Assert.Contains(intents, intent => intent.Side == TradeSide.Sell && intent.Price == 2.50m);
    }

    [Fact]
    public void Grid_StopsCreatingOrdersOutsideRange()
    {
        var strategy = new GridStrategy();
        var options = Options();

        var intents = strategy.BuildRebalanceIntents(options, strategy.BuildGrid(options), 2.70m, []);

        Assert.Empty(intents);
    }

    [Fact]
    public void Btd_AllowsDipOnlyInUptrendWithoutRiskOffSignals()
    {
        var strategy = new BtdStrategy();

        Assert.True(strategy.IsDipAllowedByRegime(MarketRegime.Uptrend, []));
        Assert.False(strategy.IsDipAllowedByRegime(MarketRegime.Downtrend, []));
        Assert.False(strategy.IsDipAllowedByRegime(MarketRegime.Uptrend, [new Signal { Type = SignalType.BtcRiskOff }]));
    }

    [Fact]
    public void Dca_RespectsMaxAllocation()
    {
        var strategy = new DcaStrategy();

        Assert.True(strategy.IsAllocationAllowed(90m, 10m, 100m));
        Assert.False(strategy.IsAllocationAllowed(95m, 10m, 100m));
    }

    [Fact]
    public void Breakout_RequiresConfirmationCandles()
    {
        var strategy = new BreakoutStrategy();
        var candles = new[]
        {
            Candle(0, 2.61m),
            Candle(1, 2.62m)
        };

        Assert.True(strategy.HasConfirmation(candles, 2.60m, 2));
        Assert.False(strategy.HasConfirmation(candles, 2.60m, 3));
    }

    [Fact]
    public void Pause_CreatesNoTradeIntents()
    {
        var intents = new PauseStrategy().BuildTradeIntents("risk");

        Assert.Empty(intents);
    }

    private static GridOptions Options() => new()
    {
        Symbol = "TONUSDT",
        LowerPrice = 2.30m,
        UpperPrice = 2.60m,
        Step = 0.05m,
        OrderSizeUsdt = 10m,
        StopLowerPrice = 2.25m,
        StopUpperPrice = 2.65m
    };

    private static Candle Candle(int index, decimal close) =>
        new(DateTimeOffset.UtcNow.AddMinutes(index), close, close + 0.01m, close - 0.01m, close, 100m, 0m);
}
