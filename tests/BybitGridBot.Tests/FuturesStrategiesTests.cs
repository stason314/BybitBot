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
    public void FuturesGridLongOnly_ClosesLongWhenGridMigratesAgainstPosition()
    {
        var decision = new FuturesGridLongOnly().Decide(Context(
            currentPrice: 85.60m,
            positionSize: 0.2m,
            entryPrice: 86.25m,
            positionSide: "Buy",
            strategyType: FuturesStrategyType.GridLongOnly,
            stopLossPercent: 2m,
            direction: FuturesDirection.LongOnly,
            tickSize: 0.01m,
            qtyStep: 0.1m,
            minOrderQty: 0.1m,
            candles: GridMigratedDownCandles()));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.CloseLong, intent.Action);
        Assert.Equal("grid-invalidation", intent.Reason);
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
    public void FuturesGridLongOnly_ClosesOpenShortBeforeLongEntry()
    {
        var decision = new FuturesGridLongOnly().Decide(Context(
            currentPrice: 50020m,
            positionSize: 0.001m,
            entryPrice: 50100m,
            positionSide: "Sell",
            direction: FuturesDirection.LongAndShort,
            aggressiveModeEnabled: true));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.CloseShort, intent.Action);
        Assert.Equal("position-side-recovery", intent.Reason);
    }

    [Fact]
    public void FuturesRouter_UsesShortCompatibleStrategyForOpenShort()
    {
        var router = Router();
        var decision = router.Decide(Context(
            currentPrice: 51200m,
            positionSize: 0.001m,
            entryPrice: 50100m,
            positionSide: "Sell",
            direction: FuturesDirection.LongAndShort,
            aggressiveModeEnabled: true));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.CloseShort, intent.Action);
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
    public void FuturesTrendFollowLongOnly_UsesInstrumentMinimumQuantity()
    {
        var decision = new FuturesTrendFollowLongOnly().Decide(Context(
            currentPrice: 51000m,
            strategyConfigJson: """{"entryNotionalUsdt":1}""",
            qtyStep: 0.001m,
            minOrderQty: 0.001m,
            minOrderAmount: 5m));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(0.001m, intent.Quantity);
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
    public void FuturesGridShortOnly_ClosesOpenLongBeforeShortEntry()
    {
        var decision = new FuturesGridShortOnly().Decide(Context(
            currentPrice: 50140m,
            positionSize: 0.001m,
            entryPrice: 50000m,
            direction: FuturesDirection.LongAndShort,
            aggressiveModeEnabled: true));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.CloseLong, intent.Action);
        Assert.Equal("position-side-recovery", intent.Reason);
    }

    [Fact]
    public void FuturesGridShortOnly_TakesPartialProfitBeforeWideTakeProfit()
    {
        var decision = new FuturesGridShortOnly().Decide(Context(
            currentPrice: 87.44m,
            positionSize: 0.2m,
            entryPrice: 88.82m,
            positionSide: "Sell",
            direction: FuturesDirection.ShortOnly,
            tickSize: 0.01m,
            qtyStep: 0.1m,
            minOrderQty: 0.1m,
            candles: SolCandles()));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.CloseShort, intent.Action);
        Assert.Equal("partial-take-profit", intent.Reason);
        Assert.Equal(0.1m, intent.Quantity);
    }

    [Fact]
    public void FuturesGridShortOnly_DoesNotPartialCloseBelowMinimumRemainingSize()
    {
        var decision = new FuturesGridShortOnly().Decide(Context(
            currentPrice: 87.44m,
            positionSize: 0.1m,
            entryPrice: 88.82m,
            positionSide: "Sell",
            direction: FuturesDirection.ShortOnly,
            tickSize: 0.01m,
            qtyStep: 0.1m,
            minOrderQty: 0.1m,
            candles: SolCandles()));

        Assert.Empty(decision.TradeIntents);
    }

    [Fact]
    public void FuturesGridShortOnly_ClosesShortWhenGridMigratesAgainstPosition()
    {
        var decision = new FuturesGridShortOnly().Decide(Context(
            currentPrice: 86.91m,
            positionSize: 0.2m,
            entryPrice: 86.248125m,
            positionSide: "Sell",
            strategyType: FuturesStrategyType.GridShortOnly,
            stopLossPercent: 2m,
            direction: FuturesDirection.ShortOnly,
            tickSize: 0.01m,
            qtyStep: 0.1m,
            minOrderQty: 0.1m,
            candles: GridMigratedUpCandles()));

        var intent = Assert.Single(decision.TradeIntents);
        Assert.Equal(FuturesTradeAction.CloseShort, intent.Action);
        Assert.Equal("grid-invalidation", intent.Reason);
    }

    [Fact]
    public void FuturesTrendFollowShortOnly_BlocksScaleInAfterReboundFromSupport()
    {
        var decision = new FuturesTrendFollowShortOnly().Decide(Context(
            currentPrice: 87.44m,
            positionSize: 0.1m,
            entryPrice: 88.82m,
            positionSide: "Sell",
            direction: FuturesDirection.ShortOnly,
            aggressiveModeEnabled: true,
            aggressiveModeKind: FuturesAggressiveModeKind.Test,
            tickSize: 0.01m,
            qtyStep: 0.1m,
            minOrderQty: 0.1m,
            candles: SolCandles()));

        Assert.Empty(decision.TradeIntents);
        Assert.Equal("Futures short scale-in blocked after rebound from support.", decision.Reason);
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
        FuturesStrategyType strategyType = FuturesStrategyType.TrendFollow,
        FuturesDirection direction = FuturesDirection.LongOnly,
        string strategyConfigJson = "{}",
        decimal tickSize = 0.1m,
        decimal qtyStep = 0.001m,
        decimal minOrderQty = 0.001m,
        decimal minOrderAmount = 5m,
        decimal stopLossPercent = 1m,
        bool aggressiveModeEnabled = false,
        FuturesAggressiveModeKind aggressiveModeKind = FuturesAggressiveModeKind.Normal,
        IReadOnlyList<Candle>? candles = null) => new()
    {
        Settings = new FuturesBotSettings
        {
            Symbol = "BTCUSDT",
            Category = "linear",
            StrategyType = strategyType,
            StrategyConfigJson = strategyConfigJson,
            Leverage = 2m,
            MarginMode = FuturesMarginMode.Isolated,
            PositionMode = FuturesPositionMode.OneWay,
            Direction = direction,
            MaxNotionalUsdt = 100m,
            MaxMarginUsdt = 50m,
            StopLossPercent = stopLossPercent,
            TakeProfitPercent = 3m,
            AggressiveModeEnabled = aggressiveModeEnabled,
            AggressiveModeKind = aggressiveModeKind
        },
        Candles = candles ?? Candles(),
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
            TickSize = tickSize,
            QtyStep = qtyStep,
            MinOrderQty = minOrderQty,
            MinOrderAmount = minOrderAmount
        }
    };

    private static FuturesStrategyRouter Router() => new(
    [
        new FuturesPause(),
        new FuturesReduceOnly(),
        new FuturesTrendFollowLongOnly(),
        new FuturesBreakoutLongOnly(),
        new FuturesGridLongOnly(),
        new FuturesTrendFollowShortOnly(),
        new FuturesBreakdownShortOnly(),
        new FuturesGridShortOnly()
    ]);

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

    private static IReadOnlyList<Candle> SolCandles()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-60);
        return Enumerable.Range(0, 60)
            .Select(index =>
            {
                var open = 88.28m - index * 0.01m;
                var close = index == 59 ? 87.44m : open - 0.01m;
                return new Candle(start.AddMinutes(index), open, 88.43m, 86.65m, close, 1m, close);
            })
            .ToArray();
    }

    private static IReadOnlyList<Candle> GridMigratedUpCandles()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-60);
        return Enumerable.Range(0, 60)
            .Select(index =>
            {
                var close = index == 59 ? 86.91m : 86.82m + index * 0.001m;
                return new Candle(start.AddMinutes(index), 86.82m, 87.08m, 86.74m, close, 1m, close);
            })
            .ToArray();
    }

    private static IReadOnlyList<Candle> GridMigratedDownCandles()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-60);
        return Enumerable.Range(0, 60)
            .Select(index =>
            {
                var close = index == 59 ? 85.60m : 85.92m - index * 0.001m;
                return new Candle(start.AddMinutes(index), 85.92m, 86.00m, 85.55m, close, 1m, close);
            })
            .ToArray();
    }
}
