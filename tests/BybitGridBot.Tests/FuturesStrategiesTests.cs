using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class FuturesStrategiesTests
{
    [Fact]
    public void FuturesPause_ReturnsNoIntents()
    {
        var decision = new FuturesPause().Decide(Context());

        Assert.Empty(decision.TradeIntents);
    }

    [Fact]
    public void FuturesReduceOnly_ClosesOpenLong()
    {
        var decision = new FuturesReduceOnly().Decide(Context(positionSize: 0.02m, entryPrice: 50000m));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.CloseLong, intent.Action);
        Assert.Equal(0, intent.PositionIdx);
    }

    [Fact]
    public void FuturesTrendFollowLongOnly_OpensLongOnPositiveMove()
    {
        var decision = new FuturesTrendFollowLongOnly().Decide(Context(currentPrice: 51000m));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.OpenLong, intent.Action);
        Assert.Equal(0, intent.PositionIdx);
        Assert.NotNull(intent.StopLossPrice);
    }

    [Fact]
    public void FuturesBreakoutLongOnly_OpensLongAtResistance()
    {
        var decision = new FuturesBreakoutLongOnly().Decide(Context(currentPrice: 50900m));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.OpenLong, intent.Action);
    }

    [Fact]
    public void FuturesGridLongOnly_OpensLongNearSupport()
    {
        var decision = new FuturesGridLongOnly().Decide(Context(currentPrice: 50020m));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.OpenLong, intent.Action);
    }

    [Fact]
    public void FuturesGridLongOnly_KeepsExistingLongInConservativeMode()
    {
        var decision = new FuturesGridLongOnly().Decide(Context(currentPrice: 50020m, positionSize: 0.001m, entryPrice: 50100m));

        Assert.Empty(decision.TradeIntents);
    }

    [Fact]
    public void FuturesGridLongOnly_AllowsScaleInInAggressiveMode()
    {
        var decision = new FuturesGridLongOnly().Decide(Context(
            currentPrice: 50020m,
            positionSize: 0.001m,
            entryPrice: 50100m,
            aggressiveModeEnabled: true));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.OpenLong, intent.Action);
    }

    [Fact]
    public void FuturesGridLongOnly_TestModeOpensAwayFromSupport()
    {
        var decision = new FuturesGridLongOnly().Decide(Context(
            currentPrice: 50600m,
            positionSize: 0.001m,
            entryPrice: 50500m,
            aggressiveModeEnabled: true,
            aggressiveModeKind: FuturesAggressiveModeKind.Test));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.OpenLong, intent.Action);
    }

    [Fact]
    public void FuturesTrendFollowShortOnly_OpensShortOnNegativeMove()
    {
        var decision = new FuturesTrendFollowShortOnly().Decide(Context(
            currentPrice: 49000m,
            direction: FuturesDirection.ShortOnly));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.OpenShort, intent.Action);
        Assert.True(intent.StopLossPrice > intent.Price);
        Assert.True(intent.TakeProfitPrice < intent.Price);
    }

    [Fact]
    public void FuturesGridShortOnly_OpensShortNearResistance()
    {
        var decision = new FuturesGridShortOnly().Decide(Context(
            currentPrice: 50140m,
            direction: FuturesDirection.ShortOnly));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.OpenShort, intent.Action);
    }

    [Fact]
    public void FuturesReduceOnly_ClosesOpenShort()
    {
        var decision = new FuturesReduceOnly().Decide(Context(
            currentPrice: 49000m,
            positionSize: 0.01m,
            entryPrice: 50000m,
            positionSide: "Sell",
            direction: FuturesDirection.ShortOnly));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.CloseShort, intent.Action);
    }

    private static FuturesStrategyContext Context(
        decimal currentPrice = 51000m,
        decimal positionSize = 0m,
        decimal entryPrice = 0m,
        string? positionSide = null,
        FuturesDirection direction = FuturesDirection.LongOnly,
        bool aggressiveModeEnabled = false,
        FuturesAggressiveModeKind aggressiveModeKind = FuturesAggressiveModeKind.Normal) => new()
    {
        Settings = new FuturesBotSettings
        {
            Symbol = "BTCUSDT",
            Category = "linear",
            StrategyType = FuturesStrategyType.TrendFollow,
            Leverage = 2m,
            MarginMode = FuturesMarginMode.Isolated,
            PositionMode = FuturesPositionMode.OneWay,
            Direction = direction,
            MaxNotionalUsdt = 100m,
            MaxMarginUsdt = 50m,
            StopLossPercent = 1m,
            TakeProfitPercent = 2m,
            AggressiveModeEnabled = aggressiveModeEnabled,
            AggressiveModeKind = aggressiveModeKind
        },
        Candles = Candles(),
        CurrentPrice = currentPrice,
        Position = new FuturesPositionSnapshot
        {
            Symbol = "BTCUSDT",
            Category = "linear",
            Side = positionSize > 0m ? positionSide ?? "Buy" : "None",
            Size = positionSize,
            EntryPrice = entryPrice,
            MarkPrice = currentPrice,
            Leverage = 2m
        },
        Instrument = new FuturesInstrumentRules
        {
            TickSize = 0.1m,
            QtyStep = 0.001m,
            MinOrderQty = 0.001m,
            MinOrderAmount = 5m
        }
    };

    private static IReadOnlyList<Candle> Candles()
    {
        var start = DateTimeOffset.UtcNow.AddHours(-4);
        return Enumerable.Range(0, 60)
            .Select(index =>
            {
                var open = 50000m + index;
                return new Candle(start.AddMinutes(index), open, open + 120m, open - 40m, open + 20m, 1m, open);
            })
            .ToArray();
    }
}
