using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Notifications;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public interface INySessionBreakoutRuntime
{
    Task<NySessionDashboardResponse> GetDashboardAsync(CancellationToken cancellationToken);

    Task<UpdateSettingsResponse> ReplacePoolSymbolAsync(NySessionPoolReplaceRequest request, CancellationToken cancellationToken);
}

public sealed class NySessionBreakoutWorker : BackgroundService, INySessionBreakoutRuntime
{
    private const string Category = "linear";
    private const string FiveMinuteInterval = "5";
    private const string FifteenMinuteInterval = "15";
    private const int FiveMinuteLookback = 360;
    private const int FifteenMinuteLookback = 96;
    private const int MaxConcurrency = 6;

    private readonly AppOptions _appOptions;
    private readonly IFuturesBacktestService _backtestService;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly FuturesExecutionService _executionService;
    private readonly FuturesOptions _futuresOptions;
    private readonly IGridRepository _repository;
    private readonly ILogger<NySessionBreakoutWorker> _logger;
    private readonly ITelegramNotifier _notifier;
    private readonly NySessionBreakoutOptions _options;
    private readonly object _sync = new();
    private readonly Dictionary<string, NySessionPoolItem> _pool = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _manualSlotSymbols = new();
    private readonly HashSet<string> _manuallyRemovedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastSignalBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<NySessionEventItem> _events = new();
    private string _status = "Starting";
    private DateTimeOffset? _lastScanAt;
    private DateTimeOffset? _sessionStart;
    private DateTimeOffset? _rangeStart;
    private DateTimeOffset? _rangeEnd;

    public NySessionBreakoutWorker(
        IOptions<AppOptions> appOptions,
        IOptions<FuturesOptions> futuresOptions,
        IOptions<NySessionBreakoutOptions> options,
        IFuturesBacktestService backtestService,
        IBybitRestClient bybitRestClient,
        FuturesExecutionService executionService,
        IGridRepository repository,
        ITelegramNotifier notifier,
        ILogger<NySessionBreakoutWorker> logger)
    {
        _appOptions = appOptions.Value;
        _backtestService = backtestService;
        _futuresOptions = futuresOptions.Value;
        _options = options.Value;
        _bybitRestClient = bybitRestClient;
        _executionService = executionService;
        _repository = repository;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<NySessionDashboardResponse> GetDashboardAsync(CancellationToken cancellationToken)
    {
        NySessionPoolItem[] pool;
        NySessionEventItem[] events;
        string status;
        DateTimeOffset? lastScanAt;
        DateTimeOffset? sessionStart;
        DateTimeOffset? rangeStart;
        DateTimeOffset? rangeEnd;
        lock (_sync)
        {
            pool = _pool.Values
                .OrderBy(item => item.Slot)
                .ToArray();
            events = _events.Reverse().Take(40).ToArray();
            status = _status;
            lastScanAt = _lastScanAt;
            sessionStart = _sessionStart;
            rangeStart = _rangeStart;
            rangeEnd = _rangeEnd;
        }

        var fills = await _repository.GetRecentFuturesFillsAsync(5000, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var totalPnl = fills.Sum(fill => fill.RealizedPnl + fill.Funding - fill.Fee);
        var dailyPnl = fills
            .Where(fill => DateOnly.FromDateTime(fill.CreatedAt.UtcDateTime) == today)
            .Sum(fill => fill.RealizedPnl + fill.Funding - fill.Fee);
        var openTrades = await BuildOpenTradesAsync(pool, cancellationToken);

        return new NySessionDashboardResponse
        {
            TradingMode = _appOptions.TradingMode.ToString(),
            FuturesEnabled = _futuresOptions.Enabled,
            StrategyEnabled = _options.Enabled,
            GeneratedAt = DateTimeOffset.UtcNow,
            LastScanAt = lastScanAt,
            NewYorkSessionStart = sessionStart,
            FourHourRangeStart = rangeStart,
            FourHourRangeEnd = rangeEnd,
            TotalPnl = totalPnl,
            DailyPnl = dailyPnl,
            UnrealizedPnl = openTrades.Sum(trade => trade.UnrealizedPnl),
            Status = status,
            Pool = pool,
            OpenTrades = openTrades,
            Events = events
        };
    }

    public async Task<UpdateSettingsResponse> ReplacePoolSymbolAsync(
        NySessionPoolReplaceRequest request,
        CancellationToken cancellationToken)
    {
        var slot = request.Slot;
        var currentSymbol = NormalizeSymbol(request.CurrentSymbol);
        var newSymbol = NormalizeSymbol(request.NewSymbol);
        if (slot <= 0 || string.IsNullOrWhiteSpace(currentSymbol) || string.IsNullOrWhiteSpace(newSymbol))
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = newSymbol,
                Message = "Pool pair was not replaced.",
                Errors = ["Slot, current symbol and new symbol are required."]
            };
        }

        if (string.Equals(currentSymbol, newSymbol, StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateSettingsResponse
            {
                Success = true,
                Symbol = newSymbol,
                Message = $"Pool slot {slot} already uses {newSymbol}."
            };
        }

        var instruments = await _bybitRestClient.GetInstrumentsAsync(Category, cancellationToken);
        var instrument = instruments.FirstOrDefault(item => string.Equals(item.Symbol, newSymbol, StringComparison.OrdinalIgnoreCase));
        if (instrument is null || !IsTradable(instrument))
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = newSymbol,
                Message = "Pool pair was not replaced.",
                Errors = [$"{newSymbol} is not a trading Bybit USDT linear perpetual."]
            };
        }

        var ticker = await _bybitRestClient.GetTickerAsync(Category, newSymbol, cancellationToken);
        var replacement = new NySessionPoolItem
        {
            Slot = slot,
            Symbol = newSymbol,
            LastPrice = ticker.LastPrice,
            State = "Manual replacement",
            Bias = "Waiting scan",
            Turnover24h = ticker.Turnover24h,
            Reason = "Manual pair replacement saved. The next worker cycle will apply the 4H NY strategy.",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        lock (_sync)
        {
            foreach (var pair in _manualSlotSymbols.Where(pair => string.Equals(pair.Value, newSymbol, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _manualSlotSymbols.Remove(pair.Key);
            }

            _manualSlotSymbols[slot] = newSymbol;
            _manuallyRemovedSymbols.Add(currentSymbol);
            _manuallyRemovedSymbols.Remove(newSymbol);
            _pool.Remove(currentSymbol);
            _pool[newSymbol] = replacement;
            _lastSignalBySymbol.Remove(currentSymbol);
            _lastSignalBySymbol.Remove(newSymbol);
        }

        AddEvent(newSymbol, "info", $"Pool slot {slot} changed from {currentSymbol} to {newSymbol}.");
        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = newSymbol,
            Message = $"Pool slot {slot} changed from {currentSymbol} to {newSymbol}."
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_futuresOptions.Enabled || !_options.Enabled)
        {
            SetStatus(!_futuresOptions.Enabled
                ? "Futures disabled. Set FUTURES_ENABLED=true."
                : "NY session strategy disabled. Set NY_SESSION_STRATEGY_ENABLED=true.");
            return;
        }

        ValidateTradingMode();
        AddEvent("SYSTEM", "info", $"NY session futures strategy started in {_appOptions.TradingMode} mode.");
        await _notifier.NotifyAsync($"NY session futures strategy started.\nMode: `{_appOptions.TradingMode}`", stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _options.LoopSeconds)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "NY session strategy loop failed.");
                SetStatus($"Loop error: {exception.Message}");
                AddEvent("SYSTEM", "error", exception.Message);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var anchors = ResolveSessionAnchors(now);
        SetAnchors(anchors);

        var instruments = await _bybitRestClient.GetInstrumentsAsync(Category, cancellationToken);
        var tradable = instruments
            .Where(IsTradable)
            .ToDictionary(instrument => instrument.Symbol, StringComparer.OrdinalIgnoreCase);
        var tickers = await _bybitRestClient.GetTickersAsync(Category, cancellationToken);
        string[] previousPoolSymbols;
        Dictionary<int, string> manualSlotSymbols;
        HashSet<string> manuallyRemovedSymbols;
        lock (_sync)
        {
            previousPoolSymbols = _pool.Keys.ToArray();
            manualSlotSymbols = new Dictionary<int, string>(_manualSlotSymbols);
            manuallyRemovedSymbols = new HashSet<string>(_manuallyRemovedSymbols, StringComparer.OrdinalIgnoreCase);
        }
        var storedOpenPositions = await _repository.GetOpenFuturesPositionsAsync(cancellationToken);

        var prioritySymbols = previousPoolSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in manualSlotSymbols.Values)
        {
            prioritySymbols.Add(symbol);
        }

        foreach (var position in storedOpenPositions)
        {
            if (!manuallyRemovedSymbols.Contains(position.Symbol))
            {
                prioritySymbols.Add(position.Symbol);
            }
        }

        var candidates = tickers
            .Where(ticker => tradable.ContainsKey(ticker.Symbol))
            .Where(ticker => ticker.LastPrice > 0m && ticker.Turnover24h > 0m)
            .OrderByDescending(ticker => ticker.Turnover24h)
            .Take(Math.Clamp(_options.ScanLimit, _options.PoolSize, 1000))
            .Concat(tickers.Where(ticker => prioritySymbols.Contains(ticker.Symbol)))
            .DistinctBy(ticker => ticker.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var evaluated = await EvaluateCandidatesAsync(candidates, tradable, anchors, cancellationToken);
        var openSymbols = await GetOpenSymbolsAsync(evaluated, cancellationToken);
        var nextPool = BuildNextPool(evaluated, openSymbols, manualSlotSymbols, manuallyRemovedSymbols);

        lock (_sync)
        {
            _pool.Clear();
            foreach (var item in nextPool)
            {
                _pool[item.Symbol] = item;
            }

            _lastScanAt = now;
            _status = $"Monitoring {nextPool.Count} pairs";
        }

        await SyncOpenPositionsAsync(nextPool, cancellationToken);
        await ProcessPaperStopsAsync(nextPool, tradable, cancellationToken);
        await ProcessSignalsAsync(nextPool, tradable, cancellationToken);
    }

    private IReadOnlyList<NySessionPoolItem> BuildNextPool(
        IReadOnlyList<NySessionPoolItem> evaluated,
        IReadOnlySet<string> openSymbols,
        IReadOnlyDictionary<int, string> manualSlotSymbols,
        IReadOnlySet<string> manuallyRemovedSymbols)
    {
        var manualSymbols = manualSlotSymbols.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orderedCandidates = evaluated
            .Where(item => IsPoolEligible(item) || openSymbols.Contains(item.Symbol))
            .Where(item => !manuallyRemovedSymbols.Contains(item.Symbol) || manualSymbols.Contains(item.Symbol))
            .Where(item =>
                openSymbols.Contains(item.Symbol) ||
                manualSymbols.Contains(item.Symbol) ||
                _backtestService.IsSymbolAllowedForTrading(item.Symbol, _options.RequireBacktestSymbolFilter))
            .OrderByDescending(item => openSymbols.Contains(item.Symbol))
            .ThenByDescending(item => ScorePoolItem(item))
            .ThenByDescending(item => item.Turnover24h)
            .ToArray();
        var evaluatedBySymbol = evaluated.ToDictionary(item => item.Symbol, StringComparer.OrdinalIgnoreCase);
        var usedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bySlot = new Dictionary<int, NySessionPoolItem>();
        var poolSize = Math.Max(1, _options.PoolSize);
        foreach (var pair in manualSlotSymbols.OrderBy(pair => pair.Key))
        {
            if (pair.Key < 1 || pair.Key > poolSize)
            {
                continue;
            }

            var symbol = pair.Value;
            var item = evaluatedBySymbol.TryGetValue(symbol, out var evaluatedItem)
                ? evaluatedItem.WithSlot(pair.Key)
                : BuildManualPendingPoolItem(pair.Key, symbol);
            bySlot[pair.Key] = item;
            usedSymbols.Add(symbol);
        }

        var candidateIndex = 0;
        for (var slot = 1; slot <= poolSize; slot++)
        {
            if (bySlot.ContainsKey(slot))
            {
                continue;
            }

            while (candidateIndex < orderedCandidates.Length && usedSymbols.Contains(orderedCandidates[candidateIndex].Symbol))
            {
                candidateIndex++;
            }

            if (candidateIndex >= orderedCandidates.Length)
            {
                break;
            }

            var item = orderedCandidates[candidateIndex].WithSlot(slot);
            bySlot[slot] = item;
            usedSymbols.Add(item.Symbol);
            candidateIndex++;
        }

        return bySlot
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToArray();
    }

    private async Task<IReadOnlyList<NySessionPoolItem>> EvaluateCandidatesAsync(
        IReadOnlyCollection<BybitTicker> candidates,
        IReadOnlyDictionary<string, BybitInstrumentInfo> instruments,
        SessionAnchors anchors,
        CancellationToken cancellationToken)
    {
        var results = new List<NySessionPoolItem>();
        using var throttler = new SemaphoreSlim(MaxConcurrency);
        var tasks = candidates.Select(async ticker =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var candles = await _bybitRestClient.GetKlinesAsync(Category, ticker.Symbol, FiveMinuteInterval, FiveMinuteLookback, cancellationToken);
                return AnalyzePoolCandidate(ticker, candles, anchors, DateTimeOffset.UtcNow);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(exception, "NY session candidate analysis failed for {Symbol}", ticker.Symbol);
                return null;
            }
            finally
            {
                throttler.Release();
            }
        });

        foreach (var item in await Task.WhenAll(tasks))
        {
            if (item is not null && instruments.ContainsKey(item.Symbol))
            {
                results.Add(item);
            }
        }

        return results;
    }

    private NySessionPoolItem? AnalyzePoolCandidate(
        BybitTicker ticker,
        IReadOnlyList<Candle> candles,
        SessionAnchors anchors,
        DateTimeOffset now)
    {
        var closed = candles
            .Where(candle => candle.OpenTime.AddMinutes(5) <= now)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        var rangeCandles = closed
            .Where(candle => candle.OpenTime >= anchors.RangeStartUtc && candle.OpenTime < anchors.RangeEndUtc)
            .ToArray();
        if (rangeCandles.Length < 2)
        {
            return null;
        }

        var high = rangeCandles.Max(candle => candle.High);
        var low = rangeCandles.Min(candle => candle.Low);
        if (high <= low || ticker.LastPrice <= 0m)
        {
            return null;
        }

        var midpoint = (high + low) / 2m;
        var rangePercent = midpoint > 0m ? (high - low) / midpoint * 100m : 0m;
        var distanceToUpper = Math.Abs(high - ticker.LastPrice) / ticker.LastPrice * 100m;
        var distanceToLower = Math.Abs(ticker.LastPrice - low) / ticker.LastPrice * 100m;
        var signal = TryFindSignal(closed, anchors);
        var state = ResolvePoolState(closed, anchors, signal);
        var bias = signal?.Side ?? ResolveBias(ticker.LastPrice, high, low, distanceToUpper, distanceToLower);
        var reason = signal is not null
            ? signal.Reason
            : state switch
            {
                "Upper swept" => "Waiting close back below 4h high.",
                "Lower swept" => "Waiting next close back above 4h low.",
                "Near boundary" => "Price is close enough to a 4h boundary to monitor.",
                _ => "No active 08:00 NY pattern."
            };

        return new NySessionPoolItem
        {
            Symbol = ticker.Symbol,
            LastPrice = ticker.LastPrice,
            FourHourHigh = high,
            FourHourLow = low,
            RangePercent = decimal.Round(rangePercent, 4, MidpointRounding.AwayFromZero),
            State = state,
            Bias = bias,
            DistanceToUpperPercent = decimal.Round(distanceToUpper, 4, MidpointRounding.AwayFromZero),
            DistanceToLowerPercent = decimal.Round(distanceToLower, 4, MidpointRounding.AwayFromZero),
            Turnover24h = ticker.Turnover24h,
            Reason = reason,
            UpdatedAt = now
        };
    }

    private static NySessionPoolItem BuildManualPendingPoolItem(int slot, string symbol) => new()
    {
        Slot = slot,
        Symbol = symbol,
        State = "Manual replacement",
        Bias = "Waiting scan",
        Reason = "Manual pair replacement is waiting for market data.",
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private async Task ProcessSignalsAsync(
        IReadOnlyCollection<NySessionPoolItem> pool,
        IReadOnlyDictionary<string, BybitInstrumentInfo> instruments,
        CancellationToken cancellationToken)
    {
        var openPositions = await CountOpenPositionsAsync(cancellationToken);
        foreach (var item in pool)
        {
            if (openPositions >= _options.MaxOpenPositions)
            {
                return;
            }

            var position = await ResolvePositionAsync(item.Symbol, item.LastPrice, cancellationToken);
            if (position.Size > 0m)
            {
                continue;
            }

            var candles = await _bybitRestClient.GetKlinesAsync(Category, item.Symbol, FiveMinuteInterval, FiveMinuteLookback, cancellationToken);
            var signal = TryFindSignal(candles, ResolveSessionAnchors(DateTimeOffset.UtcNow));
            if (signal is null ||
                IsSignalAlreadyHandled(item.Symbol, signal.SignalCandleOpenTime) ||
                await HasOpeningOrderAfterSignalAsync(item.Symbol, signal.SignalCandleOpenTime, cancellationToken))
            {
                continue;
            }

            if ((signal.Side == "Long" && !_options.AllowLongs) ||
                (signal.Side == "Short" && !_options.AllowShorts))
            {
                MarkSignalHandled(item.Symbol, signal.SignalCandleOpenTime);
                AddEvent(item.Symbol, "warning", $"{signal.Side} signal skipped by direction settings.");
                continue;
            }

            if (!_backtestService.IsSymbolAllowedForTrading(item.Symbol, _options.RequireBacktestSymbolFilter))
            {
                MarkSignalHandled(item.Symbol, signal.SignalCandleOpenTime);
                AddEvent(item.Symbol, "warning", "Symbol skipped by walk-forward backtest filter.");
                continue;
            }

            var filter = await EvaluateEntryFiltersAsync(item.Symbol, signal, candles, cancellationToken);
            if (!filter.IsAllowed)
            {
                MarkSignalHandled(item.Symbol, signal.SignalCandleOpenTime);
                AddEvent(item.Symbol, "warning", $"{filter.Mode}: {filter.Reason}");
                continue;
            }

            var instrument = MapInstrumentRules(instruments[item.Symbol]);
            var settings = BuildSettings(item.Symbol, signal);
            var intent = BuildOpenIntent(settings, signal, instrument);
            var result = await _executionService.ExecuteAsync(new FuturesExecutionRequest
            {
                Settings = settings,
                Intent = intent,
                Position = position,
                MarkPrice = signal.EntryPrice,
                Instrument = instrument
            }, cancellationToken);

            MarkSignalHandled(item.Symbol, signal.SignalCandleOpenTime);
            openPositions++;
            AddEvent(item.Symbol, "trade", $"{signal.Pattern} {signal.Side} opened at {signal.EntryPrice}. SL {signal.StopLoss}, TP {signal.TakeProfit}.");
            await _notifier.NotifyAsync(
                $"NY session entry.\nPattern: `{signal.Pattern}`\nSymbol: `{item.Symbol}`\nSide: `{signal.Side}`\nEntry: `{signal.EntryPrice}`\nSL: `{signal.StopLoss}`\nTP: `{signal.TakeProfit}`\nMode: `{_appOptions.TradingMode}`\nResult: `{result.Message}`",
                cancellationToken);
        }
    }

    private async Task ProcessPaperStopsAsync(
        IReadOnlyCollection<NySessionPoolItem> pool,
        IReadOnlyDictionary<string, BybitInstrumentInfo> instruments,
        CancellationToken cancellationToken)
    {
        if (_appOptions.TradingMode != TradingMode.Paper)
        {
            return;
        }

        foreach (var item in pool)
        {
            var position = await _repository.GetFuturesPositionAsync(item.Symbol, cancellationToken);
            if (position is null || position.Size <= 0m)
            {
                continue;
            }

            var orders = await _repository.GetFuturesOrdersAsync(item.Symbol, cancellationToken);
            var entryOrder = orders
                .Where(order => order.Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort)
                .OrderByDescending(order => order.CreatedAt)
                .FirstOrDefault();
            if (entryOrder is null || entryOrder.StopLossPrice <= 0m || entryOrder.TakeProfitPrice <= 0m)
            {
                continue;
            }

            var candles = await _bybitRestClient.GetKlinesAsync(Category, item.Symbol, FiveMinuteInterval, 3, cancellationToken);
            var lastClosed = candles
                .Where(candle => candle.OpenTime.AddMinutes(5) <= DateTimeOffset.UtcNow)
                .OrderBy(candle => candle.OpenTime)
                .LastOrDefault();
            if (lastClosed is null)
            {
                continue;
            }

            var exitPrice = ResolvePaperExitPrice(position, entryOrder, lastClosed);
            if (exitPrice is null)
            {
                continue;
            }

            var instrument = MapInstrumentRules(instruments[item.Symbol]);
            var action = IsShort(position.Side) ? FuturesTradeAction.CloseShort : FuturesTradeAction.CloseLong;
            var intent = new FuturesTradeIntent
            {
                Symbol = item.Symbol,
                Category = Category,
                Action = action,
                OrderType = OrderType.Market,
                Price = instrument.RoundPrice(exitPrice.Value),
                Quantity = instrument.RoundQuantity(position.Size),
                Leverage = position.Leverage > 0m ? position.Leverage : _futuresOptions.Leverage,
                PositionIdx = 0,
                OrderLinkId = FuturesOrderLinkIds.Create(action),
                Reason = "ny-session-paper-target"
            };

            var settings = BuildSettings(item.Symbol, new NySessionSignal
            {
                Side = IsShort(position.Side) ? "Short" : "Long",
                EntryPrice = position.EntryPrice,
                StopLoss = entryOrder.StopLossPrice,
                TakeProfit = entryOrder.TakeProfitPrice
            });
            await _executionService.ExecuteAsync(new FuturesExecutionRequest
            {
                Settings = settings,
                Intent = intent,
                Position = position,
                MarkPrice = intent.Price,
                Instrument = instrument
            }, cancellationToken);
            AddEvent(item.Symbol, "trade", $"Paper position closed at {intent.Price} by SL/TP.");
        }
    }

    private static decimal? ResolvePaperExitPrice(FuturesPositionSnapshot position, FuturesOrderRecord entryOrder, Candle candle)
    {
        if (IsShort(position.Side))
        {
            if (candle.High >= entryOrder.StopLossPrice)
            {
                return entryOrder.StopLossPrice;
            }

            if (candle.Low <= entryOrder.TakeProfitPrice)
            {
                return entryOrder.TakeProfitPrice;
            }
        }
        else
        {
            if (candle.Low <= entryOrder.StopLossPrice)
            {
                return entryOrder.StopLossPrice;
            }

            if (candle.High >= entryOrder.TakeProfitPrice)
            {
                return entryOrder.TakeProfitPrice;
            }
        }

        return null;
    }

    private async Task<NySessionEntryFilterResult> EvaluateEntryFiltersAsync(
        string symbol,
        NySessionSignal signal,
        IReadOnlyList<Candle> fiveMinuteCandles,
        CancellationToken cancellationToken)
    {
        if (string.Equals(signal.Pattern, "Engulfing", StringComparison.OrdinalIgnoreCase))
        {
            return await EvaluateEngulfingFiltersAsync(signal, cancellationToken);
        }

        if (string.Equals(signal.Pattern, "Pinbar", StringComparison.OrdinalIgnoreCase))
        {
            return await EvaluatePinbarFiltersAsync(signal, cancellationToken);
        }

        if (string.Equals(signal.Pattern, "3-Bar Continuation", StringComparison.OrdinalIgnoreCase))
        {
            return await EvaluateThreeBarContinuationFiltersAsync(signal, cancellationToken);
        }

        if (string.Equals(signal.Pattern, "3-Bar Reversal", StringComparison.OrdinalIgnoreCase))
        {
            return await EvaluateThreeBarReversalFiltersAsync(signal, cancellationToken);
        }

        if (string.Equals(signal.Pattern, "Breakout Candle", StringComparison.OrdinalIgnoreCase))
        {
            return await EvaluateBreakoutCandleFiltersAsync(signal, cancellationToken);
        }

        if (string.Equals(signal.Pattern, "Shrinking Candles", StringComparison.OrdinalIgnoreCase))
        {
            return await EvaluateShrinkingCandlesFiltersAsync(signal, cancellationToken);
        }

        if (signal.SweepDepthPercent < _options.MinSweepDepthPercent)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Failed Sweep Filter",
                Reason = $"Sweep depth {signal.SweepDepthPercent:F4}% is below {_options.MinSweepDepthPercent:F4}%."
            };
        }

        if (signal.ReclaimPercent < _options.MinReclaimPercent)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Failed Sweep Filter",
                Reason = $"Weak reclaim {signal.ReclaimPercent:F4}% is below {_options.MinReclaimPercent:F4}%."
            };
        }

        if (signal.StopDistancePercent > _options.MaxStopPercent)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Failed Sweep Filter",
                Reason = $"Stop distance {signal.StopDistancePercent:F4}% is above {_options.MaxStopPercent:F4}%."
            };
        }

        if (signal.MidlineRoomR < _options.MinMidlineRoomR)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Failed Sweep Filter",
                Reason = $"Room to 4H midline is {signal.MidlineRoomR:F2}R, below {_options.MinMidlineRoomR:F2}R."
            };
        }

        var fifteenMinuteCandles = await _bybitRestClient.GetKlinesAsync(
            Category,
            symbol,
            FifteenMinuteInterval,
            FifteenMinuteLookback,
            cancellationToken);
        var trueBreakout = AnalyzeTrueBreakout(signal, fiveMinuteCandles, fifteenMinuteCandles);
        if (trueBreakout.IsTrueBreakout)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "True Breakout Protection",
                Reason = trueBreakout.Reason
            };
        }

        if (signal.BreakoutVolumeRatio >= _options.HighBreakoutVolumeRatio)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Failed Sweep Filter",
                Reason = $"Breakout volume ratio {signal.BreakoutVolumeRatio:F2} is above {_options.HighBreakoutVolumeRatio:F2}."
            };
        }

        var btcTrend = await AnalyzeBtcTrendAsync(signal.Side, cancellationToken);
        if (btcTrend.IsBlocked)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Failed Sweep Filter",
                Reason = btcTrend.Reason
            };
        }

        return new NySessionEntryFilterResult
        {
            IsAllowed = true,
            Mode = "Sweep Reversal",
            Reason = "Sweep reclaim passed filters."
        };
    }

    private async Task<NySessionEntryFilterResult> EvaluateEngulfingFiltersAsync(
        NySessionSignal signal,
        CancellationToken cancellationToken)
    {
        if (!_options.EngulfingEnabled)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Engulfing",
                Reason = "Engulfing pattern is disabled."
            };
        }

        if (signal.BodyRatio < _options.MinEngulfingBodyRatio)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Engulfing",
                Reason = $"Body ratio {signal.BodyRatio:F2} is below {_options.MinEngulfingBodyRatio:F2}."
            };
        }

        if (signal.StopDistancePercent > _options.MaxStopPercent)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Engulfing",
                Reason = $"Stop distance {signal.StopDistancePercent:F4}% is above {_options.MaxStopPercent:F4}%."
            };
        }

        var btcTrend = await AnalyzeBtcTrendAsync(signal.Side, cancellationToken);
        if (btcTrend.IsBlocked)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Engulfing",
                Reason = btcTrend.Reason
            };
        }

        return new NySessionEntryFilterResult
        {
            IsAllowed = true,
            Mode = "Engulfing",
            Reason = "Engulfing pattern passed filters."
        };
    }

    private async Task<NySessionEntryFilterResult> EvaluatePinbarFiltersAsync(
        NySessionSignal signal,
        CancellationToken cancellationToken)
    {
        if (!_options.PinbarEnabled)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Pinbar",
                Reason = "Pinbar pattern is disabled."
            };
        }

        if (signal.WickBodyRatio < _options.MinPinbarWickBodyRatio)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Pinbar",
                Reason = $"Wick/body ratio {signal.WickBodyRatio:F2} is below {_options.MinPinbarWickBodyRatio:F2}."
            };
        }

        if (signal.WickRangePercent < _options.MinPinbarWickRangePercent)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Pinbar",
                Reason = $"Wick share {signal.WickRangePercent:F2}% is below {_options.MinPinbarWickRangePercent:F2}%."
            };
        }

        if (signal.StopDistancePercent > _options.MaxStopPercent)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Pinbar",
                Reason = $"Stop distance {signal.StopDistancePercent:F4}% is above {_options.MaxStopPercent:F4}%."
            };
        }

        var btcTrend = await AnalyzeBtcTrendAsync(signal.Side, cancellationToken);
        if (btcTrend.IsBlocked)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Pinbar",
                Reason = btcTrend.Reason
            };
        }

        return new NySessionEntryFilterResult
        {
            IsAllowed = true,
            Mode = "Pinbar",
            Reason = "Pinbar pattern passed filters."
        };
    }

    private async Task<NySessionEntryFilterResult> EvaluateThreeBarContinuationFiltersAsync(
        NySessionSignal signal,
        CancellationToken cancellationToken)
    {
        if (!_options.ThreeBarContinuationEnabled)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "3-Bar Continuation",
                Reason = "3-bar continuation pattern is disabled."
            };
        }

        if (signal.BodyRatio < _options.MinThreeBarOuterBodyRatio)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "3-Bar Continuation",
                Reason = $"Outer/body ratio {signal.BodyRatio:F2} is below {_options.MinThreeBarOuterBodyRatio:F2}."
            };
        }

        if (signal.StopDistancePercent > _options.MaxStopPercent)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "3-Bar Continuation",
                Reason = $"Stop distance {signal.StopDistancePercent:F4}% is above {_options.MaxStopPercent:F4}%."
            };
        }

        var btcTrend = await AnalyzeBtcTrendAsync(signal.Side, cancellationToken);
        if (btcTrend.IsBlocked)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "3-Bar Continuation",
                Reason = btcTrend.Reason
            };
        }

        return new NySessionEntryFilterResult
        {
            IsAllowed = true,
            Mode = "3-Bar Continuation",
            Reason = "3-bar continuation pattern passed filters."
        };
    }

    private async Task<NySessionEntryFilterResult> EvaluateThreeBarReversalFiltersAsync(
        NySessionSignal signal,
        CancellationToken cancellationToken)
    {
        if (!_options.ThreeBarReversalEnabled)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "3-Bar Reversal",
                Reason = "3-bar reversal pattern is disabled."
            };
        }

        if (signal.BodyRatio < _options.MinThreeBarOuterBodyRatio)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "3-Bar Reversal",
                Reason = $"Outer/body ratio {signal.BodyRatio:F2} is below {_options.MinThreeBarOuterBodyRatio:F2}."
            };
        }

        if (signal.StopDistancePercent > _options.MaxStopPercent)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "3-Bar Reversal",
                Reason = $"Stop distance {signal.StopDistancePercent:F4}% is above {_options.MaxStopPercent:F4}%."
            };
        }

        var btcTrend = await AnalyzeBtcTrendAsync(signal.Side, cancellationToken);
        if (btcTrend.IsBlocked)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "3-Bar Reversal",
                Reason = btcTrend.Reason
            };
        }

        return new NySessionEntryFilterResult
        {
            IsAllowed = true,
            Mode = "3-Bar Reversal",
            Reason = "3-bar reversal pattern passed filters."
        };
    }

    private async Task<NySessionEntryFilterResult> EvaluateBreakoutCandleFiltersAsync(
        NySessionSignal signal,
        CancellationToken cancellationToken)
    {
        if (!_options.BreakoutCandleEnabled)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Breakout Candle",
                Reason = "Breakout candle pattern is disabled."
            };
        }

        if (signal.BodyRatio < _options.MinBreakoutBodyRatio)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Breakout Candle",
                Reason = $"Breakout body ratio {signal.BodyRatio:F2} is below {_options.MinBreakoutBodyRatio:F2}."
            };
        }

        if (signal.StopDistancePercent > _options.MaxStopPercent)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Breakout Candle",
                Reason = $"Stop distance {signal.StopDistancePercent:F4}% is above {_options.MaxStopPercent:F4}%."
            };
        }

        var btcTrend = await AnalyzeBtcTrendAsync(signal.Side, cancellationToken);
        if (btcTrend.IsBlocked)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Breakout Candle",
                Reason = btcTrend.Reason
            };
        }

        return new NySessionEntryFilterResult
        {
            IsAllowed = true,
            Mode = "Breakout Candle",
            Reason = "Breakout candle pattern passed filters."
        };
    }

    private async Task<NySessionEntryFilterResult> EvaluateShrinkingCandlesFiltersAsync(
        NySessionSignal signal,
        CancellationToken cancellationToken)
    {
        if (!_options.ShrinkingCandlesEnabled)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Shrinking Candles",
                Reason = "Shrinking candles pattern is disabled."
            };
        }

        if (signal.BodyRatio < _options.MinShrinkingReversalBodyRatio)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Shrinking Candles",
                Reason = $"Reversal body ratio {signal.BodyRatio:F2} is below {_options.MinShrinkingReversalBodyRatio:F2}."
            };
        }

        if (signal.StopDistancePercent > _options.MaxStopPercent)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Shrinking Candles",
                Reason = $"Stop distance {signal.StopDistancePercent:F4}% is above {_options.MaxStopPercent:F4}%."
            };
        }

        var btcTrend = await AnalyzeBtcTrendAsync(signal.Side, cancellationToken);
        if (btcTrend.IsBlocked)
        {
            return new NySessionEntryFilterResult
            {
                IsAllowed = false,
                Mode = "Shrinking Candles",
                Reason = btcTrend.Reason
            };
        }

        return new NySessionEntryFilterResult
        {
            IsAllowed = true,
            Mode = "Shrinking Candles",
            Reason = "Shrinking candles pattern passed filters."
        };
    }

    private TrueBreakoutAssessment AnalyzeTrueBreakout(
        NySessionSignal signal,
        IReadOnlyList<Candle> fiveMinuteCandles,
        IReadOnlyList<Candle> fifteenMinuteCandles)
    {
        var signalClosedAt = signal.SignalCandleOpenTime.AddMinutes(5);
        var closed5m = fiveMinuteCandles
            .Where(candle => candle.OpenTime.AddMinutes(5) <= signalClosedAt)
            .Where(candle => candle.OpenTime >= signal.BreakoutCandleOpenTime && candle.OpenTime < signal.SignalCandleOpenTime)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        var closed15m = fifteenMinuteCandles
            .Where(candle => candle.OpenTime.AddMinutes(15) <= signalClosedAt)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        var last15m = closed15m.LastOrDefault();
        var adx = CalculateAdx(closed15m.TakeLast(40).ToArray(), 14);
        var previousAdx = CalculateAdx(closed15m.TakeLast(45).SkipLast(5).ToArray(), 14);
        var adxRising = adx >= _options.TrueBreakoutAdx && adx > previousAdx;
        var highVolume = signal.BreakoutVolumeRatio >= _options.HighBreakoutVolumeRatio;
        var fiveMinuteHeld = signal.Side == "Short"
            ? closed5m.Count(candle => candle.Close > signal.Boundary) >= 1
            : closed5m.Count(candle => candle.Close < signal.Boundary) >= 1;
        var fifteenMinuteOutside = last15m is not null && (signal.Side == "Short"
            ? last15m.Close > signal.Boundary
            : last15m.Close < signal.Boundary);

        if ((highVolume || adxRising) && (fiveMinuteHeld || fifteenMinuteOutside))
        {
            var direction = signal.Side == "Short" ? "upper" : "lower";
            return new TrueBreakoutAssessment(
                true,
                $"{direction} breakout looks real: volume ratio {signal.BreakoutVolumeRatio:F2}, ADX {adx:F2} rising, level is holding.");
        }

        return new TrueBreakoutAssessment(false, string.Empty);
    }

    private async Task<BtcTrendAssessment> AnalyzeBtcTrendAsync(string signalSide, CancellationToken cancellationToken)
    {
        IReadOnlyList<Candle> candles;
        try
        {
            candles = await _bybitRestClient.GetKlinesAsync(Category, "BTCUSDT", FifteenMinuteInterval, FifteenMinuteLookback, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "BTC trend guard unavailable.");
            return new BtcTrendAssessment(false, "BTC trend guard unavailable.");
        }

        var closed = candles
            .Where(candle => candle.OpenTime.AddMinutes(15) <= DateTimeOffset.UtcNow)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        if (closed.Length < 30)
        {
            return new BtcTrendAssessment(false, "BTC trend guard has not enough candles.");
        }

        var last = closed[^1];
        var baseline = closed.TakeLast(12).First();
        var movePercent = baseline.Open > 0m ? (last.Close - baseline.Open) / baseline.Open * 100m : 0m;
        var adx = CalculateAdx(closed.TakeLast(40).ToArray(), 14);
        var btcTrendingHard = Math.Abs(movePercent) >= _options.BtcTrendMovePercent && adx >= _options.BtcTrendAdx;
        if (!btcTrendingHard)
        {
            return new BtcTrendAssessment(false, "BTC trend is acceptable.");
        }

        var againstBtcTrain =
            signalSide == "Short" && movePercent > 0m ||
            signalSide == "Long" && movePercent < 0m;
        return againstBtcTrain
            ? new BtcTrendAssessment(true, $"BTC is trending against the reversal: move {movePercent:F2}%, ADX {adx:F2}.")
            : new BtcTrendAssessment(false, "BTC trend is not against this reversal.");
    }

    private NySessionSignal? TryFindSignal(
        IReadOnlyList<Candle> candles,
        SessionAnchors anchors)
    {
        var closed = candles
            .Where(candle => candle.OpenTime.AddMinutes(5) <= DateTimeOffset.UtcNow)
            .Where(candle => candle.OpenTime >= anchors.SessionStartUtc)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        if (closed.Length < 2)
        {
            return null;
        }

        var upperBoundary = closed[0].High;
        var lowerBoundary = closed[0].Low;
        decimal? upperStop = null;
        DateTimeOffset? upperSweepAt = null;
        decimal? upperReturnLevel = null;
        decimal? lowerStop = null;
        DateTimeOffset? lowerSweepAt = null;
        decimal? lowerReturnLevel = null;
        NySessionSignal? latestSignal = null;

        foreach (var candle in closed.Skip(1))
        {
            if (candle.High > upperBoundary && candle.Close < upperBoundary)
            {
                var risk = candle.High - candle.Close;
                if (risk > 0m)
                {
                    latestSignal = BuildSignal(
                        "Short",
                        candle.OpenTime,
                        candle.OpenTime,
                        upperBoundary,
                        upperBoundary,
                        lowerBoundary,
                        candle.Close,
                        candle.High,
                        "Swept above the active 08:00 NY 4h high and closed back below.",
                        closed);
                    upperStop = null;
                    upperSweepAt = null;
                    upperReturnLevel = null;
                }
            }

            if (candle.High > upperBoundary && candle.Close > upperBoundary)
            {
                upperStop = upperStop is null ? candle.High : decimal.Max(upperStop.Value, candle.High);
                upperSweepAt = candle.OpenTime;
                upperReturnLevel = upperBoundary;
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
                    latestSignal = BuildSignal(
                        "Short",
                        candle.OpenTime,
                        upperSweepAt.Value,
                        upperReturnLevel.Value,
                        upperBoundary,
                        lowerBoundary,
                        candle.Close,
                        upperStop.Value,
                        "Swept above the active 08:00 NY 4h high and reclaimed back below.",
                        closed);
                    upperStop = null;
                    upperSweepAt = null;
                    upperReturnLevel = null;
                }
            }

            if (candle.Low < lowerBoundary && candle.Close < lowerBoundary)
            {
                lowerStop = lowerStop is null ? candle.Low : decimal.Min(lowerStop.Value, candle.Low);
                lowerSweepAt = candle.OpenTime;
                lowerReturnLevel = lowerBoundary;
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
                    latestSignal = BuildSignal(
                        "Long",
                        candle.OpenTime,
                        lowerSweepAt.Value,
                        lowerReturnLevel.Value,
                        upperBoundary,
                        lowerBoundary,
                        candle.Close,
                        lowerStop.Value,
                        "Swept below the active 08:00 NY 4h low and reclaimed back above.",
                        closed);
                    lowerStop = null;
                    lowerSweepAt = null;
                    lowerReturnLevel = null;
                }
            }

            if (candle.OpenTime < anchors.RangeEndUtc)
            {
                upperBoundary = decimal.Max(upperBoundary, candle.High);
                lowerBoundary = decimal.Min(lowerBoundary, candle.Low);
            }
        }

        var engulfingSignal = TryFindEngulfingSignal(closed, upperBoundary, lowerBoundary);
        if (engulfingSignal is not null &&
            (latestSignal is null || engulfingSignal.SignalCandleOpenTime > latestSignal.SignalCandleOpenTime))
        {
            return engulfingSignal;
        }

        var pinbarSignal = TryFindPinbarSignal(closed, upperBoundary, lowerBoundary);
        if (pinbarSignal is not null &&
            (latestSignal is null || pinbarSignal.SignalCandleOpenTime > latestSignal.SignalCandleOpenTime))
        {
            return pinbarSignal;
        }

        var threeBarSignal = TryFindThreeBarContinuationSignal(closed, upperBoundary, lowerBoundary);
        if (threeBarSignal is not null &&
            (latestSignal is null || threeBarSignal.SignalCandleOpenTime > latestSignal.SignalCandleOpenTime))
        {
            return threeBarSignal;
        }

        var threeBarReversalSignal = TryFindThreeBarReversalSignal(closed, upperBoundary, lowerBoundary);
        if (threeBarReversalSignal is not null &&
            (latestSignal is null || threeBarReversalSignal.SignalCandleOpenTime > latestSignal.SignalCandleOpenTime))
        {
            return threeBarReversalSignal;
        }

        var breakoutCandleSignal = TryFindBreakoutCandleSignal(closed, upperBoundary, lowerBoundary);
        if (breakoutCandleSignal is not null &&
            (latestSignal is null || breakoutCandleSignal.SignalCandleOpenTime > latestSignal.SignalCandleOpenTime))
        {
            return breakoutCandleSignal;
        }

        var shrinkingCandlesSignal = TryFindShrinkingCandlesSignal(closed, upperBoundary, lowerBoundary);
        if (shrinkingCandlesSignal is not null &&
            (latestSignal is null || shrinkingCandlesSignal.SignalCandleOpenTime > latestSignal.SignalCandleOpenTime))
        {
            return shrinkingCandlesSignal;
        }

        return latestSignal;
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
            ? entryPrice - risk * _options.RewardRisk
            : entryPrice + risk * _options.RewardRisk;
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
        if (!_options.EngulfingEnabled || closed.Count < 2 || upperBoundary <= lowerBoundary)
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
        if (previousBody <= 0m || currentBody < previousBody * _options.MinEngulfingBodyRatio)
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

    private NySessionSignal? TryFindShrinkingCandlesSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        var sequenceCount = Math.Max(3, _options.ShrinkingSequenceCandles);
        if (!_options.ShrinkingCandlesEnabled || closed.Count < sequenceCount + 1 || upperBoundary <= lowerBoundary)
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
            if (bodies[index - 1] / bodies[index] < _options.MinShrinkingBodyStepRatio)
            {
                return null;
            }
        }

        var reversalBody = Math.Abs(reversal.Close - reversal.Open);
        var reversalBodyRatio = reversalBody / bodies.Average();
        if (reversalBody <= 0m || reversalBodyRatio < _options.MinShrinkingReversalBodyRatio)
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

    private NySessionSignal? TryFindBreakoutCandleSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        var consolidationCount = Math.Max(2, _options.BreakoutConsolidationCandles);
        if (!_options.BreakoutCandleEnabled || closed.Count < consolidationCount + 1 || upperBoundary <= lowerBoundary)
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
            consolidationRangePercent > _options.MaxBreakoutConsolidationRangePercent)
        {
            return null;
        }

        var averageBody = consolidation.Average(candle => Math.Abs(candle.Close - candle.Open));
        var breakoutBody = Math.Abs(breakout.Close - breakout.Open);
        if (averageBody <= 0m || breakoutBody / averageBody < _options.MinBreakoutBodyRatio)
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

    private NySessionSignal? TryFindThreeBarReversalSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        if (!_options.ThreeBarReversalEnabled || closed.Count < 3 || upperBoundary <= lowerBoundary)
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
        if (outerBodyRatio < _options.MinThreeBarOuterBodyRatio)
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

    private NySessionSignal? TryFindThreeBarContinuationSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        if (!_options.ThreeBarContinuationEnabled || closed.Count < 3 || upperBoundary <= lowerBoundary)
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
        if (outerBodyRatio < _options.MinThreeBarOuterBodyRatio)
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

    private NySessionSignal? TryFindPinbarSignal(
        IReadOnlyList<Candle> closed,
        decimal upperBoundary,
        decimal lowerBoundary)
    {
        if (!_options.PinbarEnabled || closed.Count < 1 || upperBoundary <= lowerBoundary)
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
        if (range <= 0m || body <= 0m || body / range * 100m > _options.MaxPinbarBodyRangePercent)
        {
            return null;
        }

        var upperWick = current.High - decimal.Max(current.Open, current.Close);
        var lowerWick = decimal.Min(current.Open, current.Close) - current.Low;
        var bullish = lowerWick / body >= _options.MinPinbarWickBodyRatio &&
            lowerWick / range * 100m >= _options.MinPinbarWickRangePercent &&
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

        var bearish = upperWick / body >= _options.MinPinbarWickBodyRatio &&
            upperWick / range * 100m >= _options.MinPinbarWickRangePercent &&
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

    private static decimal CalculateReclaimPercent(string side, decimal boundary, decimal close)
    {
        if (boundary <= 0m)
        {
            return 0m;
        }

        var distance = side == "Short"
            ? boundary - close
            : close - boundary;
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

        var breakoutVolume = ordered[index].Volume;
        var previous = ordered.Take(index).TakeLast(20).ToArray();
        if (previous.Length == 0)
        {
            return 1m;
        }

        var average = previous.Average(candle => candle.Volume);
        return average > 0m ? breakoutVolume / average : 1m;
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

    private static string ResolvePoolState(
        IReadOnlyList<Candle> candles,
        SessionAnchors anchors,
        NySessionSignal? signal)
    {
        if (signal is not null)
        {
            if (string.Equals(signal.Pattern, "Engulfing", StringComparison.OrdinalIgnoreCase))
            {
                return "Engulfing";
            }

            if (string.Equals(signal.Pattern, "Pinbar", StringComparison.OrdinalIgnoreCase))
            {
                return "Pinbar";
            }

            if (string.Equals(signal.Pattern, "3-Bar Continuation", StringComparison.OrdinalIgnoreCase))
            {
                return "3-Bar Cont";
            }

            if (string.Equals(signal.Pattern, "Breakout Candle", StringComparison.OrdinalIgnoreCase))
            {
                return "Breakout";
            }

            if (string.Equals(signal.Pattern, "Shrinking Candles", StringComparison.OrdinalIgnoreCase))
            {
                return "Shrinking";
            }

            return string.Equals(signal.Pattern, "3-Bar Reversal", StringComparison.OrdinalIgnoreCase)
                ? "3-Bar Rev"
                : "Signal";
        }

        var session = candles
            .Where(candle => candle.OpenTime >= anchors.SessionStartUtc)
            .OrderBy(candle => candle.OpenTime)
            .ToArray();
        if (session.Length < 2)
        {
            return "Building range";
        }

        var upperBoundary = session[0].High;
        var lowerBoundary = session[0].Low;
        foreach (var candle in session.Skip(1))
        {
            if (candle.High > upperBoundary && candle.Close > upperBoundary)
            {
                return "Upper swept";
            }

            if (candle.Low < lowerBoundary && candle.Close < lowerBoundary)
            {
                return "Lower swept";
            }

            if (candle.OpenTime < anchors.RangeEndUtc)
            {
                upperBoundary = decimal.Max(upperBoundary, candle.High);
                lowerBoundary = decimal.Min(lowerBoundary, candle.Low);
            }
        }

        return "Near boundary";
    }

    private bool IsPoolEligible(NySessionPoolItem item)
    {
        if (item.RangePercent < _options.MinFourHourRangePercent ||
            item.RangePercent > _options.MaxFourHourRangePercent)
        {
            return false;
        }

        return item.State == "Signal" ||
            item.State == "Engulfing" ||
            item.State == "Pinbar" ||
            item.State == "3-Bar Cont" ||
            item.State == "3-Bar Rev" ||
            item.State == "Breakout" ||
            item.State == "Shrinking" ||
            item.State == "Upper swept" ||
            item.State == "Lower swept" ||
            item.DistanceToUpperPercent <= _options.NearBoundaryPercent ||
            item.DistanceToLowerPercent <= _options.NearBoundaryPercent;
    }

    private static decimal ScorePoolItem(NySessionPoolItem item)
    {
        var stateScore = item.State switch
        {
            "Signal" => 100m,
            "Engulfing" => 90m,
            "Pinbar" => 85m,
            "3-Bar Rev" => 83m,
            "3-Bar Cont" => 82m,
            "Breakout" => 81m,
            "Shrinking" => 84m,
            "Upper swept" or "Lower swept" => 80m,
            _ => 50m
        };
        var boundaryDistance = decimal.Min(item.DistanceToUpperPercent, item.DistanceToLowerPercent);
        return stateScore - boundaryDistance;
    }

    private async Task<IReadOnlyList<NySessionOpenTradeItem>> BuildOpenTradesAsync(
        IReadOnlyCollection<NySessionPoolItem> pool,
        CancellationToken cancellationToken)
    {
        var openPositions = await _repository.GetOpenFuturesPositionsAsync(cancellationToken);
        var symbols = pool
            .Select(item => item.Symbol)
            .Concat(openPositions.Select(position => position.Symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new List<NySessionOpenTradeItem>();
        foreach (var symbol in symbols)
        {
            var position = await _repository.GetFuturesPositionAsync(symbol, cancellationToken);
            if (position is null || position.Size <= 0m)
            {
                continue;
            }

            var orders = await _repository.GetFuturesOrdersAsync(symbol, cancellationToken);
            var entryOrder = orders
                .Where(order => order.Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort)
                .OrderByDescending(order => order.CreatedAt)
                .FirstOrDefault();
            var pnlPercent = position.EntryPrice > 0m && position.Size > 0m
                ? position.UnrealizedPnl / (position.EntryPrice * position.Size) * 100m
                : 0m;
            result.Add(new NySessionOpenTradeItem
            {
                Symbol = symbol,
                Side = position.Side,
                Size = position.Size,
                EntryPrice = position.EntryPrice,
                MarkPrice = position.MarkPrice,
                StopLoss = entryOrder?.StopLossPrice ?? 0m,
                TakeProfit = entryOrder?.TakeProfitPrice ?? 0m,
                UnrealizedPnl = position.UnrealizedPnl,
                UnrealizedPnlPercent = pnlPercent,
                UpdatedAt = position.UpdatedAt
            });
        }

        return result.OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    private async Task SyncOpenPositionsAsync(IReadOnlyCollection<NySessionPoolItem> pool, CancellationToken cancellationToken)
    {
        foreach (var item in pool)
        {
            var position = await ResolvePositionAsync(item.Symbol, item.LastPrice, cancellationToken);
            await _repository.UpsertFuturesPositionAsync(position, _appOptions.TradingMode, cancellationToken);
        }
    }

    private async Task<FuturesPositionSnapshot> ResolvePositionAsync(string symbol, decimal markPrice, CancellationToken cancellationToken)
    {
        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var paper = await _repository.GetFuturesPositionAsync(symbol, cancellationToken);
            if (paper is null || paper.Size <= 0m)
            {
                return new FuturesPositionSnapshot { Symbol = symbol, Category = Category, MarkPrice = markPrice };
            }

            var unrealized = IsShort(paper.Side)
                ? (paper.EntryPrice - markPrice) * paper.Size
                : (markPrice - paper.EntryPrice) * paper.Size;
            return new FuturesPositionSnapshot
            {
                Symbol = paper.Symbol,
                Category = paper.Category,
                Side = paper.Side,
                Size = paper.Size,
                EntryPrice = paper.EntryPrice,
                MarkPrice = markPrice,
                LiquidationPrice = paper.LiquidationPrice,
                PositionValueUsdt = markPrice * paper.Size,
                MarginUsedUsdt = paper.MarginUsedUsdt,
                Leverage = paper.Leverage,
                UnrealizedPnl = unrealized,
                RealizedPnl = paper.RealizedPnl,
                Funding = paper.Funding,
                PositionIdx = paper.PositionIdx,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        var bybit = await _bybitRestClient.GetPositionAsync(Category, symbol, cancellationToken);
        if (bybit is null || bybit.Size <= 0m)
        {
            return new FuturesPositionSnapshot { Symbol = symbol, Category = Category, MarkPrice = markPrice };
        }

        return new FuturesPositionSnapshot
        {
            Symbol = bybit.Symbol,
            Category = Category,
            Side = bybit.Side,
            Size = bybit.Size,
            EntryPrice = bybit.AveragePrice,
            MarkPrice = bybit.MarkPrice > 0m ? bybit.MarkPrice : markPrice,
            LiquidationPrice = bybit.LiquidationPrice,
            PositionValueUsdt = bybit.PositionValue,
            MarginUsedUsdt = bybit.PositionInitialMargin,
            Leverage = bybit.Leverage,
            UnrealizedPnl = bybit.UnrealizedPnl,
            RealizedPnl = bybit.RealizedPnl,
            PositionIdx = bybit.PositionIdx,
            UpdatedAt = bybit.UpdatedAt
        };
    }

    private async Task<int> CountOpenPositionsAsync(CancellationToken cancellationToken)
    {
        var positions = await _repository.GetOpenFuturesPositionsAsync(cancellationToken);
        return positions.Count;
    }

    private async Task<IReadOnlySet<string>> GetOpenSymbolsAsync(
        IReadOnlyCollection<NySessionPoolItem> pool,
        CancellationToken cancellationToken)
    {
        var positions = await _repository.GetOpenFuturesPositionsAsync(cancellationToken);
        var result = positions
            .Select(position => position.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in pool.Select(item => item.Symbol).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var position = await _repository.GetFuturesPositionAsync(symbol, cancellationToken);
            if (position?.Size > 0m)
            {
                result.Add(symbol);
            }
        }

        return result;
    }

    private async Task<bool> HasOpeningOrderAfterSignalAsync(
        string symbol,
        DateTimeOffset signalCandleOpenTime,
        CancellationToken cancellationToken)
    {
        var orders = await _repository.GetFuturesOrdersAsync(symbol, cancellationToken);
        return orders.Any(order =>
            order.Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort &&
            order.CreatedAt >= signalCandleOpenTime);
    }

    private FuturesTradeIntent BuildOpenIntent(
        FuturesBotSettings settings,
        NySessionSignal signal,
        FuturesInstrumentRules instrument)
    {
        var price = instrument.RoundPrice(signal.EntryPrice);
        var quantity = ResolveEntryQuantity(_options.EntryNotionalUsdt, price, instrument);
        var action = signal.Side == "Short" ? FuturesTradeAction.OpenShort : FuturesTradeAction.OpenLong;
        var stopLoss = instrument.RoundPrice(signal.StopLoss);
        var takeProfit = instrument.RoundPrice(signal.TakeProfit);

        return new FuturesTradeIntent
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            Action = action,
            OrderType = OrderType.Market,
            Price = price,
            Quantity = quantity,
            Leverage = settings.Leverage,
            StopLossPrice = stopLoss,
            TakeProfitPrice = takeProfit,
            LiquidationPrice = action == FuturesTradeAction.OpenShort
                ? EstimateShortLiquidationPrice(price, settings.Leverage)
                : EstimateLongLiquidationPrice(price, settings.Leverage),
            PositionIdx = 0,
            OrderLinkId = FuturesOrderLinkIds.Create(action),
            Reason = signal.Pattern switch
            {
                "Engulfing" => "ny-session-engulfing",
                "Pinbar" => "ny-session-pinbar",
                "3-Bar Continuation" => "ny-session-3-bar-continuation",
                "3-Bar Reversal" => "ny-session-3-bar-reversal",
                "Breakout Candle" => "ny-session-breakout-candle",
                "Shrinking Candles" => "ny-session-shrinking-candles",
                _ => "ny-session-4h-sweep-reclaim"
            }
        };
    }

    private FuturesBotSettings BuildSettings(string symbol, NySessionSignal signal) => new()
    {
        Enabled = true,
        Symbol = symbol,
        Category = Category,
        StrategyType = FuturesStrategyType.NySessionBreakout,
        StrategyConfigJson = FormattableString.Invariant($"{{\"entryNotionalUsdt\":{_options.EntryNotionalUsdt},\"rewardRisk\":{_options.RewardRisk}}}"),
        Leverage = decimal.Min(_futuresOptions.Leverage, _futuresOptions.MvpMaxLeverage),
        MarginMode = FuturesMarginMode.Isolated,
        PositionMode = FuturesPositionMode.OneWay,
        Direction = signal.Side == "Short" ? FuturesDirection.ShortOnly : FuturesDirection.LongOnly,
        MaxNotionalUsdt = decimal.Max(_futuresOptions.MaxNotionalUsdt, _options.EntryNotionalUsdt),
        MaxMarginUsdt = decimal.Max(_futuresOptions.MaxMarginUsdt, _options.EntryNotionalUsdt / decimal.Max(1m, _futuresOptions.Leverage)),
        StopLossPercent = 1m,
        TakeProfitPercent = _options.RewardRisk,
        LiquidationBufferPercent = _futuresOptions.MinLiquidationBufferPercent,
        ReduceOnlyEnabled = true,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private bool IsSignalAlreadyHandled(string symbol, DateTimeOffset signalCandleOpenTime)
    {
        lock (_sync)
        {
            return _lastSignalBySymbol.TryGetValue(symbol, out var handledAt) && handledAt >= signalCandleOpenTime;
        }
    }

    private void MarkSignalHandled(string symbol, DateTimeOffset signalCandleOpenTime)
    {
        lock (_sync)
        {
            _lastSignalBySymbol[symbol] = signalCandleOpenTime;
        }
    }

    private void SetStatus(string status)
    {
        lock (_sync)
        {
            _status = status;
        }
    }

    private void SetAnchors(SessionAnchors anchors)
    {
        lock (_sync)
        {
            _sessionStart = anchors.SessionStartUtc;
            _rangeStart = anchors.RangeStartUtc;
            _rangeEnd = anchors.RangeEndUtc;
        }
    }

    private void AddEvent(string symbol, string level, string message)
    {
        lock (_sync)
        {
            _events.Enqueue(new NySessionEventItem
            {
                CreatedAt = DateTimeOffset.UtcNow,
                Symbol = symbol,
                Level = level,
                Message = message
            });
            while (_events.Count > 100)
            {
                _events.Dequeue();
            }
        }
    }

    private void ValidateTradingMode()
    {
        if (_appOptions.TradingMode == TradingMode.Mainnet &&
            (!_futuresOptions.MainnetEnabled || !_futuresOptions.MainnetOrderPlacementEnabled))
        {
            throw new InvalidOperationException("Futures mainnet order placement is blocked by safety flags.");
        }

        if (_appOptions.TradingMode == TradingMode.Testnet && !_futuresOptions.TestnetEnabled)
        {
            throw new InvalidOperationException("Futures testnet is disabled. Set FUTURES_TESTNET_ENABLED=true.");
        }
    }

    private static bool IsTradable(BybitInstrumentInfo instrument) =>
        string.Equals(instrument.Status, "Trading", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(instrument.QuoteCoin, "USDT", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(instrument.ContractType, "LinearPerpetual", StringComparison.OrdinalIgnoreCase) &&
        instrument.MinOrderQty > 0m;

    private static string ResolveBias(decimal lastPrice, decimal high, decimal low, decimal distanceToUpper, decimal distanceToLower)
    {
        if (lastPrice > high)
        {
            return "Short watch";
        }

        if (lastPrice < low)
        {
            return "Long watch";
        }

        return distanceToUpper <= distanceToLower ? "Upper watch" : "Lower watch";
    }

    private static string NormalizeSymbol(string symbol) =>
        symbol.Trim().ToUpperInvariant();

    private static FuturesInstrumentRules MapInstrumentRules(BybitInstrumentInfo instrument) => new()
    {
        TickSize = instrument.TickSize,
        QtyStep = instrument.QtyStep,
        BasePrecision = instrument.BasePrecision,
        MinOrderQty = instrument.MinOrderQty,
        MinOrderAmount = instrument.MinOrderAmount
    };

    private static decimal ResolveEntryQuantity(decimal notional, decimal price, FuturesInstrumentRules instrument)
    {
        var minimum = instrument.MinOrderQty;
        if (price > 0m && instrument.MinOrderAmount > 0m)
        {
            minimum = decimal.Max(minimum, instrument.MinOrderAmount / price);
        }

        var requested = price > 0m ? notional / price : minimum;
        var step = instrument.QtyStep > 0m ? instrument.QtyStep : instrument.BasePrecision;
        return step > 0m ? Math.Ceiling(decimal.Max(requested, minimum) / step) * step : decimal.Max(requested, minimum);
    }

    private static decimal EstimateLongLiquidationPrice(decimal entryPrice, decimal leverage) =>
        leverage > 0m ? decimal.Max(0m, entryPrice * (1m - (1m / leverage))) : 0m;

    private static decimal EstimateShortLiquidationPrice(decimal entryPrice, decimal leverage) =>
        leverage > 0m ? entryPrice * (1m + (1m / leverage)) : 0m;

    private static bool IsShort(string side) =>
        side.Equals("Short", StringComparison.OrdinalIgnoreCase) ||
        side.Equals("Sell", StringComparison.OrdinalIgnoreCase);

    private static SessionAnchors ResolveSessionAnchors(DateTimeOffset utcNow)
    {
        var nyZone = ResolveNewYorkTimeZone();
        var nyNow = TimeZoneInfo.ConvertTime(utcNow, nyZone);
        var sessionLocal = new DateTimeOffset(nyNow.Year, nyNow.Month, nyNow.Day, 8, 0, 0, nyNow.Offset);
        if (nyNow.TimeOfDay < TimeSpan.FromHours(8))
        {
            sessionLocal = sessionLocal.AddDays(-1);
        }

        var rangeStartLocal = sessionLocal;
        var rangeEndLocal = sessionLocal.AddHours(4);
        var sessionStartUtc = TimeZoneInfo.ConvertTime(sessionLocal, TimeZoneInfo.Utc);
        var rangeStartUtc = TimeZoneInfo.ConvertTime(rangeStartLocal, TimeZoneInfo.Utc);
        var rangeEndUtc = TimeZoneInfo.ConvertTime(rangeEndLocal, TimeZoneInfo.Utc);
        return new SessionAnchors(rangeStartUtc, rangeEndUtc, sessionStartUtc);
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

    private readonly record struct TrueBreakoutAssessment(bool IsTrueBreakout, string Reason);

    private readonly record struct BtcTrendAssessment(bool IsBlocked, string Reason);

    private readonly record struct SessionAnchors(
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        DateTimeOffset SessionStartUtc);
}

file static class NySessionPoolItemExtensions
{
    public static NySessionPoolItem WithSlot(this NySessionPoolItem item, int slot) => new()
    {
        Slot = slot,
        Symbol = item.Symbol,
        LastPrice = item.LastPrice,
        FourHourHigh = item.FourHourHigh,
        FourHourLow = item.FourHourLow,
        RangePercent = item.RangePercent,
        State = item.State,
        Bias = item.Bias,
        DistanceToUpperPercent = item.DistanceToUpperPercent,
        DistanceToLowerPercent = item.DistanceToLowerPercent,
        Turnover24h = item.Turnover24h,
        Reason = item.Reason,
        UpdatedAt = item.UpdatedAt
    };
}
