using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public interface IFuturesBacktestService
{
    FuturesBacktestStatusResponse GetStatus();

    Task<FuturesBacktestStatusResponse> StartAsync(FuturesBacktestRequest request, CancellationToken cancellationToken);

    FuturesBacktestStatusResponse Stop();

    bool IsSymbolAllowedForTrading(string symbol, bool requireCompletedBacktest);
}

public sealed class FuturesBacktestService : IFuturesBacktestService
{
    private const string Category = "linear";
    private const string FiveMinuteInterval = "5";
    private const string FifteenMinuteInterval = "15";
    private const string FourHourInterval = "240";
    private const int KlinePageLimit = 1000;
    private const int MaxConcurrency = 2;

    private readonly IBybitRestClient _bybitRestClient;
    private readonly FuturesBacktestOptions _backtestOptions;
    private readonly ILogger<FuturesBacktestService> _logger;
    private readonly NySessionBreakoutOptions _strategyOptions;
    private readonly object _sync = new();
    private CancellationTokenSource? _runCancellation;
    private FuturesBacktestStatusResponse _status = new();

    public FuturesBacktestService(
        IBybitRestClient bybitRestClient,
        IOptions<FuturesBacktestOptions> backtestOptions,
        IOptions<NySessionBreakoutOptions> strategyOptions,
        ILogger<FuturesBacktestService> logger)
    {
        _bybitRestClient = bybitRestClient;
        _backtestOptions = backtestOptions.Value;
        _strategyOptions = strategyOptions.Value;
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
                Status = "Starting 4H NY sweep/engulfing backtest",
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
            var eligible = _status.Result?.EligibleSymbols;
            if (eligible is null || eligible.Count == 0)
            {
                return !requireCompletedBacktest;
            }

            return eligible.Contains(symbol, StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task RunBacktestAsync(FuturesBacktestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var settings = ResolveSettings(request);
            var periodEnd = DateTimeOffset.UtcNow;
            var periodStart = periodEnd.AddDays(-settings.Days);
            SetStatus($"Loading top {settings.Symbols} Bybit USDT perpetual symbols", 2m);

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

            var btc15m = await FetchHistoricalCandlesAsync("BTCUSDT", FifteenMinuteInterval, periodStart, periodEnd, cancellationToken);
            var allTrades = new List<BacktestTradeInternal>();
            var falseBreakoutCount = 0;
            var trueBreakoutBlockedCount = 0;
            var processed = 0;
            using var throttler = new SemaphoreSlim(MaxConcurrency);
            var tasks = symbols.Select(async symbol =>
            {
                await throttler.WaitAsync(cancellationToken);
                try
                {
                    return await BacktestSymbolAsync(symbol, periodStart, periodEnd, btc15m, settings, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Backtest failed for {Symbol}", symbol);
                    return new SymbolBacktestOutput(symbol, [], 0, 0);
                }
                finally
                {
                    var done = Interlocked.Increment(ref processed);
                    SetStatus($"Processed {done}/{symbols.Length} symbols", 5m + 90m * done / Math.Max(1, symbols.Length));
                    throttler.Release();
                }
            });

            foreach (var output in await Task.WhenAll(tasks))
            {
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
        IReadOnlyList<Candle> btc15m,
        BacktestRunSettings settings,
        CancellationToken cancellationToken)
    {
        var fiveMinuteCandles = await FetchHistoricalCandlesAsync(symbol, FiveMinuteInterval, periodStart, periodEnd, cancellationToken);
        var fifteenMinuteCandles = await FetchHistoricalCandlesAsync(symbol, FifteenMinuteInterval, periodStart, periodEnd, cancellationToken);
        _ = await FetchHistoricalCandlesAsync(symbol, FourHourInterval, periodStart, periodEnd, cancellationToken);
        if (fiveMinuteCandles.Count < 500 || fifteenMinuteCandles.Count < 200)
        {
            return new SymbolBacktestOutput(symbol, [], 0, 0);
        }

        var nyZone = ResolveNewYorkTimeZone();
        var trades = new List<BacktestTradeInternal>();
        var falseBreakoutCount = 0;
        var trueBreakoutBlockedCount = 0;
        var groupedByDay = fiveMinuteCandles
            .GroupBy(candle => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(candle.OpenTime, nyZone).Date))
            .OrderBy(group => group.Key);

        foreach (var day in groupedByDay)
        {
            var session = day
                .Where(candle =>
                {
                    var ny = TimeZoneInfo.ConvertTime(candle.OpenTime, nyZone);
                    return ny.TimeOfDay >= TimeSpan.FromHours(8) && ny.TimeOfDay < TimeSpan.FromHours(16);
                })
                .OrderBy(candle => candle.OpenTime)
                .ToArray();
            if (session.Length < 12)
            {
                continue;
            }

            BacktestDay(symbol, session, fifteenMinuteCandles, btc15m, settings, nyZone, trades, ref falseBreakoutCount, ref trueBreakoutBlockedCount);
        }

        return new SymbolBacktestOutput(symbol, trades, falseBreakoutCount, trueBreakoutBlockedCount);
    }

    private void BacktestDay(
        string symbol,
        IReadOnlyList<Candle> session,
        IReadOnlyList<Candle> fifteenMinuteCandles,
        IReadOnlyList<Candle> btc15m,
        BacktestRunSettings settings,
        TimeZoneInfo nyZone,
        List<BacktestTradeInternal> trades,
        ref int falseBreakoutCount,
        ref int trueBreakoutBlockedCount)
    {
        var upperBoundary = session[0].High;
        var lowerBoundary = session[0].Low;
        decimal? upperStop = null;
        DateTimeOffset? upperSweepAt = null;
        decimal? upperReturnLevel = null;
        decimal? lowerStop = null;
        DateTimeOffset? lowerSweepAt = null;
        decimal? lowerReturnLevel = null;

        for (var i = 1; i < session.Count; i++)
        {
            var candle = session[i];
            var ny = TimeZoneInfo.ConvertTime(candle.OpenTime, nyZone);
            var isRangeBuilding = ny.TimeOfDay < TimeSpan.FromHours(12);
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
                if (!string.Equals(signal.Pattern, "Engulfing", StringComparison.OrdinalIgnoreCase))
                {
                    falseBreakoutCount++;
                }

                var filter = EvaluateBacktestFilters(signal, session.Take(i + 1).ToArray(), fifteenMinuteCandles, btc15m);
                if (filter.IsTrueBreakoutBlocked)
                {
                    trueBreakoutBlockedCount++;
                }

                if (filter.IsAllowed)
                {
                    var trade = SimulateTrade(symbol, signal, session, i + 1, settings);
                    trades.Add(trade);
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
                    session.Take(index + 1).ToArray());
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
                    session.Take(index + 1).ToArray());
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
                    session.Take(index + 1).ToArray());
            }
        }

        return TryFindEngulfingSignal(session.Take(index + 1).ToArray(), upperBoundary, lowerBoundary);
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
        decimal bodyRatio = 0m)
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

        if (signal.SweepDepthPercent < _strategyOptions.MinSweepDepthPercent)
        {
            return new BacktestFilterResult(false, false);
        }

        if (signal.ReclaimPercent < _strategyOptions.MinReclaimPercent)
        {
            return new BacktestFilterResult(false, false);
        }

        if (signal.StopDistancePercent > _strategyOptions.MaxStopPercent)
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

    private BacktestFilterResult EvaluateEngulfingBacktestFilters(
        NySessionSignal signal,
        IReadOnlyList<Candle> btc15m)
    {
        if (!_strategyOptions.EngulfingEnabled ||
            signal.BodyRatio < _strategyOptions.MinEngulfingBodyRatio ||
            signal.StopDistancePercent > _strategyOptions.MaxStopPercent)
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
        int startIndex,
        BacktestRunSettings settings)
    {
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

                if (candle.Low <= signal.TakeProfit)
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

                if (candle.High >= signal.TakeProfit)
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

        var optimizationTrades = trades
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
        var outOfSampleTrades = trades
            .Where(trade => trade.EntryTime >= splitAt)
            .Where(trade => eligibleSet.Contains(trade.Symbol))
            .OrderBy(trade => trade.EntryTime)
            .ToArray();
        var tradedSymbols = trades
            .Select(trade => trade.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var excludedSymbols = tradedSymbols
            .Where(symbol => !eligibleSet.Contains(symbol))
            .OrderBy(symbol => symbol)
            .ToArray();
        var publicTrades = outOfSampleTrades.Select(ToPublicTrade).ToArray();
        return new FuturesBacktestResult
        {
            StrategyName = "NY 08:00 4H Sweep Reversal + Engulfing",
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            SymbolsRequested = symbolsRequested,
            SymbolsProcessed = symbolsProcessed,
            TradesCount = outOfSampleTrades.Length,
            FalseBreakoutCount = falseBreakoutCount,
            TrueBreakoutBlockedCount = trueBreakoutBlockedCount,
            Metrics = BuildMetrics(outOfSampleTrades, splitAt, periodEnd, settings.InitialEquityUsdt),
            OptimizationMetrics = BuildMetrics(optimizationTrades, periodStart, splitAt, settings.InitialEquityUsdt),
            OutOfSampleMetrics = BuildMetrics(outOfSampleTrades, splitAt, periodEnd, settings.InitialEquityUsdt),
            EligibleSymbols = eligibleSymbols,
            ExcludedSymbols = excludedSymbols,
            BestSymbols = BuildSymbolPerformance(outOfSampleTrades).OrderByDescending(item => item.NetPnl).Take(10).ToArray(),
            WorstSymbols = BuildSymbolPerformance(outOfSampleTrades).OrderBy(item => item.NetPnl).Take(10).ToArray(),
            LongShort = BuildSidePerformance(outOfSampleTrades),
            WeekdayPerformance = BuildBucketPerformance(outOfSampleTrades, trade => trade.EntryTime.DayOfWeek.ToString()),
            HourPerformance = BuildBucketPerformance(outOfSampleTrades, trade => TimeZoneInfo.ConvertTime(trade.EntryTime, ResolveNewYorkTimeZone()).Hour.ToString("00")),
            RecentTrades = publicTrades.OrderByDescending(trade => trade.EntryTime).Take(100).ToArray()
        };
    }

    private static FuturesBacktestMetrics BuildMetrics(
        IReadOnlyList<BacktestTradeInternal> trades,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        decimal initialEquity)
    {
        var netPnl = trades.Sum(trade => trade.NetPnl);
        var wins = trades.Count(trade => trade.NetPnl > 0m);
        var grossProfit = trades.Where(trade => trade.NetPnl > 0m).Sum(trade => trade.NetPnl);
        var grossLoss = Math.Abs(trades.Where(trade => trade.NetPnl < 0m).Sum(trade => trade.NetPnl));
        var days = decimal.Max(1m, (decimal)(periodEnd - periodStart).TotalDays);
        var maxDrawdown = CalculateMaxDrawdown(trades, initialEquity);
        return new FuturesBacktestMetrics
        {
            NetPnl = netPnl,
            MaxDrawdown = maxDrawdown,
            MaxDrawdownPercent = initialEquity > 0m ? maxDrawdown / initialEquity * 100m : 0m,
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
        Side = trade.Side,
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

    private BacktestRunSettings ResolveSettings(FuturesBacktestRequest request) => new(
        Math.Clamp(request.Days ?? _backtestOptions.Days, 1, 365),
        Math.Clamp(request.Symbols ?? _backtestOptions.Symbols, 1, 200),
        request.EntryNotionalUsdt ?? _backtestOptions.EntryNotionalUsdt,
        request.TakerFeePercent ?? _backtestOptions.TakerFeePercent,
        request.MakerFeePercent ?? _backtestOptions.MakerFeePercent,
        request.SlippagePercent ?? _backtestOptions.SlippagePercent,
        request.FundingPercentPer8h ?? _backtestOptions.FundingPercentPer8h,
        _backtestOptions.InitialEquityUsdt);

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
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var index = Array.FindIndex(ordered, candle => candle.OpenTime == breakoutOpenTime);
        if (index < 0)
        {
            return 1m;
        }

        var previous = ordered.Take(index).TakeLast(20).ToArray();
        if (previous.Length == 0)
        {
            return 1m;
        }

        var average = previous.Average(candle => candle.Volume);
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

    private sealed record BacktestRunSettings(
        int Days,
        int Symbols,
        decimal EntryNotionalUsdt,
        decimal TakerFeePercent,
        decimal MakerFeePercent,
        decimal SlippagePercent,
        decimal FundingPercentPer8h,
        decimal InitialEquityUsdt);

    private sealed record SymbolBacktestOutput(
        string Symbol,
        IReadOnlyList<BacktestTradeInternal> Trades,
        int FalseBreakoutCount,
        int TrueBreakoutBlockedCount);

    private sealed record BacktestFilterResult(bool IsAllowed, bool IsTrueBreakoutBlocked);

    private sealed record BacktestTradeInternal(
        string Symbol,
        string Side,
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
