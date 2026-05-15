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
    public void Grid_AggressiveModeAllowsUnknownPhaseInsideRange()
    {
        var strategy = new GridStrategy();
        var options = Options();
        var phase = new MarketPhaseResult
        {
            Phase = MarketPhase.Unknown,
            Confidence = 0.35m,
            Score = 35m,
            Reason = "No reliable phase match.",
            SuggestedStrategy = StrategyType.Pause,
            DetectedAt = DateTimeOffset.UtcNow
        };

        Assert.False(strategy.CanCreateGridIntents(options, phase, 2.45m, bigRedGuardActive: false));
        Assert.True(strategy.CanCreateGridIntents(options, phase, 2.45m, bigRedGuardActive: false, aggressiveModeActive: true));
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
    public void Btd_AggressiveModeRelaxesTrendFiltersButKeepsRiskBlocks()
    {
        var strategy = new BtdStrategy();
        var options = Options();
        var candles = Enumerable.Range(0, 40)
            .Select(index => Candle(index, 2.40m - index * 0.001m))
            .ToArray();
        var unknownPhase = new MarketPhaseResult
        {
            Phase = MarketPhase.Unknown,
            Confidence = 0.35m,
            Score = 35m,
            Reason = "No reliable phase match.",
            SuggestedStrategy = StrategyType.Pause,
            DetectedAt = DateTimeOffset.UtcNow
        };
        var dumpPhase = new MarketPhaseResult
        {
            Phase = MarketPhase.Dump,
            Confidence = 0.9m,
            Score = 92m,
            Reason = "Dump detected.",
            SuggestedStrategy = StrategyType.Pause,
            DetectedAt = DateTimeOffset.UtcNow
        };

        Assert.False(strategy.IsDipAllowedByPhase(options, unknownPhase, 2.35m, candles, []));
        Assert.True(strategy.IsDipAllowedByPhase(options, unknownPhase, 2.35m, candles, [], aggressiveModeActive: true));
        Assert.False(strategy.IsDipAllowedByPhase(options, dumpPhase, 2.35m, candles, [], aggressiveModeActive: true));
        Assert.False(strategy.IsDipAllowedByPhase(
            options,
            unknownPhase,
            2.35m,
            candles,
            BtcRiskOffCandles(),
            aggressiveModeActive: true));
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
        StopUpperPrice = 2.65m,
        BtcLookbackCandles = 2
    };

    private static Candle Candle(int index, decimal close) =>
        new(DateTimeOffset.UtcNow.AddMinutes(index), close, close + 0.01m, close - 0.01m, close, 100m, 0m);

    private static IReadOnlyList<Candle> BtcRiskOffCandles() =>
    [
        new(DateTimeOffset.UtcNow.AddMinutes(-2), 100m, 100m, 100m, 100m, 100m, 0m),
        new(DateTimeOffset.UtcNow.AddMinutes(-1), 93m, 93m, 93m, 93m, 100m, 0m)
    ];
}
