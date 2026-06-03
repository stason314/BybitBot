using BybitGridBot.App;
using BybitGridBot.Domain;

namespace BybitGridBot.Tests;

public sealed class NyRegimeStrategyTests
{
    [Fact]
    public void Router_SelectsTurtleWhenTrueBreakoutScoreHigher()
    {
        var router = new NyStrategyRouter(new StrategyRoutingOptions());
        var decision = router.Decide([
            Candidate(NYSweepReversalStrategy.Name, StrategySide.Short, 78m, 0.75m),
            Candidate(TurtleTrendStrategy.Name, StrategySide.Long, 94m, 0.82m)
        ]);

        Assert.True(decision.IsTradeAllowed);
        Assert.Equal(TurtleTrendStrategy.Name, decision.SelectedStrategy);
    }

    [Fact]
    public void Router_PausesWhenSweepAndTurtleConflictAndScoresClose()
    {
        var router = new NyStrategyRouter(new StrategyRoutingOptions { MinScoreDifference = 15m });
        var decision = router.Decide([
            Candidate(NYSweepReversalStrategy.Name, StrategySide.Short, 78m, 0.75m),
            Candidate(TurtleTrendStrategy.Name, StrategySide.Long, 72m, 0.82m)
        ]);

        Assert.False(decision.IsTradeAllowed);
        Assert.Equal(StrategyNoTradeReason.SweepVsTurtleConflict, decision.NoTradeReason);
    }

    [Fact]
    public void PatternEngine_DetectsBullishEngulfing()
    {
        var engine = new PatternConfirmationEngine(new NySessionBreakoutOptions());
        var now = DateTimeOffset.UtcNow;
        var signals = engine.Detect([
            new Candle(now, 100m, 101m, 97m, 98m, 100m, 0m),
            new Candle(now.AddMinutes(5), 97m, 104m, 96m, 103m, 180m, 0m)
        ], 110m, 90m);

        Assert.Contains(signals, signal => signal.PatternName == "Engulfing" && signal.Side == StrategySide.Long);
    }

    [Fact]
    public void PatternEngine_AppliesMomentumModifierToTurtle()
    {
        var engine = new PatternConfirmationEngine(new NySessionBreakoutOptions());
        var modifier = engine.ScoreModifierForStrategy(
            TurtleTrendStrategy.Name,
            StrategySide.Long,
            [new PatternSignal { PatternName = "Momentum Candle", Side = StrategySide.Long, Strength = 80m, Confidence = 0.8m }]);

        Assert.Equal(20m, modifier);
    }

    [Fact]
    public void Turtle_CreatesLongCandidateOnDonchianHighBreakout()
    {
        var strategy = new TurtleTrendStrategy(
            new TurtleTrendOptions
            {
                Enabled = true,
                EntryFastPeriod = 20,
                AtrPeriod = 20,
                MinAdx = 10m,
                RequireVolumeConfirmation = false,
                UseBtcFilter = false,
                UseFixedTakeProfit = false
            },
            new PatternConfirmationEngine(new NySessionBreakoutOptions()));
        var candles = TrendCandles(70, 100m, 1m, bullish: true);
        var context = new NyStrategyContext
        {
            Symbol = "BTCUSDT",
            FiveMinuteCandles = candles.TakeLast(40).ToArray(),
            FifteenMinuteCandles = candles.TakeLast(40).ToArray(),
            TurtleCandles = candles,
            Range = new NySessionRange { Upper = 120m, Lower = 90m },
            EntryNotionalUsdt = 100m
        };

        var candidate = strategy.BuildCandidate(context, new BreakoutClassifierResult { Classification = BreakoutClassification.TrueBreakout, BreakoutSide = StrategySide.Long });

        Assert.NotNull(candidate);
        Assert.Equal(StrategySide.Long, candidate!.Side);
        Assert.Null(candidate.TradeIntent!.TakeProfit);
        Assert.True(candidate.TradeIntent.StopLoss < candidate.TradeIntent.EntryPrice);
    }

    [Fact]
    public void Turtle_DoesNotCreateLongWhenAdxTooLow()
    {
        var strategy = new TurtleTrendStrategy(
            new TurtleTrendOptions { Enabled = true, MinAdx = 90m, RequireVolumeConfirmation = false, UseBtcFilter = false },
            new PatternConfirmationEngine(new NySessionBreakoutOptions()));
        var candles = TrendCandles(70, 100m, 1m, bullish: true);
        var context = new NyStrategyContext
        {
            Symbol = "BTCUSDT",
            FiveMinuteCandles = candles.TakeLast(40).ToArray(),
            FifteenMinuteCandles = candles.TakeLast(40).ToArray(),
            TurtleCandles = candles,
            Range = new NySessionRange { Upper = 120m, Lower = 90m },
            EntryNotionalUsdt = 100m
        };

        var candidate = strategy.BuildCandidate(context, new BreakoutClassifierResult { Classification = BreakoutClassification.TrueBreakout, BreakoutSide = StrategySide.Long });

        Assert.Null(candidate);
    }

    [Fact]
    public void BreakoutClassifier_ClassifiesFalseBreakoutWhenPriceReturnsInside()
    {
        var classifier = new BreakoutClassifier(
            new NySessionBreakoutOptions { MinReclaimPercent = 0.01m },
            new TurtleTrendOptions { MinAdx = 90m });
        var candles = new List<Candle>();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 35; i++)
        {
            candles.Add(new Candle(now.AddMinutes(i * 5), 100m, 101m, 99m, 100m, 100m, 0m));
        }

        candles.Add(new Candle(now.AddMinutes(175), 100m, 106m, 99m, 101m, 100m, 0m));
        candles.Add(new Candle(now.AddMinutes(180), 101m, 102m, 96m, 98m, 100m, 0m));
        var result = classifier.Classify(new NyStrategyContext
        {
            Symbol = "ETHUSDT",
            FiveMinuteCandles = candles,
            FifteenMinuteCandles = candles.Where((_, index) => index % 3 == 0).ToArray(),
            BtcFifteenMinuteCandles = [],
            Range = new NySessionRange { Upper = 104m, Lower = 97m }
        });

        Assert.Equal(BreakoutClassification.FalseBreakout, result.Classification);
        Assert.Equal(StrategySide.Short, result.BreakoutSide);
    }

    private static StrategyCandidate Candidate(string strategy, StrategySide side, decimal score, decimal confidence) => new()
    {
        StrategyName = strategy,
        Symbol = "BTCUSDT",
        Side = side,
        Score = score,
        Confidence = confidence,
        Reason = strategy,
        TradeIntent = new StrategyTradeIntent
        {
            StrategyName = strategy,
            Symbol = "BTCUSDT",
            Side = side,
            EntryType = StrategyEntryType.Market,
            EntryPrice = 100m,
            StopLoss = side == StrategySide.Long ? 98m : 102m,
            TakeProfit = side == StrategySide.Long ? 104m : 96m,
            ExpectedR = 2m
        }
    };

    private static IReadOnlyList<Candle> TrendCandles(int count, decimal start, decimal step, bool bullish)
    {
        var candles = new List<Candle>();
        var now = DateTimeOffset.UtcNow.AddHours(-count);
        for (var i = 0; i < count; i++)
        {
            var basePrice = bullish ? start + i * step : start - i * step;
            var open = basePrice;
            var close = bullish ? basePrice + step * 0.8m : basePrice - step * 0.8m;
            var high = decimal.Max(open, close) + step * 0.5m;
            var low = decimal.Min(open, close) - step * 0.5m;
            candles.Add(new Candle(now.AddHours(i), open, high, low, close, 100m + i, 0m));
        }

        return candles;
    }
}
