using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Tests;

public sealed class BtdStrategyTests
{
    [Fact]
    public void IsDipTriggered_ReturnsTrue_WhenDrawdownExceedsThreshold()
    {
        var strategy = new BtdStrategy();
        var now = DateTimeOffset.UtcNow;
        var candles = new[]
        {
            new Candle(now.AddMinutes(-2), 1m, 1m, 0.99m, 1m, 1m, 1m),
            new Candle(now.AddMinutes(-1), 1m, 1.02m, 0.98m, 0.99m, 1m, 1m)
        };

        var triggered = strategy.IsDipTriggered(
            new BtdStrategyConfig { DipPercent = 2m, DipLookbackCandles = 5 },
            0.99m,
            candles);

        Assert.True(triggered);
    }

    [Fact]
    public void CanOpenBuy_RespectsMaxActiveBuys()
    {
        var strategy = new BtdStrategy();
        var now = DateTimeOffset.UtcNow;
        var orders = new[]
        {
            new GridOrder
            {
                OrderLinkId = "gbb-test",
                Symbol = "BILLUSDT",
                Category = "spot",
                Side = TradeSide.Buy,
                Price = 0.19m,
                Quantity = 100m,
                Status = OrderStatus.New,
                TradingMode = TradingMode.Paper,
                CreatedAt = now.AddMinutes(-20),
                UpdatedAt = now.AddMinutes(-20)
            }
        };

        var canOpen = strategy.CanOpenBuy(new BtdStrategyConfig { MaxBuys = 1 }, orders, now);

        Assert.False(canOpen);
    }

    [Fact]
    public void IsDipAllowedByPhase_AllowsReversalAfterDump_WhenAggressiveModeActive()
    {
        var strategy = new BtdStrategy();
        var candles = BuildReversalAfterDumpCandles();

        var allowed = strategy.IsDipAllowedByPhase(
            new GridOptions { BtdBlockOnBtcRiskOff = true },
            new MarketPhaseResult { Phase = MarketPhase.Dump, Reason = "Dump detected." },
            candles[^1].Close,
            candles,
            [],
            aggressiveModeActive: true);

        Assert.True(allowed);
    }

    [Fact]
    public void IsDipAllowedByPhase_BlocksReversalAfterDump_WhenBtcRiskOff()
    {
        var strategy = new BtdStrategy();
        var candles = BuildReversalAfterDumpCandles();
        var now = DateTimeOffset.UtcNow;
        var btcCandles = new[]
        {
            new Candle(now.AddMinutes(-3), 100m, 100m, 99m, 100m, 1m, 100m),
            new Candle(now.AddMinutes(-2), 100m, 100m, 97m, 97m, 1m, 97m),
            new Candle(now.AddMinutes(-1), 97m, 97m, 96m, 96m, 1m, 96m)
        };

        var allowed = strategy.IsDipAllowedByPhase(
            new GridOptions
            {
                BtdBlockOnBtcRiskOff = true,
                BtcLookbackCandles = 3,
                BtcMaxMovePercent = 2.5m
            },
            new MarketPhaseResult { Phase = MarketPhase.Dump, Reason = "Dump detected." },
            candles[^1].Close,
            candles,
            btcCandles,
            aggressiveModeActive: true);

        Assert.False(allowed);
    }

    private static IReadOnlyList<Candle> BuildReversalAfterDumpCandles()
    {
        var now = DateTimeOffset.UtcNow;
        var candles = new List<Candle>();
        for (var index = 0; index < 22; index++)
        {
            var close = 0.235m - index * 0.0004m;
            candles.Add(new Candle(now.AddMinutes(index - 60), close, close + 0.001m, close - 0.001m, close, 1000m, close * 1000m));
        }

        for (var index = 0; index < 26; index++)
        {
            var close = 0.226m - index * 0.00255m;
            candles.Add(new Candle(now.AddMinutes(index - 38), close + 0.001m, close + 0.0018m, close - 0.0018m, close, 1200m, close * 1200m));
        }

        var stabilizing = new[]
        {
            (Open: 0.1590m, High: 0.1602m, Low: 0.1580m, Close: 0.1592m, Volume: 1800m),
            (Open: 0.1592m, High: 0.1610m, Low: 0.1587m, Close: 0.1606m, Volume: 2300m),
            (Open: 0.1605m, High: 0.1624m, Low: 0.1598m, Close: 0.1620m, Volume: 2600m),
            (Open: 0.1619m, High: 0.1635m, Low: 0.1609m, Close: 0.1631m, Volume: 2400m),
            (Open: 0.1630m, High: 0.1650m, Low: 0.1622m, Close: 0.1644m, Volume: 2500m),
            (Open: 0.1641m, High: 0.1662m, Low: 0.1634m, Close: 0.1652m, Volume: 2200m),
            (Open: 0.1650m, High: 0.1672m, Low: 0.1642m, Close: 0.1664m, Volume: 2300m),
            (Open: 0.1662m, High: 0.1680m, Low: 0.1655m, Close: 0.1672m, Volume: 2100m),
            (Open: 0.1670m, High: 0.1688m, Low: 0.1662m, Close: 0.1680m, Volume: 2200m),
            (Open: 0.1678m, High: 0.1694m, Low: 0.1670m, Close: 0.1686m, Volume: 2000m)
        };
        for (var index = 0; index < stabilizing.Length; index++)
        {
            var candle = stabilizing[index];
            candles.Add(new Candle(now.AddMinutes(index - stabilizing.Length), candle.Open, candle.High, candle.Low, candle.Close, candle.Volume, candle.Close * candle.Volume));
        }

        return candles;
    }
}
