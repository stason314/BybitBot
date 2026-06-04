using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BybitGridBot.App;

public interface IFuturesBacktestService
{
    FuturesBacktestStatusResponse GetStatus();

    Task<FuturesBacktestStatusResponse> StartAsync(FuturesBacktestRequest request, CancellationToken cancellationToken);

    FuturesBacktestStatusResponse Stop();

    bool IsSymbolAllowedForTrading(string symbol, bool requireCompletedBacktest);

    bool IsStrategySymbolDirectionAllowedForTrading(string strategyName, string symbol, string direction, bool requireCompletedBacktest);

    decimal ResolveStrategySymbolDirectionSizeMultiplier(string strategyName, string symbol, string direction, bool requireCompletedBacktest);
}

public sealed class FuturesBacktestService : IFuturesBacktestService
{
    private const string Category = "linear";
    private const string FiveMinuteInterval = "5";
    private const string FifteenMinuteInterval = "15";
    private const int ExpectedNySessionFiveMinuteCandles = 96;
    private const int KlinePageLimit = 1000;

    private readonly IBybitRestClient _bybitRestClient;
    private readonly FuturesBacktestOptions _backtestOptions;
    private readonly ILogger<FuturesBacktestService> _logger;
    private readonly ScoreBasedSignalEngine _scoreBasedSignalEngine;
    private readonly StrategyPerformanceTracker _strategyPerformanceTracker;
    private readonly NySessionBreakoutOptions _strategyOptions;
    private readonly StrategyRoutingOptions _strategyRoutingOptions;
    private readonly TurtleTrendOptions _turtleOptions;
    private readonly SemaphoreSlim _candleCacheLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private CancellationTokenSource? _runCancellation;
    private FuturesBacktestStatusResponse _status = new();

    public FuturesBacktestService(
        IBybitRestClient bybitRestClient,
        IOptions<FuturesBacktestOptions> backtestOptions,
        IOptions<NySessionBreakoutOptions> strategyOptions,
        IOptions<StrategyRoutingOptions> strategyRoutingOptions,
        IOptions<TurtleTrendOptions> turtleOptions,
        ScoreBasedSignalEngine scoreBasedSignalEngine,
        StrategyPerformanceTracker strategyPerformanceTracker,
        ILogger<FuturesBacktestService> logger)
    {
        _bybitRestClient = bybitRestClient;
        _backtestOptions = backtestOptions.Value;
        _strategyOptions = strategyOptions.Value;
        _strategyRoutingOptions = strategyRoutingOptions.Value;
        _turtleOptions = turtleOptions.Value;
        _scoreBasedSignalEngine = scoreBasedSignalEngine;
        _strategyPerformanceTracker = strategyPerformanceTracker;
        _logger = logger;
    }

    public FuturesBacktestStatusResponse GetStatus()
    {
        lock (_sync)
        {
            return _status;
        }
    }

    public Task<FuturesBacktestStatusResponse> StartAsync(FuturesBacktestRequest request, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_status.IsRunning)
            {
                return Task.FromResult(_status);
            }

            _runCancellation?.Dispose();
            _runCancellation = new CancellationTokenSource();
            _status = new FuturesBacktestStatusResponse
            {
                IsRunning = true,
                Status = "Starting 4H NY sweep/engulfing/pinbar/3-bar/breakout/shrinking backtest",
                StartedAt = DateTimeOffset.UtcNow,
                ProgressPercent = 0m
            };

            _ = Task.Run(() => RunBacktestAsync(request, _runCancellation.Token), CancellationToken.None);
            return Task.FromResult(_status);
        }
    }

    public FuturesBacktestStatusResponse Stop()
    {
        lock (_sync)
        {
            if (!_status.IsRunning)
            {
                return _status;
            }

            _runCancellation?.Cancel();
            _status = new FuturesBacktestStatusResponse
            {
                IsRunning = true,
                Status = "Stopping backtest",
                ProgressPercent = _status.ProgressPercent,
                StartedAt = _status.StartedAt,
                EstimatedCompletedAt = _status.EstimatedCompletedAt,
                Result = _status.Result
            };
            return _status;
        }
    }

    public bool IsSymbolAllowedForTrading(string symbol, bool requireCompletedBacktest)
    {
        lock (_sync)
        {
            var result = _status.Result;
            if (result is null)
            {
                return !RequiresEligibleStrategyGates(requireCompletedBacktest);
            }

            if (result.EligibleStrategySymbolDirections.Count > 0)
            {
                return result.EligibleStrategySymbolDirections
                    .Any(key => IsStrategyGateKeyForSymbol(key, symbol));
            }

            if (_strategyRoutingOptions.LiveUseEligibleStrategyGatesOnly)
            {
                return false;
            }

            if (_strategyRoutingOptions.EnableStrategySymbolGating)
            {
                return !requireCompletedBacktest;
            }

            return result.EligibleSymbols.Count == 0
                ? !requireCompletedBacktest
                : result.EligibleSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool IsStrategySymbolDirectionAllowedForTrading(
        string strategyName,
        string symbol,
        string direction,
        bool requireCompletedBacktest)
    {
        if (!_strategyRoutingOptions.EnableStrategySymbolGating &&
            !_strategyRoutingOptions.LiveUseEligibleStrategyGatesOnly)
        {
            return true;
        }

        lock (_sync)
        {
            var result = _status.Result;
            if (result is null)
            {
                return !RequiresEligibleStrategyGates(requireCompletedBacktest);
            }

            if (result.EligibleStrategySymbolDirections.Count > 0)
            {
                var key = BuildStrategyGateKey(strategyName, symbol, direction);
                return result.EligibleStrategySymbolDirections.Contains(key, StringComparer.OrdinalIgnoreCase);
            }

            return false;
        }
    }

    public decimal ResolveStrategySymbolDirectionSizeMultiplier(
        string strategyName,
        string symbol,
        string direction,
        bool requireCompletedBacktest)
    {
        var isAllowed = IsStrategySymbolDirectionAllowedForTrading(strategyName, symbol, direction, requireCompletedBacktest);
        return isAllowed
            ? decimal.Max(0m, _strategyRoutingOptions.LiveEligibleGateSizeMultiplier)
            : decimal.Max(0m, _strategyRoutingOptions.LiveIneligibleGateSizeMultiplier);
    }

    private bool RequiresEligibleStrategyGates(bool requireCompletedBacktest) =>
        requireCompletedBacktest || _strategyRoutingOptions.LiveUseEligibleStrategyGatesOnly;

    private async Task RunBacktestAsync(FuturesBacktestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var settings = ResolveSettings(request);
            var periodEnd = DateTimeOffset.UtcNow;
            var periodStart = periodEnd.AddDays(-settings.Days);
            SetStatus($"Loading top {settings.Symbols} Bybit USDT perpetual symbols ({settings.Mode})", 2m);

            var instruments = await _bybitRestClient.GetInstrumentsAsync(Category, cancellationToken);
            var tradable = instruments
                .Where(IsTradable)
                .ToDictionary(instrument => instrument.Symbol, StringComparer.OrdinalIgnoreCase);
            var tickers = await _bybitRestClient.GetTickersAsync(Category, cancellationToken);
            var symbols = tickers
                .Where(ticker => tradable.ContainsKey(ticker.Symbol))
                .OrderByDescending(ticker => ticker.Turnover24h)
                .Take(settings.Symbols)
                .Select(ticker => ticker.Symbol)
                .ToArray();

            var btc15m = settings.Mode == FuturesBacktestMode.TurtleOnly
                ? Array.Empty<Candle>()
                : await FetchHistoricalCandlesAsync("BTCUSDT", FifteenMinuteInterval, periodStart, periodEnd, cancellationToken);
            var btc15mSeries = BacktestCandleSeries.Create(btc15m, 15);
            var outputs = new ConcurrentBag<SymbolBacktestOutput>();
            var processed = 0;
            await Parallel.ForEachAsync(
                symbols,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = settings.MaxConcurrency,
                    CancellationToken = cancellationToken
                },
                async (symbol, token) =>
            {
                try
                {
                    outputs.Add(await BacktestSymbolAsync(symbol, periodStart, periodEnd, btc15mSeries, settings, token));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Backtest failed for {Symbol}", symbol);
                    outputs.Add(new SymbolBacktestOutput(symbol, [], 0, 0));
                }
                finally
                {
                    var done = Interlocked.Increment(ref processed);
                    SetStatus($"Processed {done}/{symbols.Length} symbols", 5m + 90m * done / Math.Max(1, symbols.Length));
                }
            });

            var allTrades = new List<BacktestTradeInternal>();
            var falseBreakoutCount = 0;
            var trueBreakoutBlockedCount = 0;
            foreach (var output in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                allTrades.AddRange(output.Trades);
                falseBreakoutCount += output.FalseBreakoutCount;
                trueBreakoutBlockedCount += output.TrueBreakoutBlockedCount;
            }

            var result = BuildResult(
                periodStart,
                periodEnd,
                symbols.Length,
                processed,
                allTrades.OrderBy(trade => trade.EntryTime).ToArray(),
                falseBreakoutCount,
                trueBreakoutBlockedCount,
                settings);

            lock (_sync)
            {
                _status = new FuturesBacktestStatusResponse
                {
                    IsRunning = false,
                    Status = "Completed",
                    ProgressPercent = 100m,
                    StartedAt = _status.StartedAt,
                    EstimatedCompletedAt = null,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Result = result
                };
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                _status = new FuturesBacktestStatusResponse
                {
                    IsRunning = false,
                    Status = "Cancelled",
                    ProgressPercent = _status.ProgressPercent,
                    StartedAt = _status.StartedAt,
                    EstimatedCompletedAt = null,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Result = _status.Result
                };
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Backtest failed.");
            lock (_sync)
            {
                _status = new FuturesBacktestStatusResponse
                {
                    IsRunning = false,
                    Status = $"Failed: {exception.Message}",
                    ProgressPercent = _status.ProgressPercent,
                    StartedAt = _status.StartedAt,
                    EstimatedCompletedAt = null,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Result = _status.Result
                };
            }
        }
    }

    private async Task<SymbolBacktestOutput> BacktestSymbolAsync(
        string symbol,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        BacktestCandleSeries btc15m,
        BacktestRunSettings settings,
        CancellationToken cancellationToken)
    {
        var fiveMinuteCandles = await FetchHistoricalCandlesAsync(symbol, FiveMinuteInterval, periodStart, periodEnd, cancellationToken);
        var fiveMinuteSeries = BacktestCandleSeries.Create(fiveMinuteCandles, 5);
        if (fiveMinuteSeries.Count < 500)
        {
            return new SymbolBacktestOutput(symbol, [], 0, 0);
        }

        var shouldLoadTurtleCandles = settings.Mode == FuturesBacktestMode.TurtleOnly ||
            _strategyRoutingOptions.SignalSelectionMode == SignalSelectionMode.ScoreBased;
        var turtleCandles = shouldLoadTurtleCandles
            ? await FetchHistoricalCandlesAsync(symbol, _turtleOptions.Timeframe, periodStart.AddDays(-10), periodEnd, cancellationToken)
            : Array.Empty<Candle>();
        var turtleSeries = BacktestCandleSeries.Create(turtleCandles, ResolveIntervalMinutes(_turtleOptions.Timeframe));
        var turtleIndicators = PrecomputedTurtleIndicators.Build(turtleSeries.Candles, _turtleOptions, cancellationToken);
        var fiveMinuteTurtleExits = PrecomputedTurtleChannelExits.Build(fiveMinuteSeries.Candles, _turtleOptions, cancellationToken);

        if (settings.Mode == FuturesBacktestMode.TurtleOnly)
        {
            return BacktestTurtleOnlySymbol(symbol, periodStart, fiveMinuteSeries, turtleSeries, turtleIndicators, fiveMinuteTurtleExits, settings, cancellationToken);
        }

        var fifteenMinuteCandles = await FetchHistoricalCandlesAsync(symbol, FifteenMinuteInterval, periodStart, periodEnd, cancellationToken);
        var fifteenMinuteSeries = BacktestCandleSeries.Create(fifteenMinuteCandles, 15);
        if (fifteenMinuteSeries.Count < 200)
        {
            return new SymbolBacktestOutput(symbol, [], 0, 0);
        }

        var nyZone = ResolveNewYorkTimeZone();
        var trades = new List<BacktestTradeInternal>();
        var falseBreakoutCount = 0;
        var trueBreakoutBlockedCount = 0;
        var groupedByDay = fiveMinuteSeries.Candles
            .GroupBy(candle => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(candle.OpenTime, nyZone).Date))
            .OrderBy(group => group.Key);

        foreach (var day in groupedByDay)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsNySessionComplete(day.Key, nyZone, periodEnd))
            {
                continue;
            }

            var session = day
                .Where(candle =>
                {
                    var ny = TimeZoneInfo.ConvertTime(candle.OpenTime, nyZone);
                    return ny.TimeOfDay >= TimeSpan.FromHours(8) && ny.TimeOfDay < TimeSpan.FromHours(16);
                })
                .OrderBy(candle => candle.OpenTime)
                .ToArray();
            if (session.Length < ExpectedNySessionFiveMinuteCandles)
            {
                continue;
            }

            BacktestDay(
                symbol,
                session,
                fiveMinuteSeries,
                fifteenMinuteSeries,
                turtleSeries,
                turtleIndicators,
                btc15m,
                fiveMinuteTurtleExits,
                settings,
                nyZone,
                trades,
                ref falseBreakoutCount,
                ref trueBreakoutBlockedCount,
                cancellationToken);
        }

        return new SymbolBacktestOutput(symbol, trades, falseBreakoutCount, trueBreakoutBlockedCount);
    }

    private SymbolBacktestOutput BacktestTurtleOnlySymbol(
        string symbol,
        DateTimeOffset periodStart,
        BacktestCandleSeries fiveMinuteCandles,
        BacktestCandleSeries turtleCandles,
        PrecomputedTurtleIndicators indicators,
        PrecomputedTurtleChannelExits turtleExits,
        BacktestRunSettings settings,
        CancellationToken cancellationToken)
    {
        var minCandles = Math.Max(_turtleOptions.EntrySlowPeriod, _turtleOptions.AtrPeriod) + 1;
        if (turtleCandles.Count < minCandles || fiveMinuteCandles.Count < 2)
        {
            return new SymbolBacktestOutput(symbol, [], 0, 0);
        }

        var trades = new List<BacktestTradeInternal>();
        var equity = settings.InitialEquityUsdt;
        bool? previousS1WasProfitable = null;
        for (var index = minCandles; index < turtleCandles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = turtleCandles.Candles[index];
            if (current.OpenTime < periodStart)
            {
                continue;
            }

            if (HasOpenBacktestTrade(trades, current.OpenTime))
            {
                continue;
            }

            var signal = TryBuildTurtleOnlySignal(symbol, current, indicators, index);
            if (signal is null)
            {
                continue;
            }

            if (signal.TurtleSystem == "S1" && previousS1WasProfitable == true)
            {
                continue;
            }

            var trade = SimulateTrade(
                symbol,
                signal,
                fiveMinuteCandles.Candles,
                fiveMinuteCandles.Candles,
                turtleExits,
                0,
                settings,
                equity,
                cancellationToken);
            trades.Add(trade);
            equity += trade.NetPnl;
            if (signal.TurtleSystem == "S1")
            {
                previousS1WasProfitable = trade.NetPnl > 0m;
            }
        }

        return new SymbolBacktestOutput(symbol, trades, 0, 0);
    }

    private NySessionSignal? TryBuildTurtleOnlySignal(
        string symbol,
        Candle current,
        PrecomputedTurtleIndicators indicators,
        int index)
    {
        var candidate = ResolvePrecomputedTurtleSignal(current, indicators, index);
        if (candidate.Side == StrategySide.None)
        {
            return null;
        }

        var nValue = indicators.TurtleN[index];
        if (nValue <= 0m)
        {
            return null;
        }

        var stopLoss = candidate.Side == StrategySide.Long
            ? current.Close - nValue * _turtleOptions.StopAtrMultiplier
            : current.Close + nValue * _turtleOptions.StopAtrMultiplier;
        var risk = Math.Abs(current.Close - stopLoss);
        if (risk <= 0m)
        {
            return null;
        }

        return new NySessionSignal
        {
            Side = candidate.Side.ToString(),
            EntryPrice = current.Close,
            StopLoss = stopLoss,
            TakeProfit = 0m,
            Boundary = candidate.BreakoutLevel,
            StopDistancePercent = current.Close > 0m ? risk / current.Close * 100m : 0m,
            Pattern = TurtleTrendStrategy.Name,
            BreakoutCandleOpenTime = current.OpenTime,
            SignalCandleOpenTime = current.OpenTime,
            BreakoutVolumeRatio = 1m,
            TurtleSystem = candidate.System,
            TurtleSignalId = $"{symbol}:{candidate.System}:{candidate.Side}:{current.OpenTime.UtcDateTime:yyyyMMddHHmm}:{candidate.BreakoutLevel:0.########}",
            TurtleN = nValue,
            TurtleBreakoutLevel = candidate.BreakoutLevel,
            Reason = $"Turtle-only {candidate.System} {candidate.Side} breakout. Close={current.Close:F8}, breakoutLevel={candidate.BreakoutLevel:F8}, N={nValue:F8}."
        };
    }

    private static PrecomputedTurtleSignal ResolvePrecomputedTurtleSignal(
        Candle current,
        PrecomputedTurtleIndicators indicators,
        int index)
    {
        var slowHigh = indicators.EntrySlowHigh[index];
        var slowLow = indicators.EntrySlowLow[index];
        if (slowHigh > 0m && current.Close > slowHigh)
        {
            return new PrecomputedTurtleSignal("S2", StrategySide.Long, slowHigh);
        }

        if (slowLow > 0m && current.Close < slowLow)
        {
            return new PrecomputedTurtleSignal("S2", StrategySide.Short, slowLow);
        }

        var fastHigh = indicators.EntryFastHigh[index];
        var fastLow = indicators.EntryFastLow[index];
        if (fastHigh > 0m && current.Close > fastHigh)
        {
            return new PrecomputedTurtleSignal("S1", StrategySide.Long, fastHigh);
        }

        if (fastLow > 0m && current.Close < fastLow)
        {
            return new PrecomputedTurtleSignal("S1", StrategySide.Short, fastLow);
        }

        return new PrecomputedTurtleSignal(string.Empty, StrategySide.None, 0m);
    }

    private static bool IsNySessionComplete(DateOnly nyDate, TimeZoneInfo nyZone, DateTimeOffset periodEnd)
    {
        var sessionEndLocal = nyDate.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)));
        var sessionEndUtc = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(sessionEndLocal, DateTimeKind.Unspecified), nyZone));
        return periodEnd >= sessionEndUtc;
    }

    private void BacktestDay(
        string symbol,
        IReadOnlyList<Candle> session,
        BacktestCandleSeries allFiveMinuteCandles,
        BacktestCandleSeries fifteenMinuteCandles,
        BacktestCandleSeries turtleCandles,
        PrecomputedTurtleIndicators turtleIndicators,
        BacktestCandleSeries btc15m,
        PrecomputedTurtleChannelExits turtleExits,
        BacktestRunSettings settings,
        TimeZoneInfo nyZone,
        List<BacktestTradeInternal> trades,
        ref int falseBreakoutCount,
        ref int trueBreakoutBlockedCount,
        CancellationToken cancellationToken)
    {
        var upperBoundary = session[0].High;
        var lowerBoundary = session[0].Low;
        decimal? upperStop = null;
        DateTimeOffset? upperSweepAt = null;
        decimal? upperReturnLevel = null;
        decimal? lowerStop = null;
        DateTimeOffset? lowerSweepAt = null;
        decimal? lowerReturnLevel = null;
        var processedScoreSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedBreakoutClassifications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool? previousS1WasProfitable = null;
        var realizedPnl = trades.Sum(trade => trade.NetPnl);

        for (var i = 1; i < session.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candle = session[i];
            var ny = TimeZoneInfo.ConvertTime(candle.OpenTime, nyZone);
            var isRangeBuilding = ny.TimeOfDay < TimeSpan.FromHours(12);
            if (_strategyRoutingOptions.SignalSelectionMode == SignalSelectionMode.ScoreBased)
            {
                var decision = BuildScoreBasedBacktestDecision(symbol, session, allFiveMinuteCandles, i, fifteenMinuteCandles, turtleCandles, turtleIndicators, btc15m, settings);
                TrackScoreBasedBreakoutCounters(decision, session[i], processedBreakoutClassifications, ref falseBreakoutCount, ref trueBreakoutBlockedCount);

                if (decision.IsTradeAllowed &&
                    decision.SelectedCandidate is not null &&
                    i + 1 < session.Count &&
                    !HasOpenBacktestTrade(trades, session[i].OpenTime))
                {
                    if (!IsBacktestLiveEntryAllowed(decision.SelectedCandidate, session[i].OpenTime, nyZone))
                    {
                        continue;
                    }

                    var signalKey = BuildScoreSignalKey(decision.SelectedCandidate);
                    if (!processedScoreSignals.Add(signalKey))
                    {
                        continue;
                    }

                    var scoreSignal = ToBacktestSignal(decision.SelectedCandidate, session, i);
                    if (IsTurtleBacktestSignal(scoreSignal) &&
                        scoreSignal.TurtleSystem == "S1" &&
                        previousS1WasProfitable == true)
                    {
                        continue;
                    }

                    var accountEquity = settings.InitialEquityUsdt + realizedPnl;
                    var trade = SimulateTrade(symbol, scoreSignal, session, allFiveMinuteCandles.Candles, turtleExits, i + 1, settings, accountEquity, cancellationToken);
                    trades.Add(trade);
                    realizedPnl += trade.NetPnl;
                    if (IsTurtleBacktestSignal(scoreSignal) && scoreSignal.TurtleSystem == "S1")
                    {
                        previousS1WasProfitable = trade.NetPnl > 0m;
                    }

                    i = trade.ExitIndex;
                    continue;
                }

                if (isRangeBuilding)
                {
                    upperBoundary = decimal.Max(upperBoundary, candle.High);
                    lowerBoundary = decimal.Min(lowerBoundary, candle.Low);
                }

                continue;
            }

            var signal = TryBuildSignal(
                session,
                i,
                candle,
                upperBoundary,
                lowerBoundary,
                upperStop,
                upperSweepAt,
                upperReturnLevel,
                lowerStop,
                lowerSweepAt,
                lowerReturnLevel);

            if (signal is not null)
            {
                if (!string.Equals(signal.Pattern, "Engulfing", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(signal.Pattern, "Pinbar", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(signal.Pattern, "3-Bar Continuation", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(signal.Pattern, "3-Bar Reversal", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(signal.Pattern, "Breakout Candle", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(signal.Pattern, "Shrinking Candles", StringComparison.OrdinalIgnoreCase))
                {
                    falseBreakoutCount++;
                }

                var filter = EvaluateBacktestFilters(signal, CopyPrefix(session, i + 1), fifteenMinuteCandles.Candles, btc15m.Candles);
                if (filter.IsTrueBreakoutBlocked)
                {
                    trueBreakoutBlockedCount++;
                }

                if (filter.IsAllowed)
                {
                    var accountEquity = settings.InitialEquityUsdt + realizedPnl;
                    var trade = SimulateTrade(symbol, signal, session, allFiveMinuteCandles.Candles, turtleExits, i + 1, settings, accountEquity, cancellationToken);
                    trades.Add(trade);
                    realizedPnl += trade.NetPnl;
                    i = trade.ExitIndex;
                    upperStop = null;
                    upperSweepAt = null;
                    upperReturnLevel = null;
                    lowerStop = null;
                    lowerSweepAt = null;
                    lowerReturnLevel = null;
                    continue;
                }
            }

            if (candle.High > upperBoundary && candle.Close > upperBoundary)
            {
                upperStop = upperStop is null ? candle.High : decimal.Max(upperStop.Value, candle.High);
                upperSweepAt = candle.OpenTime;
                upperReturnLevel = upperBoundary;
            }

            if (candle.Low < lowerBoundary && candle.Close < lowerBoundary)
            {
                lowerStop = lowerStop is null ? candle.Low : decimal.Min(lowerStop.Value, candle.Low);
                lowerSweepAt = candle.OpenTime;
                lowerReturnLevel = lowerBoundary;
            }

            if (isRangeBuilding)
            {
                upperBoundary = decimal.Max(upperBoundary, candle.High);
                lowerBoundary = decimal.Min(lowerBoundary, candle.Low);
            }
        }
    }

    private StrategyDecision BuildScoreBasedBacktestDecision(
        string symbol,
        IReadOnlyList<Candle> session,
        BacktestCandleSeries allFiveMinuteCandles,
        int index,
        BacktestCandleSeries fifteenMinuteCandles,
        BacktestCandleSeries turtleCandles,
        PrecomputedTurtleIndicators turtleIndicators,
        BacktestCandleSeries btc15m,
        BacktestRunSettings settings)
    {
        var current = session[index];
        var currentCloseTime = current.OpenTime.AddMinutes(5);
        var fiveMinuteCandles = CopyPrefix(session, index + 1);
        var range = BuildBacktestRange(session, allFiveMinuteCandles, index);
        var turtleInterval = ParseIntervalMinutes(_turtleOptions.Timeframe, 60);
        var turtleClosedCount = turtleCandles.CountClosedUntil(currentCloseTime, turtleInterval);
        var context = new NyStrategyContext
        {
            Symbol = symbol,
            FiveMinuteCandles = fiveMinuteCandles,
            FifteenMinuteCandles = fifteenMinuteCandles.CopyClosedUntil(currentCloseTime),
            TurtleCandles = turtleCandles.CopyFirst(turtleClosedCount),
            TurtleIndicators = BuildTurtleIndicatorSnapshot(turtleCandles, turtleIndicators, turtleClosedCount),
            BtcFifteenMinuteCandles = btc15m.CopyClosedUntil(currentCloseTime),
            Range = range,
            Now = currentCloseTime,
            EntryNotionalUsdt = settings.EntryNotionalUsdt,
            RewardRisk = _strategyOptions.RewardRisk
        };

        return _scoreBasedSignalEngine.Decide(context);
    }

    private static void TrackScoreBasedBreakoutCounters(
        StrategyDecision decision,
        Candle candle,
        ISet<string> processedBreakoutClassifications,
        ref int falseBreakoutCount,
        ref int trueBreakoutBlockedCount)
    {
        if (decision.BreakoutClassification == BreakoutClassification.Unclear ||
            decision.BreakoutSide == StrategySide.None)
        {
            return;
        }

        var key = $"{decision.BreakoutClassification}|{decision.BreakoutSide}|{candle.OpenTime:O}";
        if (!processedBreakoutClassifications.Add(key))
        {
            return;
        }

        if (decision.BreakoutClassification == BreakoutClassification.FalseBreakout)
        {
            falseBreakoutCount++;
            return;
        }

        var sweepBlocked = decision.AllCandidates.Any(candidate =>
            string.Equals(candidate.StrategyName, NYSweepReversalStrategy.Name, StringComparison.OrdinalIgnoreCase) &&
            candidate.RejectionReason == StrategyNoTradeReason.TrueBreakoutProtection);
        if (decision.BreakoutClassification == BreakoutClassification.TrueBreakout && sweepBlocked)
        {
            trueBreakoutBlockedCount++;
        }
    }

    private NySessionRange BuildBacktestRange(
        IReadOnlyList<Candle> session,
        BacktestCandleSeries allFiveMinuteCandles,
        int index)
    {
        var sessionStart = session[0].OpenTime;
        var rangeStart = sessionStart;
        var rangeEnd = sessionStart.AddHours(4);
        var currentCandles = CopyPrefix(session, index + 1);
        var rangeCandles = _strategyRoutingOptions.NyRangeMode == NyRangeMode.PreSessionReferenceRange
            ? allFiveMinuteCandles.CopyWindow(rangeStart.AddHours(-4), rangeStart, currentCandles[0])
            : CopyWindow(currentCandles, currentCandles[0].OpenTime, rangeEnd, currentCandles[0]);

        return new NySessionRange
        {
            Upper = MaxHigh(rangeCandles),
            Lower = MinLow(rangeCandles),
            Mode = _strategyRoutingOptions.NyRangeMode,
            RangeStartUtc = _strategyRoutingOptions.NyRangeMode == NyRangeMode.PreSessionReferenceRange ? rangeStart.AddHours(-4) : rangeStart,
            RangeEndUtc = _strategyRoutingOptions.NyRangeMode == NyRangeMode.PreSessionReferenceRange ? rangeStart : rangeEnd
        };
    }

    private static TurtleIndicatorSnapshot? BuildTurtleIndicatorSnapshot(
        BacktestCandleSeries turtleCandles,
        PrecomputedTurtleIndicators indicators,
        int closedCount)
    {
        if (closedCount <= 0)
        {
            return null;
        }

        var index = closedCount - 1;
        return new TurtleIndicatorSnapshot
        {
            Current = turtleCandles.Candles[index],
            EntryFastHigh = indicators.EntryFastHigh[index],
            EntryFastLow = indicators.EntryFastLow[index],
            EntrySlowHigh = indicators.EntrySlowHigh[index],
            EntrySlowLow = indicators.EntrySlowLow[index],
            ExitFastHigh = indicators.ExitFastHigh[index],
            ExitFastLow = indicators.ExitFastLow[index],
            ExitSlowHigh = indicators.ExitSlowHigh[index],
            ExitSlowLow = indicators.ExitSlowLow[index],
            TurtleN = indicators.TurtleN[index]
        };
    }

    private static NySessionSignal ToBacktestSignal(StrategyCandidate candidate, IReadOnlyList<Candle> session, int index)
    {
        var intent = candidate.TradeIntent ?? throw new InvalidOperationException("Strategy candidate has no trade intent.");
        var current = session[index];
        var takeProfit = intent.TakeProfit ?? 0m;
        var boundary = candidate.Side == StrategySide.Short
            ? MaxHigh(session, index)
            : MinLow(session, index);
        var stopDistancePercent = intent.EntryPrice > 0m
            ? Math.Abs(intent.EntryPrice - intent.StopLoss) / intent.EntryPrice * 100m
            : 0m;

        return new NySessionSignal
        {
            Side = intent.Side.ToString(),
            EntryPrice = intent.EntryPrice,
            StopLoss = intent.StopLoss,
            TakeProfit = takeProfit,
            Boundary = boundary,
            StopDistancePercent = stopDistancePercent,
            Pattern = candidate.StrategyName,
            BreakoutCandleOpenTime = current.OpenTime,
            SignalCandleOpenTime = current.OpenTime,
            BreakoutVolumeRatio = 1m,
            TurtleSystem = intent.TurtleSystem,
            TurtleSignalId = string.Equals(candidate.StrategyName, TurtleTrendStrategy.Name, StringComparison.OrdinalIgnoreCase)
                ? $"{candidate.Symbol}:{intent.TurtleSystem}:{candidate.Side}:{candidate.CreatedAt.UtcDateTime:yyyyMMddHHmm}:{intent.TurtleBreakoutLevel:0.########}"
                : string.Empty,
            TurtleN = intent.TurtleN,
            TurtleBreakoutLevel = intent.TurtleBreakoutLevel,
            Reason = $"{candidate.StrategyName}: score={candidate.Score:F0}, confidence={candidate.Confidence:F2}, {candidate.Reason}"
        };
    }

    private static string BuildScoreSignalKey(StrategyCandidate candidate)
    {
        var intent = candidate.TradeIntent;
        return string.Join(
            '|',
            candidate.StrategyName,
            candidate.Symbol,
            candidate.Side,
            candidate.CreatedAt.ToUnixTimeSeconds(),
            intent?.EntryType.ToString() ?? string.Empty,
            FormatSignalPrice(intent?.EntryPrice),
            FormatSignalPrice(intent?.StopLoss),
            FormatSignalPrice(intent?.TakeProfit));
    }

    private static bool IsTurtleBacktestSignal(NySessionSignal signal) =>
        string.Equals(signal.Pattern, TurtleTrendStrategy.Name, StringComparison.OrdinalIgnoreCase);

    private static string FormatSignalPrice(decimal? value) =>
        value.HasValue ? value.Value.ToString("0.########") : string.Empty;

    private static bool HasOpenBacktestTrade(IReadOnlyList<BacktestTradeInternal> trades, DateTimeOffset currentTime) =>
        trades.Any(trade => trade.EntryTime <= currentTime && trade.ExitTime > currentTime);

    private static int ParseIntervalMinutes(string interval, int fallback)
    {
        return int.TryParse(interval, out var minutes) && minutes > 0 ? minutes : fallback;
    }

    private NySessionSignal? TryBuildSignal(
        IReadOnlyList<Candle> session,
        int index,
        Candle candle,
        decimal upperBoundary,
        decimal lowerBoundary,
        decimal? upperStop,
        DateTimeOffset? upperSweepAt,
        decimal? upperReturnLevel,
        decimal? lowerStop,
        DateTimeOffset? lowerSweepAt,
        decimal? lowerReturnLevel)
    {
        if (candle.High > upperBoundary && candle.Close < upperBoundary)
        {
            var risk = candle.High - candle.Close;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Short",
                    candle.OpenTime,
                    candle.OpenTime,
                    upperBoundary,
                    upperBoundary,
                    lowerBoundary,
                    candle.Close,
                    candle.High,
                    "Upper sweep reclaimed.",
                    CopyPrefix(session, index + 1));
            }
        }

        if (upperStop is not null &&
            upperSweepAt is not null &&
            upperReturnLevel is not null &&
            candle.OpenTime > upperSweepAt &&
            candle.Close < upperReturnLevel.Value)
        {
            var risk = upperStop.Value - candle.Close;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Short",
                    candle.OpenTime,
                    upperSweepAt.Value,
                    upperReturnLevel.Value,
                    upperBoundary,
                    lowerBoundary,
                    candle.Close,
                    upperStop.Value,
                    "Upper breakout failed.",
                    CopyPrefix(session, index + 1));
            }
        }

        if (lowerStop is not null &&
            lowerSweepAt is not null &&
            lowerReturnLevel is not null &&
            candle.OpenTime > lowerSweepAt &&
            candle.Close > lowerReturnLevel.Value)
        {
            var risk = candle.Close - lowerStop.Value;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Long",
                    candle.OpenTime,
                    lowerSweepAt.Value,
                    lowerReturnLevel.Value,
                    upperBoundary,
                    lowerBoundary,
                    candle.Close,
                    lowerStop.Value,
                    "Lower breakout failed.",
                    CopyPrefix(session, index + 1));
            }
        }

        var closed = CopyPrefix(session, index + 1);
        return TryFindEngulfingSignal(closed, upperBoundary, lowerBoundary) ??
            TryFindPinbarSignal(closed, upperBoundary, lowerBoundary) ??
            TryFindThreeBarContinuationSignal(closed, upperBoundary, lowerBoundary) ??
            TryFindThreeBarReversalSignal(closed, upperBoundary, lowerBoundary) ??
            TryFindBreakoutCandleSignal(closed, upperBoundary, lowerBoundary) ??
            TryFindShrinkingCandlesSignal(closed, upperBoundary, lowerBoundary);
    }

    private NySessionSignal BuildSignal(
        string side,
        DateTimeOffset signalCandleOpenTime,
        DateTimeOffset breakoutCandleOpenTime,
        decimal boundary,
        decimal rangeHigh,
        decimal rangeLow,
        decimal entryPrice,
        decimal sweepExtreme,
        string reason,
        IReadOnlyList<Candle> candles,
        string pattern = "Sweep Reversal",
        decimal bodyRatio = 0m,
        decimal wickBodyRatio = 0m,
        decimal wickRangePercent = 0m)
    {
        var risk = Math.Abs(sweepExtreme - entryPrice);
        var takeProfit = side == "Short"
            ? entryPrice - risk * _strategyOptions.RewardRisk
            : entryPrice + risk * _strategyOptions.RewardRisk;
        return new NySessionSignal
        {
            Pattern = pattern,
            Side = side,
            SignalCandleOpenTime = signalCandleOpenTime,
            BreakoutCandleOpenTime = breakoutCandleOpenTime,
            Boundary = boundary,
            RangeHigh = rangeHigh,
            RangeLow = rangeLow,
            EntryPrice = entryPrice,
            SweepExtreme = sweepExtreme,
            StopLoss = sweepExtreme,
            TakeProfit = takeProfit,
            ReclaimPercent = CalculateReclaimPercent(side, boundary, entryPrice),
            SweepDepthPercent = CalculateSweepDepthPercent(side, boundary, sweepExtreme),
            StopDistancePercent = entryPrice > 0m ? risk / entryPrice * 100m : 0m,
            MidlineRoomR = CalculateMidlineRoomR(side, rangeHigh, rangeLow, entryPrice, risk),
            BreakoutVolumeRatio = CalculateBreakoutVolumeRatio(candles, breakoutCandleOpenTime),
            BodyRatio = bodyRatio,
            WickBodyRatio = wickBodyRatio,
            WickRangePercent = wickRangePercent,
            Reason = reason
        };
    }

    private NySessionSignal? TryFindEngulfingSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        if (!_strategyOptions.EngulfingEnabled || closed.Count < 2 || upperBoundary <= lowerBoundary)
        {
            return null;
        }

        var previous = closed[^2];
        var current = closed[^1];
        if (!IsInsideRange(previous.Close, upperBoundary, lowerBoundary) ||
            !IsInsideRange(current.Close, upperBoundary, lowerBoundary))
        {
            return null;
        }

        var previousBody = Math.Abs(previous.Close - previous.Open);
        var currentBody = Math.Abs(current.Close - current.Open);
        if (previousBody <= 0m || currentBody < previousBody * _strategyOptions.MinEngulfingBodyRatio)
        {
            return null;
        }

        var bullish = previous.Close < previous.Open &&
            current.Close > current.Open &&
            current.Open <= previous.Close &&
            current.Close >= previous.Open;
        if (bullish)
        {
            var risk = current.Close - current.Low;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Long",
                    current.OpenTime,
                    current.OpenTime,
                    lowerBoundary,
                    upperBoundary,
                    lowerBoundary,
                    current.Close,
                    current.Low,
                    "Bullish engulfing inside the active 4H range.",
                    closed,
                    "Engulfing",
                    currentBody / previousBody);
            }
        }

        var bearish = previous.Close > previous.Open &&
            current.Close < current.Open &&
            current.Open >= previous.Close &&
            current.Close <= previous.Open;
        if (bearish)
        {
            var risk = current.High - current.Close;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Short",
                    current.OpenTime,
                    current.OpenTime,
                    upperBoundary,
                    upperBoundary,
                    lowerBoundary,
                    current.Close,
                    current.High,
                    "Bearish engulfing inside the active 4H range.",
                    closed,
                    "Engulfing",
                    currentBody / previousBody);
            }
        }

        return null;
    }

    private NySessionSignal? TryFindPinbarSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        if (!_strategyOptions.PinbarEnabled || closed.Count < 1 || upperBoundary <= lowerBoundary)
        {
            return null;
        }

        var current = closed[^1];
        if (!IsInsideRange(current.Open, upperBoundary, lowerBoundary) ||
            !IsInsideRange(current.Close, upperBoundary, lowerBoundary) ||
            !IsInsideRange(current.High, upperBoundary, lowerBoundary) ||
            !IsInsideRange(current.Low, upperBoundary, lowerBoundary))
        {
            return null;
        }

        var range = current.High - current.Low;
        var body = Math.Abs(current.Close - current.Open);
        if (range <= 0m || body <= 0m || body / range * 100m > _strategyOptions.MaxPinbarBodyRangePercent)
        {
            return null;
        }

        var upperWick = current.High - decimal.Max(current.Open, current.Close);
        var lowerWick = decimal.Min(current.Open, current.Close) - current.Low;
        var bullish = lowerWick / body >= _strategyOptions.MinPinbarWickBodyRatio &&
            lowerWick / range * 100m >= _strategyOptions.MinPinbarWickRangePercent &&
            upperWick < lowerWick;
        if (bullish)
        {
            var risk = current.Close - current.Low;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Long",
                    current.OpenTime,
                    current.OpenTime,
                    lowerBoundary,
                    upperBoundary,
                    lowerBoundary,
                    current.Close,
                    current.Low,
                    "Bullish pinbar inside the active 4H range.",
                    closed,
                    "Pinbar",
                    wickBodyRatio: lowerWick / body,
                    wickRangePercent: lowerWick / range * 100m);
            }
        }

        var bearish = upperWick / body >= _strategyOptions.MinPinbarWickBodyRatio &&
            upperWick / range * 100m >= _strategyOptions.MinPinbarWickRangePercent &&
            lowerWick < upperWick;
        if (bearish)
        {
            var risk = current.High - current.Close;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Short",
                    current.OpenTime,
                    current.OpenTime,
                    upperBoundary,
                    upperBoundary,
                    lowerBoundary,
                    current.Close,
                    current.High,
                    "Bearish pinbar inside the active 4H range.",
                    closed,
                    "Pinbar",
                    wickBodyRatio: upperWick / body,
                    wickRangePercent: upperWick / range * 100m);
            }
        }

        return null;
    }

    private NySessionSignal? TryFindThreeBarContinuationSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        if (!_strategyOptions.ThreeBarContinuationEnabled || closed.Count < 3 || upperBoundary <= lowerBoundary)
        {
            return null;
        }

        var first = closed[^3];
        var second = closed[^2];
        var third = closed[^1];
        if (!IsInsideRange(first.Close, upperBoundary, lowerBoundary) ||
            !IsInsideRange(second.Close, upperBoundary, lowerBoundary) ||
            !IsInsideRange(third.Close, upperBoundary, lowerBoundary))
        {
            return null;
        }

        var firstBody = Math.Abs(first.Close - first.Open);
        var secondBody = Math.Abs(second.Close - second.Open);
        var thirdBody = Math.Abs(third.Close - third.Open);
        if (firstBody <= 0m || secondBody <= 0m || thirdBody <= 0m)
        {
            return null;
        }

        var outerBodyRatio = decimal.Min(firstBody, thirdBody) / secondBody;
        if (outerBodyRatio < _strategyOptions.MinThreeBarOuterBodyRatio)
        {
            return null;
        }

        var bullish = first.Close > first.Open &&
            second.Close < second.Open &&
            third.Close > third.Open &&
            third.Close > first.Close;
        if (bullish)
        {
            var stop = decimal.Min(first.Low, decimal.Min(second.Low, third.Low));
            var risk = third.Close - stop;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Long",
                    third.OpenTime,
                    third.OpenTime,
                    lowerBoundary,
                    upperBoundary,
                    lowerBoundary,
                    third.Close,
                    stop,
                    "Bullish 3-bar continuation inside the active 4H range.",
                    closed,
                    "3-Bar Continuation",
                    outerBodyRatio);
            }
        }

        var bearish = first.Close < first.Open &&
            second.Close > second.Open &&
            third.Close < third.Open &&
            third.Close < first.Close;
        if (bearish)
        {
            var stop = decimal.Max(first.High, decimal.Max(second.High, third.High));
            var risk = stop - third.Close;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Short",
                    third.OpenTime,
                    third.OpenTime,
                    upperBoundary,
                    upperBoundary,
                    lowerBoundary,
                    third.Close,
                    stop,
                    "Bearish 3-bar continuation inside the active 4H range.",
                    closed,
                    "3-Bar Continuation",
                    outerBodyRatio);
            }
        }

        return null;
    }

    private NySessionSignal? TryFindThreeBarReversalSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        if (!_strategyOptions.ThreeBarReversalEnabled || closed.Count < 3 || upperBoundary <= lowerBoundary)
        {
            return null;
        }

        var first = closed[^3];
        var second = closed[^2];
        var third = closed[^1];
        if (!IsInsideRange(first.Close, upperBoundary, lowerBoundary) ||
            !IsInsideRange(second.Close, upperBoundary, lowerBoundary) ||
            !IsInsideRange(third.Close, upperBoundary, lowerBoundary))
        {
            return null;
        }

        var firstBody = Math.Abs(first.Close - first.Open);
        var secondBody = Math.Abs(second.Close - second.Open);
        var thirdBody = Math.Abs(third.Close - third.Open);
        if (firstBody <= 0m || secondBody <= 0m || thirdBody <= 0m)
        {
            return null;
        }

        var outerBodyRatio = decimal.Min(firstBody, thirdBody) / secondBody;
        if (outerBodyRatio < _strategyOptions.MinThreeBarOuterBodyRatio)
        {
            return null;
        }

        var bullish = first.Close < first.Open &&
            second.Close < second.Open &&
            third.Close > third.Open &&
            third.Close > first.Open;
        if (bullish)
        {
            var stop = decimal.Min(first.Low, decimal.Min(second.Low, third.Low));
            var risk = third.Close - stop;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Long",
                    third.OpenTime,
                    third.OpenTime,
                    lowerBoundary,
                    upperBoundary,
                    lowerBoundary,
                    third.Close,
                    stop,
                    "Bullish 3-bar reversal inside the active 4H range.",
                    closed,
                    "3-Bar Reversal",
                    outerBodyRatio);
            }
        }

        var bearish = first.Close > first.Open &&
            second.Close > second.Open &&
            third.Close < third.Open &&
            third.Close < first.Open;
        if (bearish)
        {
            var stop = decimal.Max(first.High, decimal.Max(second.High, third.High));
            var risk = stop - third.Close;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Short",
                    third.OpenTime,
                    third.OpenTime,
                    upperBoundary,
                    upperBoundary,
                    lowerBoundary,
                    third.Close,
                    stop,
                    "Bearish 3-bar reversal inside the active 4H range.",
                    closed,
                    "3-Bar Reversal",
                    outerBodyRatio);
            }
        }

        return null;
    }

    private NySessionSignal? TryFindBreakoutCandleSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        var consolidationCount = Math.Max(2, _strategyOptions.BreakoutConsolidationCandles);
        if (!_strategyOptions.BreakoutCandleEnabled || closed.Count < consolidationCount + 1 || upperBoundary <= lowerBoundary)
        {
            return null;
        }

        var breakout = closed[^1];
        var consolidation = closed
            .Skip(closed.Count - consolidationCount - 1)
            .Take(consolidationCount)
            .ToArray();
        if (consolidation.Any(candle =>
                !IsInsideRange(candle.Close, upperBoundary, lowerBoundary) ||
                !IsInsideRange(candle.High, upperBoundary, lowerBoundary) ||
                !IsInsideRange(candle.Low, upperBoundary, lowerBoundary)))
        {
            return null;
        }

        var consolidationHigh = consolidation.Max(candle => candle.High);
        var consolidationLow = consolidation.Min(candle => candle.Low);
        var consolidationMid = (consolidationHigh + consolidationLow) / 2m;
        var consolidationRangePercent = consolidationMid > 0m
            ? (consolidationHigh - consolidationLow) / consolidationMid * 100m
            : 0m;
        if (consolidationHigh <= consolidationLow ||
            consolidationRangePercent > _strategyOptions.MaxBreakoutConsolidationRangePercent)
        {
            return null;
        }

        var averageBody = consolidation.Average(candle => Math.Abs(candle.Close - candle.Open));
        var breakoutBody = Math.Abs(breakout.Close - breakout.Open);
        if (averageBody <= 0m || breakoutBody / averageBody < _strategyOptions.MinBreakoutBodyRatio)
        {
            return null;
        }

        var bullish = breakout.Close > breakout.Open &&
            breakout.Close > consolidationHigh &&
            breakout.Open <= consolidationHigh &&
            IsInsideRange(breakout.Open, upperBoundary, lowerBoundary);
        if (bullish)
        {
            var risk = breakout.Close - consolidationLow;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Long",
                    breakout.OpenTime,
                    breakout.OpenTime,
                    consolidationHigh,
                    upperBoundary,
                    lowerBoundary,
                    breakout.Close,
                    consolidationLow,
                    "Bullish breakout candle after consolidation.",
                    closed,
                    "Breakout Candle",
                    breakoutBody / averageBody);
            }
        }

        var bearish = breakout.Close < breakout.Open &&
            breakout.Close < consolidationLow &&
            breakout.Open >= consolidationLow &&
            IsInsideRange(breakout.Open, upperBoundary, lowerBoundary);
        if (bearish)
        {
            var risk = consolidationHigh - breakout.Close;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Short",
                    breakout.OpenTime,
                    breakout.OpenTime,
                    consolidationLow,
                    upperBoundary,
                    lowerBoundary,
                    breakout.Close,
                    consolidationHigh,
                    "Bearish breakout candle after consolidation.",
                    closed,
                    "Breakout Candle",
                    breakoutBody / averageBody);
            }
        }

        return null;
    }

    private NySessionSignal? TryFindShrinkingCandlesSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        var sequenceCount = Math.Max(3, _strategyOptions.ShrinkingSequenceCandles);
        if (!_strategyOptions.ShrinkingCandlesEnabled || closed.Count < sequenceCount + 1 || upperBoundary <= lowerBoundary)
        {
            return null;
        }

        var sequence = closed
            .Skip(closed.Count - sequenceCount - 1)
            .Take(sequenceCount)
            .ToArray();
        var reversal = closed[^1];
        var all = sequence.Append(reversal).ToArray();
        if (all.Any(candle =>
                !IsInsideRange(candle.Close, upperBoundary, lowerBoundary) ||
                !IsInsideRange(candle.High, upperBoundary, lowerBoundary) ||
                !IsInsideRange(candle.Low, upperBoundary, lowerBoundary)))
        {
            return null;
        }

        var bodies = sequence.Select(candle => Math.Abs(candle.Close - candle.Open)).ToArray();
        if (bodies.Any(body => body <= 0m))
        {
            return null;
        }

        for (var index = 1; index < bodies.Length; index++)
        {
            if (bodies[index - 1] / bodies[index] < _strategyOptions.MinShrinkingBodyStepRatio)
            {
                return null;
            }
        }

        var reversalBody = Math.Abs(reversal.Close - reversal.Open);
        var reversalBodyRatio = reversalBody / bodies.Average();
        if (reversalBody <= 0m || reversalBodyRatio < _strategyOptions.MinShrinkingReversalBodyRatio)
        {
            return null;
        }

        var bullish = sequence.All(candle => candle.Close < candle.Open) &&
            reversal.Close > reversal.Open &&
            reversal.Close > sequence[0].Open;
        if (bullish)
        {
            var stop = all.Min(candle => candle.Low);
            var risk = reversal.Close - stop;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Long",
                    reversal.OpenTime,
                    reversal.OpenTime,
                    lowerBoundary,
                    upperBoundary,
                    lowerBoundary,
                    reversal.Close,
                    stop,
                    "Bullish shrinking candles reversal inside the active 4H range.",
                    closed,
                    "Shrinking Candles",
                    reversalBodyRatio);
            }
        }

        var bearish = sequence.All(candle => candle.Close > candle.Open) &&
            reversal.Close < reversal.Open &&
            reversal.Close < sequence[0].Open;
        if (bearish)
        {
            var stop = all.Max(candle => candle.High);
            var risk = stop - reversal.Close;
            if (risk > 0m)
            {
                return BuildSignal(
                    "Short",
                    reversal.OpenTime,
                    reversal.OpenTime,
                    upperBoundary,
                    upperBoundary,
                    lowerBoundary,
                    reversal.Close,
                    stop,
                    "Bearish shrinking candles reversal inside the active 4H range.",
                    closed,
                    "Shrinking Candles",
                    reversalBodyRatio);
            }
        }

        return null;
    }

    private BacktestFilterResult EvaluateBacktestFilters(
        NySessionSignal signal,
        IReadOnlyList<Candle> fiveMinuteCandlesSoFar,
        IReadOnlyList<Candle> fifteenMinuteCandles,
        IReadOnlyList<Candle> btc15m)
    {
        if (string.Equals(signal.Pattern, "Engulfing", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateEngulfingBacktestFilters(signal, btc15m);
        }

        if (string.Equals(signal.Pattern, "Pinbar", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluatePinbarBacktestFilters(signal, btc15m);
        }

        if (string.Equals(signal.Pattern, "3-Bar Continuation", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateThreeBarContinuationBacktestFilters(signal, btc15m);
        }

        if (string.Equals(signal.Pattern, "3-Bar Reversal", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateThreeBarReversalBacktestFilters(signal, btc15m);
        }

        if (string.Equals(signal.Pattern, "Breakout Candle", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateBreakoutCandleBacktestFilters(signal, btc15m);
        }

        if (string.Equals(signal.Pattern, "Shrinking Candles", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateShrinkingCandlesBacktestFilters(signal, btc15m);
        }

        if (signal.SweepDepthPercent < _strategyOptions.MinSweepDepthPercent)
        {
            return new BacktestFilterResult(false, false);
        }

        if (signal.ReclaimPercent < _strategyOptions.MinReclaimPercent)
        {
            return new BacktestFilterResult(false, false);
        }

        if (IsStopDistanceOutsideBounds(signal))
        {
            return new BacktestFilterResult(false, false);
        }

        if (signal.MidlineRoomR < _strategyOptions.MinMidlineRoomR)
        {
            return new BacktestFilterResult(false, false);
        }

        var symbol15mSoFar = fifteenMinuteCandles
            .Where(candle => candle.OpenTime.AddMinutes(15) <= signal.SignalCandleOpenTime.AddMinutes(5))
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        var trueBreakout = AnalyzeTrueBreakout(signal, fiveMinuteCandlesSoFar, symbol15mSoFar);
        if (trueBreakout)
        {
            return new BacktestFilterResult(false, true);
        }

        if (signal.BreakoutVolumeRatio >= _strategyOptions.HighBreakoutVolumeRatio)
        {
            return new BacktestFilterResult(false, false);
        }

        var btc15mSoFar = btc15m
            .Where(candle => candle.OpenTime.AddMinutes(15) <= signal.SignalCandleOpenTime.AddMinutes(5))
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        if (IsBtcTrendAgainstSignal(signal.Side, btc15mSoFar))
        {
            return new BacktestFilterResult(false, false);
        }

        return new BacktestFilterResult(true, false);
    }

    private BacktestFilterResult EvaluateShrinkingCandlesBacktestFilters(
        NySessionSignal signal,
        IReadOnlyList<Candle> btc15m)
    {
        if (!_strategyOptions.ShrinkingCandlesEnabled ||
            signal.BodyRatio < _strategyOptions.MinShrinkingReversalBodyRatio ||
            IsStopDistanceOutsideBounds(signal))
        {
            return new BacktestFilterResult(false, false);
        }

        var btc15mSoFar = btc15m
            .Where(candle => candle.OpenTime.AddMinutes(15) <= signal.SignalCandleOpenTime.AddMinutes(5))
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        if (IsBtcTrendAgainstSignal(signal.Side, btc15mSoFar))
        {
            return new BacktestFilterResult(false, false);
        }

        return new BacktestFilterResult(true, false);
    }

    private BacktestFilterResult EvaluateBreakoutCandleBacktestFilters(
        NySessionSignal signal,
        IReadOnlyList<Candle> btc15m)
    {
        if (!_strategyOptions.BreakoutCandleEnabled ||
            signal.BodyRatio < _strategyOptions.MinBreakoutBodyRatio ||
            IsStopDistanceOutsideBounds(signal))
        {
            return new BacktestFilterResult(false, false);
        }

        var btc15mSoFar = btc15m
            .Where(candle => candle.OpenTime.AddMinutes(15) <= signal.SignalCandleOpenTime.AddMinutes(5))
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        if (IsBtcTrendAgainstSignal(signal.Side, btc15mSoFar))
        {
            return new BacktestFilterResult(false, false);
        }

        return new BacktestFilterResult(true, false);
    }

    private BacktestFilterResult EvaluateThreeBarReversalBacktestFilters(
        NySessionSignal signal,
        IReadOnlyList<Candle> btc15m)
    {
        if (!_strategyOptions.ThreeBarReversalEnabled ||
            signal.BodyRatio < _strategyOptions.MinThreeBarOuterBodyRatio ||
            IsStopDistanceOutsideBounds(signal))
        {
            return new BacktestFilterResult(false, false);
        }

        var btc15mSoFar = btc15m
            .Where(candle => candle.OpenTime.AddMinutes(15) <= signal.SignalCandleOpenTime.AddMinutes(5))
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        if (IsBtcTrendAgainstSignal(signal.Side, btc15mSoFar))
        {
            return new BacktestFilterResult(false, false);
        }

        return new BacktestFilterResult(true, false);
    }

    private BacktestFilterResult EvaluateThreeBarContinuationBacktestFilters(
        NySessionSignal signal,
        IReadOnlyList<Candle> btc15m)
    {
        if (!_strategyOptions.ThreeBarContinuationEnabled ||
            signal.BodyRatio < _strategyOptions.MinThreeBarOuterBodyRatio ||
            IsStopDistanceOutsideBounds(signal))
        {
            return new BacktestFilterResult(false, false);
        }

        var btc15mSoFar = btc15m
            .Where(candle => candle.OpenTime.AddMinutes(15) <= signal.SignalCandleOpenTime.AddMinutes(5))
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        if (IsBtcTrendAgainstSignal(signal.Side, btc15mSoFar))
        {
            return new BacktestFilterResult(false, false);
        }

        return new BacktestFilterResult(true, false);
    }

    private BacktestFilterResult EvaluatePinbarBacktestFilters(
        NySessionSignal signal,
        IReadOnlyList<Candle> btc15m)
    {
        if (!_strategyOptions.PinbarEnabled ||
            signal.WickBodyRatio < _strategyOptions.MinPinbarWickBodyRatio ||
            signal.WickRangePercent < _strategyOptions.MinPinbarWickRangePercent ||
            IsStopDistanceOutsideBounds(signal))
        {
            return new BacktestFilterResult(false, false);
        }

        var btc15mSoFar = btc15m
            .Where(candle => candle.OpenTime.AddMinutes(15) <= signal.SignalCandleOpenTime.AddMinutes(5))
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        if (IsBtcTrendAgainstSignal(signal.Side, btc15mSoFar))
        {
            return new BacktestFilterResult(false, false);
        }

        return new BacktestFilterResult(true, false);
    }

    private BacktestFilterResult EvaluateEngulfingBacktestFilters(
        NySessionSignal signal,
        IReadOnlyList<Candle> btc15m)
    {
        if (!_strategyOptions.EngulfingEnabled ||
            signal.BodyRatio < _strategyOptions.MinEngulfingBodyRatio ||
            IsStopDistanceOutsideBounds(signal))
        {
            return new BacktestFilterResult(false, false);
        }

        var btc15mSoFar = btc15m
            .Where(candle => candle.OpenTime.AddMinutes(15) <= signal.SignalCandleOpenTime.AddMinutes(5))
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        if (IsBtcTrendAgainstSignal(signal.Side, btc15mSoFar))
        {
            return new BacktestFilterResult(false, false);
        }

        return new BacktestFilterResult(true, false);
    }

    private bool IsStopDistanceOutsideBounds(NySessionSignal signal) =>
        signal.StopDistancePercent < _strategyOptions.MinStopPercent ||
        signal.StopDistancePercent > _strategyOptions.MaxStopPercent;

    private bool AnalyzeTrueBreakout(
        NySessionSignal signal,
        IReadOnlyList<Candle> fiveMinuteCandlesSoFar,
        IReadOnlyList<Candle> fifteenMinuteCandlesSoFar)
    {
        var signalClosedAt = signal.SignalCandleOpenTime.AddMinutes(5);
        var closed5m = fiveMinuteCandlesSoFar
            .Where(candle => candle.OpenTime.AddMinutes(5) <= signalClosedAt)
            .Where(candle => candle.OpenTime >= signal.BreakoutCandleOpenTime && candle.OpenTime < signal.SignalCandleOpenTime)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        var closed15m = fifteenMinuteCandlesSoFar
            .Where(candle => candle.OpenTime.AddMinutes(15) <= signalClosedAt)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        var last15m = closed15m.LastOrDefault();
        var adx = CalculateAdx(closed15m.TakeLast(40).ToArray(), 14);
        var previousAdx = CalculateAdx(closed15m.TakeLast(45).SkipLast(5).ToArray(), 14);
        var adxRising = adx >= _strategyOptions.TrueBreakoutAdx && adx > previousAdx;
        var highVolume = signal.BreakoutVolumeRatio >= _strategyOptions.HighBreakoutVolumeRatio;
        var fiveMinuteHeld = signal.Side == "Short"
            ? closed5m.Count(candle => candle.Close > signal.Boundary) >= 1
            : closed5m.Count(candle => candle.Close < signal.Boundary) >= 1;
        var fifteenMinuteOutside = last15m is not null && (signal.Side == "Short"
            ? last15m.Close > signal.Boundary
            : last15m.Close < signal.Boundary);

        return (highVolume || adxRising) && (fiveMinuteHeld || fifteenMinuteOutside);
    }

    private bool IsBtcTrendAgainstSignal(string signalSide, IReadOnlyList<Candle> btc15mSoFar)
    {
        var closed = btc15mSoFar.OrderBy(candle => candle.OpenTime).ToArray();
        if (closed.Length < 30)
        {
            return false;
        }

        var last = closed[^1];
        var baseline = closed.TakeLast(12).First();
        var movePercent = baseline.Open > 0m ? (last.Close - baseline.Open) / baseline.Open * 100m : 0m;
        var adx = CalculateAdx(closed.TakeLast(40).ToArray(), 14);
        var btcTrendingHard = Math.Abs(movePercent) >= _strategyOptions.BtcTrendMovePercent && adx >= _strategyOptions.BtcTrendAdx;
        return btcTrendingHard && (signalSide == "Short" && movePercent > 0m || signalSide == "Long" && movePercent < 0m);
    }

    private BacktestTradeInternal SimulateTrade(
        string symbol,
        NySessionSignal signal,
        IReadOnlyList<Candle> session,
        IReadOnlyList<Candle> allFiveMinuteCandles,
        PrecomputedTurtleChannelExits turtleExits,
        int startIndex,
        BacktestRunSettings settings,
        decimal accountEquityUsdt,
        CancellationToken cancellationToken)
    {
        var useTurtleChannelExit = string.Equals(signal.Pattern, TurtleTrendStrategy.Name, StringComparison.OrdinalIgnoreCase) &&
            signal.TakeProfit <= 0m;
        if (useTurtleChannelExit)
        {
            return SimulateTurtleTrade(symbol, signal, session, allFiveMinuteCandles, turtleExits, settings, accountEquityUsdt, cancellationToken);
        }

        var isShort = signal.Side == "Short";
        var entryPrice = ApplySlippage(signal.EntryPrice, isShort, isEntry: true, settings.SlippagePercent);
        var quantity = settings.EntryNotionalUsdt / entryPrice;
        var riskPerUnit = Math.Abs(entryPrice - signal.StopLoss);
        var exitPrice = session[^1].Close;
        var exitTime = session[^1].OpenTime;
        var exitReason = "SessionClose";
        var exitIndex = session.Count - 1;

        for (var i = startIndex; i < session.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candle = session[i];
            if (isShort)
            {
                if (candle.High >= signal.StopLoss)
                {
                    exitPrice = signal.StopLoss;
                    exitTime = candle.OpenTime;
                    exitReason = "StopLoss";
                    exitIndex = i;
                    break;
                }

                if (signal.TakeProfit > 0m && candle.Low <= signal.TakeProfit)
                {
                    exitPrice = signal.TakeProfit;
                    exitTime = candle.OpenTime;
                    exitReason = "TakeProfit";
                    exitIndex = i;
                    break;
                }
            }
            else
            {
                if (candle.Low <= signal.StopLoss)
                {
                    exitPrice = signal.StopLoss;
                    exitTime = candle.OpenTime;
                    exitReason = "StopLoss";
                    exitIndex = i;
                    break;
                }

                if (signal.TakeProfit > 0m && candle.High >= signal.TakeProfit)
                {
                    exitPrice = signal.TakeProfit;
                    exitTime = candle.OpenTime;
                    exitReason = "TakeProfit";
                    exitIndex = i;
                    break;
                }
            }
        }

        exitPrice = ApplySlippage(exitPrice, isShort, isEntry: false, settings.SlippagePercent);
        var grossPnl = isShort
            ? (entryPrice - exitPrice) * quantity
            : (exitPrice - entryPrice) * quantity;
        var entryNotional = entryPrice * quantity;
        var exitNotional = exitPrice * quantity;
        var exitFeePercent = exitReason == "TakeProfit" ? settings.MakerFeePercent : settings.TakerFeePercent;
        var fees = entryNotional * settings.TakerFeePercent / 100m + exitNotional * exitFeePercent / 100m;
        var slippageCost = settings.EntryNotionalUsdt * settings.SlippagePercent / 100m * 2m;
        var holdingHours = decimal.Max(0m, (decimal)(exitTime - signal.SignalCandleOpenTime).TotalHours);
        var fundingCost = settings.EntryNotionalUsdt * settings.FundingPercentPer8h / 100m * holdingHours / 8m;
        var netPnl = grossPnl - fees - fundingCost;
        var initialRiskUsdt = riskPerUnit * quantity;
        var rMultiple = initialRiskUsdt > 0m ? netPnl / initialRiskUsdt : 0m;

        return new BacktestTradeInternal(
            symbol,
            signal.Side,
            signal.Pattern,
            signal.SignalCandleOpenTime,
            exitTime,
            entryPrice,
            exitPrice,
            signal.StopLoss,
            signal.TakeProfit,
            grossPnl,
            fees,
            slippageCost,
            fundingCost,
            netPnl,
            rMultiple,
            exitReason,
            exitIndex);
    }

    private BacktestTradeInternal SimulateTurtleTrade(
        string symbol,
        NySessionSignal signal,
        IReadOnlyList<Candle> session,
        IReadOnlyList<Candle> allFiveMinuteCandles,
        PrecomputedTurtleChannelExits turtleExits,
        BacktestRunSettings settings,
        decimal accountEquityUsdt,
        CancellationToken cancellationToken)
    {
        var candles = allFiveMinuteCandles;
        var startIndex = FindFirstOpenTimeAfter(candles, signal.SignalCandleOpenTime);
        if (startIndex < 0)
        {
            startIndex = candles.Count - 1;
        }

        var isShort = signal.Side == "Short";
        var nValue = signal.TurtleN > 0m ? signal.TurtleN : Math.Abs(signal.EntryPrice - signal.StopLoss) / _turtleOptions.StopAtrMultiplier;
        var dollarVolatility = nValue * _turtleOptions.PointValueUsdt;
        var unitQuantity = dollarVolatility > 0m
            ? Math.Floor(accountEquityUsdt * _turtleOptions.RiskPerUnitPercent / 100m / dollarVolatility * 1_000_000m) / 1_000_000m
            : 0m;
        if (unitQuantity <= 0m)
        {
            unitQuantity = settings.EntryNotionalUsdt / signal.EntryPrice;
        }

        var firstEntryPrice = ApplySlippage(
            signal.TurtleBreakoutLevel > 0m ? signal.TurtleBreakoutLevel : signal.EntryPrice,
            isShort,
            isEntry: true,
            settings.SlippagePercent);
        var entryPrices = new List<decimal> { firstEntryPrice };
        var quantities = new List<decimal> { unitQuantity };
        var protectedStop = isShort
            ? firstEntryPrice + nValue * _turtleOptions.StopAtrMultiplier
            : firstEntryPrice - nValue * _turtleOptions.StopAtrMultiplier;
        var nextAddLevel = isShort
            ? firstEntryPrice - nValue * _turtleOptions.AddAtrInterval
            : firstEntryPrice + nValue * _turtleOptions.AddAtrInterval;
        var entryFees = firstEntryPrice * unitQuantity * settings.TakerFeePercent / 100m;
        var totalEntryNotional = firstEntryPrice * unitQuantity;
        var totalSlippageNotional = totalEntryNotional;
        var exitPrice = candles[startIndex].Close;
        var exitTime = candles[startIndex].OpenTime;
        var exitReason = "BacktestEnd";
        var exitIndex = ResolveSessionExitIndex(session, exitTime);
        var units = 1;

        for (var i = startIndex; i < candles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candle = candles[i];
            if (isShort)
            {
                if (candle.High >= protectedStop)
                {
                    exitPrice = protectedStop;
                    exitTime = candle.OpenTime;
                    exitReason = "StopLoss";
                    exitIndex = ResolveSessionExitIndex(session, exitTime);
                    break;
                }

                if (IsBacktestTurtleChannelExit(candles, turtleExits, i, StrategySide.Short, signal.TurtleSystem))
                {
                    exitPrice = candle.Close;
                    exitTime = candle.OpenTime;
                    exitReason = "ChannelExit";
                    exitIndex = ResolveSessionExitIndex(session, exitTime);
                    break;
                }

                if (_turtleOptions.UsePyramiding && units < _turtleOptions.MaxUnits && candle.Low <= nextAddLevel)
                {
                    var addPrice = ApplySlippage(nextAddLevel, isShort, isEntry: true, settings.SlippagePercent);
                    entryPrices.Add(addPrice);
                    quantities.Add(unitQuantity);
                    entryFees += addPrice * unitQuantity * settings.TakerFeePercent / 100m;
                    totalEntryNotional += addPrice * unitQuantity;
                    totalSlippageNotional += addPrice * unitQuantity;
                    units++;
                    protectedStop = addPrice + nValue * _turtleOptions.StopAtrMultiplier;
                    nextAddLevel = addPrice - nValue * _turtleOptions.AddAtrInterval;
                }
            }
            else
            {
                if (candle.Low <= protectedStop)
                {
                    exitPrice = protectedStop;
                    exitTime = candle.OpenTime;
                    exitReason = "StopLoss";
                    exitIndex = ResolveSessionExitIndex(session, exitTime);
                    break;
                }

                if (IsBacktestTurtleChannelExit(candles, turtleExits, i, StrategySide.Long, signal.TurtleSystem))
                {
                    exitPrice = candle.Close;
                    exitTime = candle.OpenTime;
                    exitReason = "ChannelExit";
                    exitIndex = ResolveSessionExitIndex(session, exitTime);
                    break;
                }

                if (_turtleOptions.UsePyramiding && units < _turtleOptions.MaxUnits && candle.High >= nextAddLevel)
                {
                    var addPrice = ApplySlippage(nextAddLevel, isShort, isEntry: true, settings.SlippagePercent);
                    entryPrices.Add(addPrice);
                    quantities.Add(unitQuantity);
                    entryFees += addPrice * unitQuantity * settings.TakerFeePercent / 100m;
                    totalEntryNotional += addPrice * unitQuantity;
                    totalSlippageNotional += addPrice * unitQuantity;
                    units++;
                    protectedStop = addPrice - nValue * _turtleOptions.StopAtrMultiplier;
                    nextAddLevel = addPrice + nValue * _turtleOptions.AddAtrInterval;
                }
            }

            exitPrice = candle.Close;
            exitTime = candle.OpenTime;
            exitIndex = ResolveSessionExitIndex(session, exitTime);
        }

        var isOpenAtBacktestEnd = exitReason == "BacktestEnd";
        exitPrice = isOpenAtBacktestEnd
            ? exitPrice
            : ApplySlippage(exitPrice, isShort, isEntry: false, settings.SlippagePercent);
        var grossPnl = 0m;
        for (var unitIndex = 0; unitIndex < entryPrices.Count; unitIndex++)
        {
            grossPnl += isShort
                ? (entryPrices[unitIndex] - exitPrice) * quantities[unitIndex]
                : (exitPrice - entryPrices[unitIndex]) * quantities[unitIndex];
        }

        var totalQuantity = quantities.Sum();
        var averageEntryPrice = totalQuantity > 0m ? totalEntryNotional / totalQuantity : firstEntryPrice;
        var exitNotional = exitPrice * totalQuantity;
        var fees = entryFees +
            (isOpenAtBacktestEnd ? 0m : exitNotional * settings.TakerFeePercent / 100m);
        var slippageCost = (totalSlippageNotional + (isOpenAtBacktestEnd ? 0m : exitNotional)) * settings.SlippagePercent / 100m;
        var holdingHours = decimal.Max(0m, (decimal)(exitTime - signal.SignalCandleOpenTime).TotalHours);
        var fundingCost = totalEntryNotional * settings.FundingPercentPer8h / 100m * holdingHours / 8m;
        var netPnl = grossPnl - fees - fundingCost;
        var initialRiskUsdt = nValue * _turtleOptions.StopAtrMultiplier * unitQuantity;
        var rMultiple = initialRiskUsdt > 0m ? netPnl / initialRiskUsdt : 0m;

        return new BacktestTradeInternal(
            symbol,
            signal.Side,
            signal.Pattern,
            signal.SignalCandleOpenTime,
            exitTime,
            averageEntryPrice,
            exitPrice,
            protectedStop,
            signal.TakeProfit,
            grossPnl,
            fees,
            slippageCost,
            fundingCost,
            netPnl,
            rMultiple,
            exitReason,
            exitIndex);
    }

    private bool IsBacktestTurtleChannelExit(
        IReadOnlyList<Candle> candles,
        PrecomputedTurtleChannelExits exits,
        int index,
        StrategySide side,
        string turtleSystem)
    {
        if (!_turtleOptions.UseChannelExit)
        {
            return false;
        }

        var current = candles[index];
        var isSlow = string.Equals(turtleSystem, "S2", StringComparison.OrdinalIgnoreCase);
        var exitLow = exits.GetLow(index, isSlow);
        var exitHigh = exits.GetHigh(index, isSlow);
        return side == StrategySide.Long
            ? exitLow > 0m && current.Close < exitLow
            : exitHigh > 0m && current.Close > exitHigh;
    }

    private decimal UpdateTurtleProtectedStop(
        IReadOnlyList<Candle> candles,
        int index,
        bool isShort,
        decimal entryPrice,
        decimal riskPerUnit,
        decimal currentStop)
    {
        if (!_turtleOptions.UseProfitLock || riskPerUnit <= 0m || index < 0 || index >= candles.Count)
        {
            return currentStop;
        }

        var current = candles[index];
        var rMultiple = isShort
            ? (entryPrice - current.Close) / riskPerUnit
            : (current.Close - entryPrice) / riskPerUnit;
        var nextStop = currentStop;
        if (rMultiple >= _turtleOptions.BreakevenTriggerR)
        {
            nextStop = isShort
                ? decimal.Min(nextStop, entryPrice)
                : decimal.Max(nextStop, entryPrice);
        }

        if (rMultiple >= _turtleOptions.LockTriggerR)
        {
            var lockStop = isShort
                ? entryPrice - riskPerUnit * _turtleOptions.LockProfitR
                : entryPrice + riskPerUnit * _turtleOptions.LockProfitR;
            nextStop = isShort
                ? decimal.Min(nextStop, lockStop)
                : decimal.Max(nextStop, lockStop);
        }

        if (_turtleOptions.UseTrailingAtrStop && rMultiple >= _turtleOptions.AtrTrailTriggerR)
        {
            var lookback = candles.Take(index + 1).ToArray();
            var atr = TradingIndicatorMath.Atr(lookback, _turtleOptions.AtrPeriod);
            if (atr > 0m)
            {
                var atrStop = isShort
                    ? current.Close + atr * _turtleOptions.AtrTrailMultiplier
                    : current.Close - atr * _turtleOptions.AtrTrailMultiplier;
                nextStop = isShort
                    ? decimal.Min(nextStop, atrStop)
                    : decimal.Max(nextStop, atrStop);
            }
        }

        return nextStop;
    }

    private static int ResolveSessionExitIndex(IReadOnlyList<Candle> session, DateTimeOffset exitTime)
    {
        for (var i = 0; i < session.Count; i++)
        {
            if (session[i].OpenTime >= exitTime)
            {
                return i;
            }
        }

        return session.Count - 1;
    }

    private FuturesBacktestResult BuildResult(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        int symbolsRequested,
        int symbolsProcessed,
        IReadOnlyList<BacktestTradeInternal> trades,
        int falseBreakoutCount,
        int trueBreakoutBlockedCount,
        BacktestRunSettings settings)
    {
        var splitAt = periodEnd.AddDays(-30);
        if (splitAt <= periodStart)
        {
            splitAt = periodStart.AddTicks((periodEnd - periodStart).Ticks * 2 / 3);
        }

        var closedTrades = trades
            .Where(trade => !IsOpenAtBacktestEnd(trade))
            .OrderBy(trade => trade.EntryTime)
            .ToArray();
        var openAtBacktestEndTrades = trades
            .Where(IsOpenAtBacktestEnd)
            .OrderBy(trade => trade.EntryTime)
            .ToArray();
        var optimizationTrades = closedTrades
            .Where(trade => trade.EntryTime < splitAt)
            .OrderBy(trade => trade.EntryTime)
            .ToArray();
        var optimizationSymbols = BuildSymbolPerformance(optimizationTrades);
        var eligibleSymbols = optimizationSymbols
            .Where(item => item.Trades >= 3)
            .Where(item => item.ProfitFactor > 1m && item.AverageR > 0m && item.NetPnl > 0m)
            .Select(item => item.Symbol)
            .OrderBy(symbol => symbol)
            .ToArray();
        var eligibleSet = eligibleSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var optimizationStrategyGates = BuildStrategyGatePerformance(optimizationTrades);
        var outOfSampleTrades = closedTrades
            .Where(trade => trade.EntryTime >= splitAt)
            .OrderBy(trade => trade.EntryTime)
            .ToArray();
        var outOfSampleOpenTrades = openAtBacktestEndTrades
            .Where(trade => trade.EntryTime >= splitAt)
            .OrderBy(trade => trade.EntryTime)
            .ToArray();
        var outOfSampleStrategyGates = BuildStrategyGatePerformance(outOfSampleTrades)
            .ToDictionary(
                item => BuildStrategyGateKey(item.StrategyName, item.Symbol, item.Direction),
                StringComparer.OrdinalIgnoreCase);
        var eligibleStrategySymbolDirections = optimizationStrategyGates
            .Where(item => IsLiveGateStrategyEnabled(item.StrategyName))
            .Where(item => item.TradesCount >= _strategyRoutingOptions.MinTradesForStrategySymbolGating)
            .Where(item =>
                item.ProfitFactor >= _strategyRoutingOptions.MinProfitFactorToEnable &&
                item.AverageR >= _strategyRoutingOptions.MinAverageRToEnable &&
                item.NetPnl > 0m)
            .Where(item => IsOosGateConfirmed(item, outOfSampleStrategyGates))
            .Select(item => BuildStrategyGateKey(item.StrategyName, item.Symbol, item.Direction))
            .OrderBy(key => key)
            .ToArray();
        var eligibleStrategyGateSet = eligibleStrategySymbolDirections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filteredOutOfSampleTrades = outOfSampleTrades
            .Where(trade => eligibleStrategyGateSet.Contains(BuildStrategyGateKey(trade.Pattern, trade.Symbol, trade.Side)))
            .OrderBy(trade => trade.EntryTime)
            .ToArray();
        var filteredOutOfSampleOpenTrades = outOfSampleOpenTrades
            .Where(trade => eligibleStrategyGateSet.Contains(BuildStrategyGateKey(trade.Pattern, trade.Symbol, trade.Side)))
            .OrderBy(trade => trade.EntryTime)
            .ToArray();
        var tradedSymbols = closedTrades
            .Select(trade => trade.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedSymbols = tradedSymbols
            .Where(symbol => !eligibleSet.Contains(symbol))
            .OrderBy(symbol => symbol)
            .ToArray();
        var tradedStrategyGates = BuildStrategyGatePerformance(closedTrades)
            .Select(item => BuildStrategyGateKey(item.StrategyName, item.Symbol, item.Direction))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedStrategySymbolDirections = tradedStrategyGates
            .Where(key => !eligibleStrategyGateSet.Contains(key))
            .OrderBy(key => key)
            .ToArray();
        var publicTrades = outOfSampleTrades.Select(ToPublicTrade).ToArray();
        var publicOpenAtEndTrades = openAtBacktestEndTrades.Select(ToPublicTrade).ToArray();
        return new FuturesBacktestResult
        {
            StrategyName = settings.Mode == FuturesBacktestMode.TurtleOnly
                ? "Turtle-only Donchian S1/S2 trend backtest"
                : _strategyRoutingOptions.SignalSelectionMode == SignalSelectionMode.ScoreBased
                ? "NY 08:00 Regime Router: Sweep Reversal + Turtle Trend + Breakout Retest with candle confirmations"
                : "NY 08:00 4H Sweep Reversal + Engulfing + Pinbar + 3-Bar Continuation + 3-Bar Reversal + Breakout Candle + Shrinking Candles",
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            SymbolsRequested = symbolsRequested,
            SymbolsProcessed = symbolsProcessed,
            TradesCount = outOfSampleTrades.Length,
            FalseBreakoutCount = falseBreakoutCount,
            TrueBreakoutBlockedCount = trueBreakoutBlockedCount,
            OpenAtBacktestEndCount = openAtBacktestEndTrades.Length,
            OpenAtBacktestEndUnrealizedPnl = openAtBacktestEndTrades.Sum(trade => trade.NetPnl),
            Metrics = BuildMetrics(outOfSampleTrades, splitAt, periodEnd, settings.InitialEquityUsdt, outOfSampleOpenTrades),
            OptimizationMetrics = BuildMetrics(optimizationTrades, periodStart, splitAt, settings.InitialEquityUsdt),
            OutOfSampleMetrics = BuildMetrics(outOfSampleTrades, splitAt, periodEnd, settings.InitialEquityUsdt, outOfSampleOpenTrades),
            FilteredOutOfSampleMetrics = BuildMetrics(filteredOutOfSampleTrades, splitAt, periodEnd, settings.InitialEquityUsdt, filteredOutOfSampleOpenTrades),
            LiveUseEligibleStrategyGatesOnly = _strategyRoutingOptions.LiveUseEligibleStrategyGatesOnly,
            LiveEligibleGateSizeMultiplier = _strategyRoutingOptions.LiveEligibleGateSizeMultiplier,
            LiveIneligibleGateSizeMultiplier = _strategyRoutingOptions.LiveIneligibleGateSizeMultiplier,
            EligibleSymbols = eligibleSymbols,
            ExcludedSymbols = excludedSymbols,
            EligibleStrategySymbolDirections = eligibleStrategySymbolDirections,
            ExcludedStrategySymbolDirections = excludedStrategySymbolDirections,
            BestSymbols = BuildSymbolPerformance(outOfSampleTrades).OrderByDescending(item => item.NetPnl).Take(10).ToArray(),
            WorstSymbols = BuildSymbolPerformance(outOfSampleTrades).OrderBy(item => item.NetPnl).Take(10).ToArray(),
            LongShort = BuildSidePerformance(outOfSampleTrades),
            PatternPerformance = BuildBucketPerformance(outOfSampleTrades, trade => trade.Pattern),
            StrategyPerformance = _strategyPerformanceTracker.Build(publicTrades),
            WeekdayPerformance = BuildBucketPerformance(outOfSampleTrades, trade => trade.EntryTime.DayOfWeek.ToString()),
            HourPerformance = BuildBucketPerformance(outOfSampleTrades, trade => TimeZoneInfo.ConvertTime(trade.EntryTime, ResolveNewYorkTimeZone()).Hour.ToString("00")),
            RecentTrades = publicTrades.OrderByDescending(trade => trade.EntryTime).Take(100).ToArray(),
            OpenAtBacktestEndTrades = publicOpenAtEndTrades.OrderByDescending(trade => trade.EntryTime).Take(100).ToArray()
        };
    }

    private static IReadOnlyList<StrategyGatePerformance> BuildStrategyGatePerformance(IReadOnlyList<BacktestTradeInternal> trades) =>
        trades
            .GroupBy(trade => new
            {
                StrategyName = trade.Pattern,
                trade.Symbol,
                Direction = trade.Side
            })
            .Select(group => new StrategyGatePerformance(
                group.Key.StrategyName,
                group.Key.Symbol,
                group.Key.Direction,
                group.Count(),
                group.Sum(trade => trade.NetPnl),
                CalculateProfitFactor(group),
                group.Any() ? group.Average(trade => trade.RMultiple) : 0m))
            .ToArray();

    private static bool IsOpenAtBacktestEnd(BacktestTradeInternal trade) =>
        string.Equals(trade.ExitReason, "BacktestEnd", StringComparison.OrdinalIgnoreCase);

    private bool IsBacktestLiveEntryAllowed(StrategyCandidate candidate, DateTimeOffset signalTime, TimeZoneInfo nyZone) =>
        IsLiveGateStrategyEnabled(candidate.StrategyName) &&
        IsLiveHourAllowed(signalTime, nyZone);

    private bool IsLiveHourAllowed(DateTimeOffset signalTime, TimeZoneInfo nyZone)
    {
        var allowedHours = ParseAllowedHours(_strategyRoutingOptions.LiveAllowedHours);
        if (allowedHours.Count == 0)
        {
            return true;
        }

        return allowedHours.Contains(TimeZoneInfo.ConvertTime(signalTime, nyZone).Hour);
    }

    private bool IsOosGateConfirmed(
        StrategyGatePerformance optimizationGate,
        IReadOnlyDictionary<string, StrategyGatePerformance> outOfSampleGates)
    {
        var key = BuildStrategyGateKey(optimizationGate.StrategyName, optimizationGate.Symbol, optimizationGate.Direction);
        return outOfSampleGates.TryGetValue(key, out var outOfSampleGate) &&
            outOfSampleGate.TradesCount >= _strategyRoutingOptions.MinOosTradesForStrategySymbolGating &&
            outOfSampleGate.ProfitFactor >= _strategyRoutingOptions.MinOosProfitFactorToEnable &&
            outOfSampleGate.AverageR >= _strategyRoutingOptions.MinOosAverageRToEnable &&
            outOfSampleGate.NetPnl >= 0m;
    }

    private bool IsLiveGateStrategyEnabled(string strategyName) =>
        string.Equals(strategyName, TurtleTrendStrategy.Name, StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(strategyName, NYSweepReversalStrategy.Name, StringComparison.OrdinalIgnoreCase) &&
            _strategyRoutingOptions.NySweepLiveTradingEnabled);

    private static IReadOnlySet<int> ParseAllowedHours(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? new HashSet<int>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => int.TryParse(part, out var hour) ? hour : -1)
                .Where(hour => hour is >= 0 and <= 23)
                .ToHashSet();

    private static string BuildStrategyGateKey(string strategyName, string symbol, string direction) =>
        $"{NormalizeStrategyGateText(strategyName)}:{NormalizeStrategyGateSymbol(symbol)}:{NormalizeStrategyGateText(direction)}";

    private static bool IsStrategyGateKeyForSymbol(string key, string symbol)
    {
        var parts = key.Split(':', StringSplitOptions.TrimEntries);
        return parts.Length == 3 &&
            string.Equals(parts[1], NormalizeStrategyGateSymbol(symbol), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStrategyGateText(string value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string NormalizeStrategyGateSymbol(string value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Trim().ToUpperInvariant();

    private static FuturesBacktestMetrics BuildMetrics(
        IReadOnlyList<BacktestTradeInternal> trades,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        decimal initialEquity,
        IReadOnlyList<BacktestTradeInternal>? openTrades = null)
    {
        var closedNetPnl = trades.Sum(trade => trade.NetPnl);
        var openUnrealizedPnl = openTrades?.Sum(trade => trade.NetPnl) ?? 0m;
        var markToMarketNetPnl = closedNetPnl + openUnrealizedPnl;
        var wins = trades.Count(trade => trade.NetPnl > 0m);
        var grossProfit = trades.Where(trade => trade.NetPnl > 0m).Sum(trade => trade.NetPnl);
        var grossLoss = Math.Abs(trades.Where(trade => trade.NetPnl < 0m).Sum(trade => trade.NetPnl));
        var days = decimal.Max(1m, (decimal)(periodEnd - periodStart).TotalDays);
        var maxDrawdown = CalculateMaxDrawdown(trades, initialEquity);
        var markToMarketTrades = openTrades is { Count: > 0 }
            ? trades.Concat(openTrades).ToArray()
            : trades;
        var markToMarketMaxDrawdown = CalculateMaxDrawdown(markToMarketTrades, initialEquity);
        return new FuturesBacktestMetrics
        {
            NetPnl = closedNetPnl,
            ClosedNetPnl = closedNetPnl,
            OpenUnrealizedPnl = openUnrealizedPnl,
            MarkToMarketNetPnl = markToMarketNetPnl,
            MaxDrawdown = maxDrawdown,
            MaxDrawdownPercent = initialEquity > 0m ? maxDrawdown / initialEquity * 100m : 0m,
            MarkToMarketMaxDrawdown = markToMarketMaxDrawdown,
            MarkToMarketMaxDrawdownPercent = initialEquity > 0m ? markToMarketMaxDrawdown / initialEquity * 100m : 0m,
            WinRate = trades.Count > 0 ? (decimal)wins / trades.Count * 100m : 0m,
            ProfitFactor = grossLoss > 0m ? grossProfit / grossLoss : grossProfit > 0m ? 999m : 0m,
            AverageR = trades.Count > 0 ? trades.Average(trade => trade.RMultiple) : 0m,
            TradesPerDay = trades.Count / days
        };
    }

    private static decimal CalculateMaxDrawdown(IReadOnlyList<BacktestTradeInternal> trades, decimal initialEquity)
    {
        var equity = initialEquity;
        var peak = equity;
        var maxDrawdown = 0m;
        foreach (var trade in trades.OrderBy(trade => trade.ExitTime))
        {
            equity += trade.NetPnl;
            peak = decimal.Max(peak, equity);
            maxDrawdown = decimal.Max(maxDrawdown, peak - equity);
        }

        return maxDrawdown;
    }

    private static IReadOnlyList<FuturesBacktestSymbolPerformance> BuildSymbolPerformance(IReadOnlyList<BacktestTradeInternal> trades) =>
        trades
            .GroupBy(trade => trade.Symbol)
            .Select(group => new FuturesBacktestSymbolPerformance
            {
                Symbol = group.Key,
                Trades = group.Count(),
                NetPnl = group.Sum(trade => trade.NetPnl),
                WinRate = group.Any() ? (decimal)group.Count(trade => trade.NetPnl > 0m) / group.Count() * 100m : 0m,
                ProfitFactor = CalculateProfitFactor(group),
                AverageR = group.Any() ? group.Average(trade => trade.RMultiple) : 0m
            })
            .ToArray();

    private static IReadOnlyList<FuturesBacktestSidePerformance> BuildSidePerformance(IReadOnlyList<BacktestTradeInternal> trades) =>
        trades
            .GroupBy(trade => trade.Side)
            .Select(group => new FuturesBacktestSidePerformance
            {
                Side = group.Key,
                Trades = group.Count(),
                NetPnl = group.Sum(trade => trade.NetPnl),
                WinRate = group.Any() ? (decimal)group.Count(trade => trade.NetPnl > 0m) / group.Count() * 100m : 0m,
                ProfitFactor = CalculateProfitFactor(group),
                AverageR = group.Any() ? group.Average(trade => trade.RMultiple) : 0m
            })
            .OrderBy(item => item.Side)
            .ToArray();

    private static IReadOnlyList<FuturesBacktestBucketPerformance> BuildBucketPerformance(
        IReadOnlyList<BacktestTradeInternal> trades,
        Func<BacktestTradeInternal, string> bucketSelector) =>
        trades
            .GroupBy(bucketSelector)
            .Select(group => new FuturesBacktestBucketPerformance
            {
                Bucket = group.Key,
                Trades = group.Count(),
                NetPnl = group.Sum(trade => trade.NetPnl),
                WinRate = group.Any() ? (decimal)group.Count(trade => trade.NetPnl > 0m) / group.Count() * 100m : 0m,
                ProfitFactor = CalculateProfitFactor(group),
                AverageR = group.Any() ? group.Average(trade => trade.RMultiple) : 0m
            })
            .OrderBy(item => item.Bucket)
            .ToArray();

    private static decimal CalculateProfitFactor(IEnumerable<BacktestTradeInternal> trades)
    {
        var items = trades.ToArray();
        var grossProfit = items.Where(trade => trade.NetPnl > 0m).Sum(trade => trade.NetPnl);
        var grossLoss = Math.Abs(items.Where(trade => trade.NetPnl < 0m).Sum(trade => trade.NetPnl));
        return grossLoss > 0m ? grossProfit / grossLoss : grossProfit > 0m ? 999m : 0m;
    }

    private static FuturesBacktestTrade ToPublicTrade(BacktestTradeInternal trade) => new()
    {
        Symbol = trade.Symbol,
        StrategyName = trade.Pattern,
        Side = trade.Side,
        Pattern = trade.Pattern,
        EntryTime = trade.EntryTime,
        ExitTime = trade.ExitTime,
        EntryPrice = trade.EntryPrice,
        ExitPrice = trade.ExitPrice,
        StopLoss = trade.StopLoss,
        TakeProfit = trade.TakeProfit,
        GrossPnl = trade.GrossPnl,
        Fees = trade.Fees,
        SlippageCost = trade.SlippageCost,
        FundingCost = trade.FundingCost,
        NetPnl = trade.NetPnl,
        RMultiple = trade.RMultiple,
        ExitReason = trade.ExitReason
    };

    private async Task<IReadOnlyList<Candle>> FetchHistoricalCandlesAsync(
        string symbol,
        string interval,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        if (!_backtestOptions.CandleCacheEnabled)
        {
            return await FetchHistoricalCandlesFromBybitAsync(symbol, interval, start, end, cancellationToken);
        }

        return await FetchHistoricalCandlesWithCacheAsync(symbol, interval, start, end, cancellationToken);
    }

    private async Task<IReadOnlyList<Candle>> FetchHistoricalCandlesWithCacheAsync(
        string symbol,
        string interval,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var intervalMinutes = ResolveIntervalMinutes(interval);
        var cacheableEnd = end.AddMinutes(-intervalMinutes);
        var cachePath = BuildCandleCachePath(symbol, interval);
        var cached = await ReadCandleCacheAsync(cachePath, cancellationToken);
        var additions = new List<Candle>();

        if (cached.Count == 0)
        {
            additions.AddRange(await FetchHistoricalCandlesFromBybitAsync(symbol, interval, start, end, cancellationToken));
        }
        else
        {
            var ordered = cached.OrderBy(candle => candle.OpenTime).ToArray();
            var cachedStart = ordered[0].OpenTime;
            var cachedEnd = ordered[^1].OpenTime;
            if (cachedStart > start.AddMinutes(intervalMinutes))
            {
                additions.AddRange(await FetchHistoricalCandlesFromBybitAsync(
                    symbol,
                    interval,
                    start,
                    cachedStart.AddMilliseconds(-1),
                    cancellationToken));
            }

            if (cachedEnd < cacheableEnd.AddMinutes(-intervalMinutes))
            {
                additions.AddRange(await FetchHistoricalCandlesFromBybitAsync(
                    symbol,
                    interval,
                    cachedEnd.AddMinutes(intervalMinutes),
                    end,
                    cancellationToken));
            }
        }

        if (additions.Count > 0)
        {
            cached = await MergeAndWriteCandleCacheAsync(cachePath, cached, additions, intervalMinutes, cancellationToken);
        }

        return cached
            .Where(candle => candle.OpenTime >= start && candle.OpenTime <= end)
            .DistinctBy(candle => candle.OpenTime)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
    }

    private async Task<IReadOnlyList<Candle>> FetchHistoricalCandlesFromBybitAsync(
        string symbol,
        string interval,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var result = new List<Candle>();
        var cursorEnd = end;
        while (cursorEnd > start)
        {
            var batch = await _bybitRestClient.GetKlinesAsync(
                Category,
                symbol,
                interval,
                start,
                cursorEnd,
                KlinePageLimit,
                cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            result.AddRange(batch.Where(candle => candle.OpenTime >= start && candle.OpenTime <= end));
            var oldest = batch.Min(candle => candle.OpenTime);
            if (oldest <= start || batch.Count < KlinePageLimit)
            {
                break;
            }

            cursorEnd = oldest.AddMilliseconds(-1);
        }

        return result
            .DistinctBy(candle => candle.OpenTime)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
    }

    private async Task<IReadOnlyList<Candle>> ReadCandleCacheAsync(string path, CancellationToken cancellationToken)
    {
        await _candleCacheLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<Candle>();
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<Candle>>(stream, _jsonOptions, cancellationToken) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to read backtest candle cache {Path}", path);
            return Array.Empty<Candle>();
        }
        finally
        {
            _candleCacheLock.Release();
        }
    }

    private async Task<IReadOnlyList<Candle>> MergeAndWriteCandleCacheAsync(
        string path,
        IReadOnlyList<Candle> cached,
        IReadOnlyList<Candle> additions,
        int intervalMinutes,
        CancellationToken cancellationToken)
    {
        await _candleCacheLock.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<Candle> disk = File.Exists(path)
                ? await ReadCandleCacheFileWithoutLockAsync(path, cancellationToken)
                : Array.Empty<Candle>();
            var cacheableBefore = DateTimeOffset.UtcNow.AddMinutes(-intervalMinutes);
            var merged = disk
                .Concat(cached)
                .Concat(additions)
                .Where(candle => candle.OpenTime.AddMinutes(intervalMinutes) <= cacheableBefore)
                .DistinctBy(candle => candle.OpenTime)
                .OrderBy(candle => candle.OpenTime)
                .ToArray();

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, merged, _jsonOptions, cancellationToken);
            return merged
                .Concat(additions)
                .DistinctBy(candle => candle.OpenTime)
                .OrderBy(candle => candle.OpenTime)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to write backtest candle cache {Path}", path);
            return cached.Concat(additions)
                .DistinctBy(candle => candle.OpenTime)
                .OrderBy(candle => candle.OpenTime)
                .ToArray();
        }
        finally
        {
            _candleCacheLock.Release();
        }
    }

    private async Task<IReadOnlyList<Candle>> ReadCandleCacheFileWithoutLockAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<Candle>>(stream, _jsonOptions, cancellationToken) ?? [];
    }

    private string BuildCandleCachePath(string symbol, string interval)
    {
        var key = $"{Category}:{symbol.ToUpperInvariant()}:{interval}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..16];
        var fileName = $"{SanitizeFileName(symbol)}_{SanitizeFileName(interval)}_{hash}.json";
        return Path.Combine(_backtestOptions.CandleCachePath, Category, fileName);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static int ResolveIntervalMinutes(string interval) =>
        int.TryParse(interval, out var minutes) && minutes > 0 ? minutes : interval switch
        {
            "D" => 1440,
            "W" => 10080,
            "M" => 43200,
            _ => 60
        };

    private BacktestRunSettings ResolveSettings(FuturesBacktestRequest request) => new(
        Math.Clamp(request.Days ?? _backtestOptions.Days, 1, 365),
        Math.Clamp(request.Symbols ?? _backtestOptions.Symbols, 1, 200),
        Math.Clamp(_backtestOptions.MaxConcurrency, 1, 20),
        ResolveBacktestMode(request.Mode, _backtestOptions.Mode),
        request.EntryNotionalUsdt ?? _backtestOptions.EntryNotionalUsdt,
        request.TakerFeePercent ?? _backtestOptions.TakerFeePercent,
        request.MakerFeePercent ?? _backtestOptions.MakerFeePercent,
        request.SlippagePercent ?? _backtestOptions.SlippagePercent,
        request.FundingPercentPer8h ?? _backtestOptions.FundingPercentPer8h,
        _backtestOptions.InitialEquityUsdt);

    private static FuturesBacktestMode ResolveBacktestMode(string? requestMode, string optionsMode)
    {
        var value = string.IsNullOrWhiteSpace(requestMode) ? optionsMode : requestMode;
        return Enum.TryParse<FuturesBacktestMode>(value, ignoreCase: true, out var mode)
            ? mode
            : FuturesBacktestMode.ScoreBasedRouter;
    }

    private void SetStatus(string message, decimal progressPercent)
    {
        lock (_sync)
        {
            var roundedProgress = decimal.Round(Math.Clamp(progressPercent, 0m, 100m), 2);
            _status = new FuturesBacktestStatusResponse
            {
                IsRunning = true,
                Status = message,
                ProgressPercent = roundedProgress,
                StartedAt = _status.StartedAt,
                EstimatedCompletedAt = EstimateCompletion(_status.StartedAt, roundedProgress),
                Result = _status.Result
            };
        }
    }

    private static DateTimeOffset? EstimateCompletion(DateTimeOffset? startedAt, decimal progressPercent)
    {
        if (startedAt is null || progressPercent <= 1m || progressPercent >= 100m)
        {
            return null;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt.Value;
        if (elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var totalSeconds = elapsed.TotalSeconds / ((double)progressPercent / 100d);
        var remaining = TimeSpan.FromSeconds(Math.Max(0d, totalSeconds - elapsed.TotalSeconds));
        return DateTimeOffset.UtcNow.Add(remaining);
    }

    private static bool IsTradable(BybitInstrumentInfo instrument) =>
        string.Equals(instrument.Status, "Trading", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(instrument.QuoteCoin, "USDT", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(instrument.ContractType, "LinearPerpetual", StringComparison.OrdinalIgnoreCase) &&
        instrument.MinOrderQty > 0m;

    private static decimal ApplySlippage(decimal price, bool isShort, bool isEntry, decimal slippagePercent)
    {
        var multiplier = slippagePercent / 100m;
        var adverseUp = isEntry ? !isShort : isShort;
        return adverseUp ? price * (1m + multiplier) : price * (1m - multiplier);
    }

    private static decimal CalculateReclaimPercent(string side, decimal boundary, decimal close)
    {
        if (boundary <= 0m)
        {
            return 0m;
        }

        var distance = side == "Short" ? boundary - close : close - boundary;
        return decimal.Max(0m, distance / boundary * 100m);
    }

    private static decimal CalculateSweepDepthPercent(string side, decimal boundary, decimal sweepExtreme)
    {
        if (boundary <= 0m)
        {
            return 0m;
        }

        var distance = side == "Short"
            ? sweepExtreme - boundary
            : boundary - sweepExtreme;
        return decimal.Max(0m, distance / boundary * 100m);
    }

    private static decimal CalculateMidlineRoomR(string side, decimal rangeHigh, decimal rangeLow, decimal entryPrice, decimal risk)
    {
        if (risk <= 0m || rangeHigh <= rangeLow)
        {
            return 0m;
        }

        var midline = (rangeHigh + rangeLow) / 2m;
        var room = side == "Short"
            ? entryPrice - midline
            : midline - entryPrice;
        return decimal.Max(0m, room / risk);
    }

    private static bool IsInsideRange(decimal price, decimal upperBoundary, decimal lowerBoundary) =>
        price > lowerBoundary && price < upperBoundary;

    private static decimal CalculateBreakoutVolumeRatio(IReadOnlyList<Candle> candles, DateTimeOffset breakoutOpenTime)
    {
        var ordered = EnsureOrdered(candles);
        var index = Array.FindIndex(ordered, candle => candle.OpenTime == breakoutOpenTime);
        if (index < 0)
        {
            return 1m;
        }

        var start = Math.Max(0, index - 20);
        var count = index - start;
        if (count <= 0)
        {
            return 1m;
        }

        var sum = 0m;
        for (var i = start; i < index; i++)
        {
            sum += ordered[i].Volume;
        }

        var average = sum / count;
        return average > 0m ? ordered[index].Volume / average : 1m;
    }

    private static decimal CalculateAdx(IReadOnlyList<Candle> candles, int period)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < period * 2 + 1)
        {
            return 0m;
        }

        var trueRanges = new List<decimal>();
        var plusDm = new List<decimal>();
        var minusDm = new List<decimal>();
        for (var i = 1; i < ordered.Length; i++)
        {
            var current = ordered[i];
            var previous = ordered[i - 1];
            var upMove = current.High - previous.High;
            var downMove = previous.Low - current.Low;
            plusDm.Add(upMove > downMove && upMove > 0m ? upMove : 0m);
            minusDm.Add(downMove > upMove && downMove > 0m ? downMove : 0m);
            trueRanges.Add(decimal.Max(current.High - current.Low, decimal.Max(
                Math.Abs(current.High - previous.Close),
                Math.Abs(current.Low - previous.Close))));
        }

        var dxValues = new List<decimal>();
        for (var i = period - 1; i < trueRanges.Count; i++)
        {
            var tr = trueRanges.Skip(i - period + 1).Take(period).Sum();
            if (tr <= 0m)
            {
                continue;
            }

            var plusDi = plusDm.Skip(i - period + 1).Take(period).Sum() / tr * 100m;
            var minusDi = minusDm.Skip(i - period + 1).Take(period).Sum() / tr * 100m;
            var denominator = plusDi + minusDi;
            if (denominator <= 0m)
            {
                continue;
            }

            dxValues.Add(Math.Abs(plusDi - minusDi) / denominator * 100m);
        }

        return dxValues.Count == 0 ? 0m : dxValues.TakeLast(period).Average();
    }

    private static TimeZoneInfo ResolveNewYorkTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    private static Candle[] CopyPrefix(IReadOnlyList<Candle> candles, int count)
    {
        count = Math.Clamp(count, 0, candles.Count);
        var result = new Candle[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = candles[i];
        }

        return result;
    }

    private static Candle[] CopyWindow(
        IReadOnlyList<Candle> candles,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive,
        Candle fallback)
    {
        var result = new List<Candle>();
        for (var i = 0; i < candles.Count; i++)
        {
            var openTime = candles[i].OpenTime;
            if (openTime >= startInclusive && openTime < endExclusive)
            {
                result.Add(candles[i]);
            }
        }

        return result.Count > 0 ? result.ToArray() : [fallback];
    }

    private static decimal MaxHigh(IReadOnlyList<Candle> candles)
    {
        var value = candles.Count > 0 ? candles[0].High : 0m;
        for (var i = 1; i < candles.Count; i++)
        {
            value = decimal.Max(value, candles[i].High);
        }

        return value;
    }

    private static decimal MaxHigh(IReadOnlyList<Candle> candles, int inclusiveEnd)
    {
        if (candles.Count == 0)
        {
            return 0m;
        }

        inclusiveEnd = Math.Clamp(inclusiveEnd, 0, candles.Count - 1);
        var value = candles[0].High;
        for (var i = 1; i <= inclusiveEnd; i++)
        {
            value = decimal.Max(value, candles[i].High);
        }

        return value;
    }

    private static decimal MinLow(IReadOnlyList<Candle> candles)
    {
        var value = candles.Count > 0 ? candles[0].Low : 0m;
        for (var i = 1; i < candles.Count; i++)
        {
            value = decimal.Min(value, candles[i].Low);
        }

        return value;
    }

    private static decimal MinLow(IReadOnlyList<Candle> candles, int inclusiveEnd)
    {
        if (candles.Count == 0)
        {
            return 0m;
        }

        inclusiveEnd = Math.Clamp(inclusiveEnd, 0, candles.Count - 1);
        var value = candles[0].Low;
        for (var i = 1; i <= inclusiveEnd; i++)
        {
            value = decimal.Min(value, candles[i].Low);
        }

        return value;
    }

    private static int FindFirstOpenTimeAfter(IReadOnlyList<Candle> candles, DateTimeOffset openTime)
    {
        var low = 0;
        var high = candles.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (candles[mid].OpenTime <= openTime)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low < candles.Count ? low : -1;
    }

    private static Candle[] EnsureOrdered(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 2)
        {
            return candles.ToArray();
        }

        for (var i = 1; i < candles.Count; i++)
        {
            if (candles[i - 1].OpenTime > candles[i].OpenTime)
            {
                return candles.OrderBy(candle => candle.OpenTime).ToArray();
            }
        }

        return candles as Candle[] ?? candles.ToArray();
    }

    private sealed record BacktestRunSettings(
        int Days,
        int Symbols,
        int MaxConcurrency,
        FuturesBacktestMode Mode,
        decimal EntryNotionalUsdt,
        decimal TakerFeePercent,
        decimal MakerFeePercent,
        decimal SlippagePercent,
        decimal FundingPercentPer8h,
        decimal InitialEquityUsdt);

    private sealed class BacktestCandleSeries
    {
        private BacktestCandleSeries(Candle[] candles, int intervalMinutes)
        {
            Candles = candles;
            IntervalMinutes = intervalMinutes;
        }

        public Candle[] Candles { get; }

        public int IntervalMinutes { get; }

        public int Count => Candles.Length;

        public static BacktestCandleSeries Create(IReadOnlyList<Candle> candles, int intervalMinutes) =>
            new(EnsureOrdered(candles), intervalMinutes);

        public Candle[] CopyClosedUntil(DateTimeOffset closedAt) =>
            CopyClosedUntil(closedAt, IntervalMinutes);

        public Candle[] CopyFirst(int count)
        {
            count = Math.Clamp(count, 0, Candles.Length);
            var result = new Candle[count];
            Array.Copy(Candles, result, count);
            return result;
        }

        public Candle[] CopyClosedUntil(DateTimeOffset closedAt, int intervalMinutes)
        {
            var count = CountClosedUntil(closedAt, intervalMinutes);
            return CopyFirst(count);
        }

        public Candle[] CopyWindow(DateTimeOffset startInclusive, DateTimeOffset endExclusive, Candle fallback)
        {
            var start = LowerBoundOpenTime(startInclusive);
            var end = LowerBoundOpenTime(endExclusive);
            var count = Math.Max(0, end - start);
            if (count == 0)
            {
                return [fallback];
            }

            var result = new Candle[count];
            Array.Copy(Candles, start, result, 0, count);
            return result;
        }

        public int CountClosedUntil(DateTimeOffset closedAt, int intervalMinutes)
        {
            var low = 0;
            var high = Candles.Length;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (Candles[mid].OpenTime.AddMinutes(intervalMinutes) <= closedAt)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }

        private int LowerBoundOpenTime(DateTimeOffset openTime)
        {
            var low = 0;
            var high = Candles.Length;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (Candles[mid].OpenTime < openTime)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }
    }

    private sealed record PrecomputedTurtleSignal(string System, StrategySide Side, decimal BreakoutLevel);

    private sealed class PrecomputedTurtleIndicators
    {
        private PrecomputedTurtleIndicators(
            decimal[] entryFastHigh,
            decimal[] entryFastLow,
            decimal[] entrySlowHigh,
            decimal[] entrySlowLow,
            decimal[] exitFastHigh,
            decimal[] exitFastLow,
            decimal[] exitSlowHigh,
            decimal[] exitSlowLow,
            decimal[] turtleN)
        {
            EntryFastHigh = entryFastHigh;
            EntryFastLow = entryFastLow;
            EntrySlowHigh = entrySlowHigh;
            EntrySlowLow = entrySlowLow;
            ExitFastHigh = exitFastHigh;
            ExitFastLow = exitFastLow;
            ExitSlowHigh = exitSlowHigh;
            ExitSlowLow = exitSlowLow;
            TurtleN = turtleN;
        }

        public decimal[] EntryFastHigh { get; }

        public decimal[] EntryFastLow { get; }

        public decimal[] EntrySlowHigh { get; }

        public decimal[] EntrySlowLow { get; }

        public decimal[] ExitFastHigh { get; }

        public decimal[] ExitFastLow { get; }

        public decimal[] ExitSlowHigh { get; }

        public decimal[] ExitSlowLow { get; }

        public decimal[] TurtleN { get; }

        public static PrecomputedTurtleIndicators Build(
            IReadOnlyList<Candle> candles,
            TurtleTrendOptions options,
            CancellationToken cancellationToken)
        {
            return new PrecomputedTurtleIndicators(
                ComputeDonchianHigh(candles, options.EntryFastPeriod, cancellationToken: cancellationToken),
                ComputeDonchianLow(candles, options.EntryFastPeriod, cancellationToken: cancellationToken),
                ComputeDonchianHigh(candles, options.EntrySlowPeriod, cancellationToken: cancellationToken),
                ComputeDonchianLow(candles, options.EntrySlowPeriod, cancellationToken: cancellationToken),
                ComputeDonchianHigh(candles, options.ExitFastPeriod, cancellationToken: cancellationToken),
                ComputeDonchianLow(candles, options.ExitFastPeriod, cancellationToken: cancellationToken),
                ComputeDonchianHigh(candles, options.ExitSlowPeriod, cancellationToken: cancellationToken),
                ComputeDonchianLow(candles, options.ExitSlowPeriod, cancellationToken: cancellationToken),
                ComputeTurtleN(candles, options.AtrPeriod, cancellationToken));
        }
    }

    private sealed class PrecomputedTurtleChannelExits
    {
        private PrecomputedTurtleChannelExits(
            decimal[] fastHigh,
            decimal[] fastLow,
            decimal[] slowHigh,
            decimal[] slowLow)
        {
            FastHigh = fastHigh;
            FastLow = fastLow;
            SlowHigh = slowHigh;
            SlowLow = slowLow;
        }

        private decimal[] FastHigh { get; }

        private decimal[] FastLow { get; }

        private decimal[] SlowHigh { get; }

        private decimal[] SlowLow { get; }

        public static PrecomputedTurtleChannelExits Build(
            IReadOnlyList<Candle> fiveMinuteCandles,
            TurtleTrendOptions options,
            CancellationToken cancellationToken)
        {
            var turtleInterval = ParseIntervalMinutes(options.Timeframe, 60);
            var fastBars = Math.Max(options.ExitFastPeriod, options.ExitFastPeriod * turtleInterval / 5);
            var slowBars = Math.Max(options.ExitSlowPeriod, options.ExitSlowPeriod * turtleInterval / 5);
            return new PrecomputedTurtleChannelExits(
                ComputeDonchianHigh(fiveMinuteCandles, fastBars, requireFullWarmup: true, cancellationToken: cancellationToken),
                ComputeDonchianLow(fiveMinuteCandles, fastBars, requireFullWarmup: true, cancellationToken: cancellationToken),
                ComputeDonchianHigh(fiveMinuteCandles, slowBars, requireFullWarmup: true, cancellationToken: cancellationToken),
                ComputeDonchianLow(fiveMinuteCandles, slowBars, requireFullWarmup: true, cancellationToken: cancellationToken));
        }

        public decimal GetHigh(int index, bool slow) =>
            slow ? SlowHigh[index] : FastHigh[index];

        public decimal GetLow(int index, bool slow) =>
            slow ? SlowLow[index] : FastLow[index];
    }

    private static decimal[] ComputeDonchianHigh(
        IReadOnlyList<Candle> candles,
        int period,
        bool requireFullWarmup = false,
        CancellationToken cancellationToken = default)
    {
        var result = new decimal[candles.Count];
        if (period <= 0)
        {
            return result;
        }

        for (var index = 0; index < candles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == 0 || requireFullWarmup && index + 1 < period + 2)
            {
                continue;
            }

            var start = Math.Max(0, index - period);
            var high = candles[start].High;
            for (var i = start + 1; i < index; i++)
            {
                high = decimal.Max(high, candles[i].High);
            }

            result[index] = high;
        }

        return result;
    }

    private static decimal[] ComputeDonchianLow(
        IReadOnlyList<Candle> candles,
        int period,
        bool requireFullWarmup = false,
        CancellationToken cancellationToken = default)
    {
        var result = new decimal[candles.Count];
        if (period <= 0)
        {
            return result;
        }

        for (var index = 0; index < candles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == 0 || requireFullWarmup && index + 1 < period + 2)
            {
                continue;
            }

            var start = Math.Max(0, index - period);
            var low = candles[start].Low;
            for (var i = start + 1; i < index; i++)
            {
                low = decimal.Min(low, candles[i].Low);
            }

            result[index] = low;
        }

        return result;
    }

    private static decimal[] ComputeTurtleN(
        IReadOnlyList<Candle> candles,
        int period,
        CancellationToken cancellationToken)
    {
        var result = new decimal[candles.Count];
        if (period <= 0 || candles.Count < period + 1)
        {
            return result;
        }

        var trueRanges = new decimal[candles.Count];
        for (var index = 1; index < candles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = candles[index];
            var previous = candles[index - 1];
            trueRanges[index] = decimal.Max(
                current.High - current.Low,
                decimal.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
        }

        var sum = 0m;
        for (var index = 1; index <= period; index++)
        {
            sum += trueRanges[index];
        }

        var n = sum / period;
        result[period] = n;
        for (var index = period + 1; index < candles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            n = ((period - 1m) * n + trueRanges[index]) / period;
            result[index] = n;
        }

        return result;
    }

    private sealed record SymbolBacktestOutput(
        string Symbol,
        IReadOnlyList<BacktestTradeInternal> Trades,
        int FalseBreakoutCount,
        int TrueBreakoutBlockedCount);

    private sealed record BacktestFilterResult(bool IsAllowed, bool IsTrueBreakoutBlocked);

    private sealed record StrategyGatePerformance(
        string StrategyName,
        string Symbol,
        string Direction,
        int TradesCount,
        decimal NetPnl,
        decimal ProfitFactor,
        decimal AverageR);

    private sealed record BacktestTradeInternal(
        string Symbol,
        string Side,
        string Pattern,
        DateTimeOffset EntryTime,
        DateTimeOffset ExitTime,
        decimal EntryPrice,
        decimal ExitPrice,
        decimal StopLoss,
        decimal TakeProfit,
        decimal GrossPnl,
        decimal Fees,
        decimal SlippageCost,
        decimal FundingCost,
        decimal NetPnl,
        decimal RMultiple,
        string ExitReason,
        int ExitIndex);
}
