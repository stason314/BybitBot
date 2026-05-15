using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Notifications;
using BybitGridBot.Risk;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public sealed class FuturesBotWorker : BackgroundService
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(30);

    private readonly AppOptions _appOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly FuturesExecutionService _executionService;
    private readonly FuturesOptions _futuresOptions;
    private readonly FuturesPreflightService _preflightService;
    private readonly FuturesReconciliationService _reconciliationService;
    private readonly FuturesRiskManager _riskManager;
    private readonly FuturesRiskOptions _riskOptions;
    private readonly FuturesStrategyQualityOptions _strategyQualityOptions;
    private readonly FuturesStrategyRouter _strategyRouter;
    private readonly ILogger<FuturesBotWorker> _logger;
    private readonly ITelegramNotifier _notifier;
    private readonly IGridRepository _repository;

    public FuturesBotWorker(
        IOptions<AppOptions> appOptions,
        IOptions<FuturesOptions> futuresOptions,
        IOptions<FuturesRiskOptions> riskOptions,
        IOptions<FuturesStrategyQualityOptions> strategyQualityOptions,
        IBybitRestClient bybitRestClient,
        FuturesExecutionService executionService,
        FuturesPreflightService preflightService,
        FuturesReconciliationService reconciliationService,
        FuturesRiskManager riskManager,
        FuturesStrategyRouter strategyRouter,
        IGridRepository repository,
        ITelegramNotifier notifier,
        ILogger<FuturesBotWorker> logger)
    {
        _appOptions = appOptions.Value;
        _futuresOptions = futuresOptions.Value;
        _riskOptions = riskOptions.Value;
        _strategyQualityOptions = strategyQualityOptions.Value;
        _bybitRestClient = bybitRestClient;
        _executionService = executionService;
        _preflightService = preflightService;
        _reconciliationService = reconciliationService;
        _riskManager = riskManager;
        _strategyRouter = strategyRouter;
        _repository = repository;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_futuresOptions.Enabled)
        {
            _logger.LogInformation("Futures worker disabled. Set FUTURES_ENABLED=true to enable futures paper/testnet cycles.");
            return;
        }

        if (_appOptions.TradingMode == TradingMode.Mainnet && !_futuresOptions.MainnetEnabled)
        {
            throw new InvalidOperationException("Futures mainnet is blocked. Set FUTURES_MAINNET_ENABLED=true only after the mainnet checklist is complete.");
        }

        if (_appOptions.TradingMode == TradingMode.Testnet && !_futuresOptions.TestnetEnabled)
        {
            throw new InvalidOperationException("Futures worker is paper-only until FUTURES_TESTNET_ENABLED=true.");
        }

        _logger.LogInformation("Starting futures worker. Mode: {TradingMode}", _appOptions.TradingMode);
        await _notifier.NotifyAsync($"Futures worker started.\nMode: `{_appOptions.TradingMode}`", stoppingToken);

        using var timer = new PeriodicTimer(LoopInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var profiles = await _repository.GetFuturesSettingsProfilesAsync(stoppingToken);
                foreach (var profile in profiles)
                {
                    await RunProfileCycleAsync(profile, profiles, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unhandled futures worker loop error.");
                await _notifier.NotifyAsync($"Futures worker error: `{exception.Message}`", stoppingToken);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }

    private async Task RunProfileCycleAsync(
        FuturesBotSettings settings,
        IReadOnlyCollection<FuturesBotSettings> profiles,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            _logger.LogInformation("Futures profile disabled. Symbol: {Symbol}", settings.Symbol);
            return;
        }

        ValidateMvpSettings(settings);
        var ticker = await _bybitRestClient.GetTickerAsync(settings.Category, settings.Symbol, cancellationToken);
        var currentPrice = ticker.LastPrice;
        var instrument = await _bybitRestClient.GetInstrumentInfoAsync(settings.Category, settings.Symbol, cancellationToken);
        var instrumentRules = MapInstrumentRules(instrument);
        var candles = await _bybitRestClient.GetKlinesAsync(
            settings.Category,
            settings.Symbol,
            AnalysisDefaults.AutoRecommendationCandleInterval,
            AnalysisDefaults.AutoRecommendationLookbackCandles,
            cancellationToken);
        var state = await EnsureFuturesStateAsync(settings, currentPrice, cancellationToken);
        var position = _appOptions.TradingMode == TradingMode.Paper
            ? MapStateToPosition(settings, state, currentPrice)
            : new FuturesPositionSnapshot();

        if (_appOptions.TradingMode == TradingMode.Testnet)
        {
            await _preflightService.EnsureTestnetReadyAsync(settings, instrument, cancellationToken);
            var reconciliation = await _reconciliationService.ReconcileAsync(settings, state, currentPrice, cancellationToken);
            position = reconciliation.Position;
            if (reconciliation.SyncedOrderCount > 0 ||
                reconciliation.SyncedFillCount > 0 ||
                reconciliation.FixedHangingOrderCount > 0)
            {
                _logger.LogInformation(
                    "Futures reconciliation completed for {Symbol}. Open: {OpenCount}, History: {HistoryCount}, Synced orders: {SyncedCount}, Synced fills: {FillCount}, Fixed hanging: {FixedCount}",
                    settings.Symbol,
                    reconciliation.RemoteOpenOrderCount,
                    reconciliation.RemoteHistoryOrderCount,
                    reconciliation.SyncedOrderCount,
                    reconciliation.SyncedFillCount,
                    reconciliation.FixedHangingOrderCount);
            }
        }

        if (_riskOptions.EmergencyPause)
        {
            await PauseProfileAsync(state, "FUTURES_EMERGENCY_PAUSE is enabled.", cancellationToken);
            _logger.LogWarning("Futures profile paused by emergency pause. Symbol: {Symbol}", settings.Symbol);
            return;
        }

        if (state.IsPaused)
        {
            _logger.LogInformation(
                "Futures profile paused. Symbol: {Symbol}. Reason: {Reason}",
                settings.Symbol,
                state.PauseReason);
            return;
        }

        var accountRisk = await ResolveAccountRiskSnapshotAsync(settings, state, position, cancellationToken);
        var openPositionCount = await CountOpenFuturesPositionsAsync(profiles, cancellationToken);
        var decision = _strategyRouter.Decide(new FuturesStrategyContext
        {
            Settings = settings,
            Candles = candles,
            Position = position,
            CurrentPrice = currentPrice,
            Instrument = instrumentRules
        });
        foreach (var intent in decision.TradeIntents)
        {
            var strategyBlockReason = await GetStrategyEntryBlockReasonAsync(settings, position, intent, candles, cancellationToken);
            if (strategyBlockReason is not null)
            {
                await RecordStrategyFilterDecisionAsync(settings.Symbol, intent, strategyBlockReason, cancellationToken);
                _logger.LogInformation(
                    "Futures strategy intent filtered for {Symbol}. Action: {Action}. Reason: {Reason}",
                    settings.Symbol,
                    intent.Action,
                    strategyBlockReason);
                continue;
            }

            var riskDecision = _riskManager.Evaluate(new FuturesRiskEvaluationContext
            {
                RiskOptions = _riskOptions,
                Intent = intent,
                Position = position,
                MarkPrice = currentPrice,
                AvailableMarginUsdt = accountRisk.AvailableMarginUsdt,
                DailyRealizedPnl = state.DailyRealizedPnl,
                TotalRealizedPnl = state.TotalRealizedPnl,
                AccountEquityUsdt = accountRisk.AccountEquityUsdt,
                CurrentDrawdownUsdt = state.CurrentDrawdownUsdt,
                CurrentDrawdownPercent = state.CurrentDrawdownPercent,
                OpenPositionCount = openPositionCount
            });
            await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
            {
                Symbol = settings.Symbol,
                Source = "Risk",
                OrderLinkId = intent.OrderLinkId,
                Action = intent.Action,
                IsAllowed = riskDecision.IsAllowed,
                Reason = riskDecision.Reason,
                Severity = riskDecision.Severity.ToString(),
                SuggestedAction = riskDecision.SuggestedAction.ToString(),
                CreatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
            if (!riskDecision.IsAllowed)
            {
                if (riskDecision.SuggestedAction == RiskSuggestedAction.PauseBot)
                {
                    await PauseProfileAsync(state, riskDecision.Reason, cancellationToken);
                }

                _logger.LogInformation(
                    "Futures intent blocked for {Symbol}. Action: {Action}. Reason: {Reason}",
                    settings.Symbol,
                    intent.Action,
                    riskDecision.Reason);
                continue;
            }

            var result = await _executionService.ExecuteAsync(new FuturesExecutionRequest
            {
                Settings = settings,
                Intent = intent,
                Position = position,
                MarkPrice = currentPrice,
                Instrument = instrumentRules
            }, cancellationToken);

            position = result.Position;
            FuturesReconciliationService.ApplyPositionToState(state, position, _appOptions.TradingMode == TradingMode.Paper);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            await _repository.UpsertFuturesPositionAsync(position, _appOptions.TradingMode, cancellationToken);
            _logger.LogInformation(
                "Futures intent executed for {Symbol}. Action: {Action}. Paper: {IsPaper}. Message: {Message}",
                settings.Symbol,
                intent.Action,
                result.IsPaper,
                result.Message);
        }

        if (decision.TradeIntents.Count == 0)
        {
            FuturesReconciliationService.ApplyPositionToState(state, position, _appOptions.TradingMode == TradingMode.Paper);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            await _repository.UpsertFuturesPositionAsync(position, _appOptions.TradingMode, cancellationToken);
        }
    }

    private async Task<string?> GetStrategyEntryBlockReasonAsync(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        FuturesTradeIntent intent,
        IReadOnlyList<Candle> candles,
        CancellationToken cancellationToken)
    {
        if (!intent.IsPositionIncreasing || position.Size > 0m)
        {
            return null;
        }

        var atrPercent = CalculateAtrPercent(candles);
        if (_strategyQualityOptions.MaxEntryAtrPercent > 0m &&
            atrPercent > _strategyQualityOptions.MaxEntryAtrPercent)
        {
            return $"FUTURES_MAX_ENTRY_ATR_PERCENT filter blocked entry. ATR={atrPercent:F4}%.";
        }

        var cooldownReason = await GetStopLossCooldownReasonAsync(settings.Symbol, cancellationToken);
        if (cooldownReason is not null)
        {
            return cooldownReason;
        }

        if (_strategyQualityOptions.BtcRiskOffEnabled &&
            !string.Equals(settings.Symbol, "BTCUSDT", StringComparison.OrdinalIgnoreCase))
        {
            var btcRiskOffReason = await GetBtcRiskOffReasonAsync(settings.Category, cancellationToken);
            if (btcRiskOffReason is not null)
            {
                return btcRiskOffReason;
            }
        }

        return null;
    }

    private async Task<string?> GetStopLossCooldownReasonAsync(string symbol, CancellationToken cancellationToken)
    {
        if (_strategyQualityOptions.StopLossCooldownMinutes <= 0)
        {
            return null;
        }

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-_strategyQualityOptions.StopLossCooldownMinutes);
        var recentFills = await _repository.GetFuturesFillsAsync(symbol, 50, cancellationToken);
        var recentLossExit = recentFills
            .Where(fill => fill.Action is FuturesTradeAction.CloseLong or FuturesTradeAction.ReduceOnlyClose)
            .Where(fill => fill.RealizedPnl < 0m)
            .OrderByDescending(fill => fill.CreatedAt)
            .FirstOrDefault();
        return recentLossExit is not null && recentLossExit.CreatedAt >= cutoff
            ? $"FUTURES_STOP_LOSS_COOLDOWN_MINUTES active after losing exit at {recentLossExit.CreatedAt:O}."
            : null;
    }

    private async Task<string?> GetBtcRiskOffReasonAsync(string category, CancellationToken cancellationToken)
    {
        try
        {
            var btcCandles = await _bybitRestClient.GetKlinesAsync(
                category,
                "BTCUSDT",
                AnalysisDefaults.AutoRecommendationCandleInterval,
                AnalysisDefaults.AutoRecommendationLookbackCandles,
                cancellationToken);
            var movePercent = CalculateMovePercent(btcCandles);
            return movePercent <= _strategyQualityOptions.BtcRiskOffMovePercent
                ? $"FUTURES_BTC_RISK_OFF filter blocked entry. BTC move={movePercent:F4}%."
                : null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "BTC risk-off filter unavailable; continuing futures strategy evaluation.");
            return null;
        }
    }

    private Task RecordStrategyFilterDecisionAsync(
        string symbol,
        FuturesTradeIntent intent,
        string reason,
        CancellationToken cancellationToken) =>
        _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = symbol,
            Source = "StrategyFilter",
            OrderLinkId = intent.OrderLinkId,
            Action = intent.Action,
            IsAllowed = false,
            Reason = reason,
            Severity = RiskSeverity.Warning.ToString(),
            SuggestedAction = RiskSuggestedAction.BlockNewOrders.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

    private async Task<int> CountOpenFuturesPositionsAsync(
        IReadOnlyCollection<FuturesBotSettings> profiles,
        CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var profile in profiles.Where(static profile => profile.Enabled))
        {
            var position = await _repository.GetFuturesPositionAsync(profile.Symbol, cancellationToken);
            if (position?.Size > 0m)
            {
                count++;
            }
        }

        return count;
    }

    private async Task<FuturesAccountRiskSnapshot> ResolveAccountRiskSnapshotAsync(
        FuturesBotSettings settings,
        BotState state,
        FuturesPositionSnapshot position,
        CancellationToken cancellationToken)
    {
        var configuredAvailableMargin = decimal.Max(0m, settings.MaxMarginUsdt - position.MarginUsedUsdt);
        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var currentEquity = state.QuoteAssetBalance + state.UnrealizedPnl;
            return new FuturesAccountRiskSnapshot(configuredAvailableMargin, currentEquity > 0m ? currentEquity : _futuresOptions.PaperInitialEquityUsdt);
        }

        var wallet = await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, "USDT");
        var accountEquity = wallet.Coins.TryGetValue("USDT", out var usdt) && usdt.Equity > 0m
            ? usdt.Equity
            : wallet.TotalAvailableBalance;
        var availableMargin = wallet.TotalAvailableBalance > 0m
            ? decimal.Min(configuredAvailableMargin, wallet.TotalAvailableBalance)
            : configuredAvailableMargin;

        return new FuturesAccountRiskSnapshot(availableMargin, accountEquity);
    }

    private async Task PauseProfileAsync(BotState state, string reason, CancellationToken cancellationToken)
    {
        if (state.IsPaused && string.Equals(state.PauseReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        state.IsPaused = true;
        state.PauseReason = reason;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);
        await _notifier.NotifyAsync($"Futures profile paused.\nSymbol: `{state.Symbol}`\nReason: `{reason}`", cancellationToken);
    }

    private async Task<BotState> EnsureFuturesStateAsync(
        FuturesBotSettings settings,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        var stateKey = FuturesStateKeys.ForSymbol(settings.Symbol);
        var state = await _repository.GetBotStateAsync(stateKey, cancellationToken);
        if (state is not null)
        {
            ResetDailyPnlIfNeeded(state);
            state.TradingMode = _appOptions.TradingMode;
            state.LastObservedPrice = currentPrice;
            if (_appOptions.TradingMode == TradingMode.Paper && state.QuoteAssetBalance <= 0m)
            {
                state.QuoteAssetBalance = _futuresOptions.PaperInitialEquityUsdt + state.TotalRealizedPnl;
                state.PeakEquityUsdt = decimal.Max(state.PeakEquityUsdt, state.QuoteAssetBalance + state.UnrealizedPnl);
            }

            return state;
        }

        state = new BotState
        {
            Symbol = stateKey,
            TradingMode = _appOptions.TradingMode,
            LastObservedPrice = currentPrice,
            PositionSide = "None",
            Leverage = settings.Leverage,
            MarginMode = settings.MarginMode.ToString(),
            QuoteAssetBalance = _appOptions.TradingMode == TradingMode.Paper ? _futuresOptions.PaperInitialEquityUsdt : 0m,
            PeakEquityUsdt = _appOptions.TradingMode == TradingMode.Paper ? _futuresOptions.PaperInitialEquityUsdt : 0m,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repository.SaveBotStateAsync(state, cancellationToken);
        return state;
    }

    private static FuturesPositionSnapshot MapStateToPosition(
        FuturesBotSettings settings,
        BotState state,
        decimal currentPrice)
    {
        var size = state.BaseAssetQuantity;
        var entry = state.EntryPrice > 0m ? state.EntryPrice : state.AverageEntryPrice;
        var leverage = state.Leverage > 0m ? state.Leverage : settings.Leverage;
        var positionValue = size * currentPrice;
        return new FuturesPositionSnapshot
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            Side = size > 0m ? "Buy" : "None",
            Size = size,
            EntryPrice = entry,
            MarkPrice = currentPrice,
            LiquidationPrice = state.LiquidationPrice,
            PositionValueUsdt = positionValue,
            MarginUsedUsdt = leverage > 0m ? positionValue / leverage : 0m,
            Leverage = leverage,
            UnrealizedPnl = size > 0m ? (currentPrice - entry) * size : 0m,
            RealizedPnl = state.TotalRealizedPnl,
            PositionIdx = state.PositionIdx,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private void ValidateMvpSettings(FuturesBotSettings settings)
    {
        if (!string.Equals(settings.Category, "linear", StringComparison.OrdinalIgnoreCase) ||
            settings.MarginMode != FuturesMarginMode.Isolated ||
            settings.PositionMode != FuturesPositionMode.OneWay ||
            settings.Direction != FuturesDirection.LongOnly ||
            settings.Leverage > _futuresOptions.MvpMaxLeverage)
        {
            throw new InvalidOperationException("Futures worker MVP supports only linear, isolated, one-way, long-only profiles within the leverage cap.");
        }
    }

    private static void ResetDailyPnlIfNeeded(BotState state)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (state.DailyPnlDate == today)
        {
            return;
        }

        state.DailyPnlDate = today;
        state.DailyRealizedPnl = 0m;
    }

    private static FuturesInstrumentRules MapInstrumentRules(BybitInstrumentInfo instrument) => new()
    {
        TickSize = instrument.TickSize,
        QtyStep = instrument.QtyStep,
        BasePrecision = instrument.BasePrecision,
        MinOrderQty = instrument.MinOrderQty,
        MinOrderAmount = instrument.MinOrderAmount
    };

    private static decimal CalculateAtrPercent(IReadOnlyList<Candle> candles)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < 2 || ordered[^1].Close <= 0m)
        {
            return 0m;
        }

        var lookback = ordered.TakeLast(Math.Min(60, ordered.Length)).ToArray();
        var total = 0m;
        for (var index = 1; index < lookback.Length; index++)
        {
            var current = lookback[index];
            var previous = lookback[index - 1];
            total += decimal.Max(
                current.High - current.Low,
                decimal.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
        }

        var atr = total / Math.Max(1, lookback.Length - 1);
        return atr / ordered[^1].Close * 100m;
    }

    private static decimal CalculateMovePercent(IReadOnlyList<Candle> candles)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length == 0)
        {
            return 0m;
        }

        var lookback = ordered.TakeLast(Math.Min(60, ordered.Length)).ToArray();
        var first = lookback[0].Open > 0m ? lookback[0].Open : lookback[0].Close;
        var last = lookback[^1].Close;
        return first > 0m ? (last - first) / first * 100m : 0m;
    }

    private sealed record FuturesAccountRiskSnapshot(decimal AvailableMarginUsdt, decimal AccountEquityUsdt);
}
