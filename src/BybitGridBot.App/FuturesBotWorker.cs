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
    private static readonly TimeSpan UserStreamStaleThreshold = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UserStreamFallbackAlertCooldown = TimeSpan.FromMinutes(5);
    private const decimal DefaultFuturesFeeRatePercent = 0.06m;

    private readonly AppOptions _appOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly BybitUserStreamTelemetry _userStreamTelemetry;
    private readonly FuturesExecutionService _executionService;
    private readonly FuturesOptions _futuresOptions;
    private readonly FuturesPreflightService _preflightService;
    private readonly FuturesProtectionService _protectionService;
    private readonly FuturesReconciliationService _reconciliationService;
    private readonly FuturesRiskManager _riskManager;
    private readonly FuturesRiskOptions _riskOptions;
    private readonly FuturesStrategyQualityOptions _strategyQualityOptions;
    private readonly FuturesAutoConfigRecommender _recommender;
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
        BybitUserStreamTelemetry userStreamTelemetry,
        FuturesExecutionService executionService,
        FuturesPreflightService preflightService,
        FuturesProtectionService protectionService,
        FuturesReconciliationService reconciliationService,
        FuturesRiskManager riskManager,
        FuturesAutoConfigRecommender recommender,
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
        _userStreamTelemetry = userStreamTelemetry;
        _executionService = executionService;
        _preflightService = preflightService;
        _protectionService = protectionService;
        _reconciliationService = reconciliationService;
        _riskManager = riskManager;
        _recommender = recommender;
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

        if (_appOptions.TradingMode == TradingMode.Mainnet &&
            (!_futuresOptions.MainnetEnabled || !_futuresOptions.MainnetOrderPlacementEnabled))
        {
            throw new InvalidOperationException("Futures mainnet is blocked. Set FUTURES_MAINNET_ENABLED=true and FUTURES_MAINNET_ORDER_PLACEMENT_ENABLED=true only after the mainnet checklist is complete.");
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
        await RecordUserStreamFallbackIfNeededAsync(settings, cancellationToken);

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
        await UpdateEquityDrawdownAsync(state, accountRisk.AccountEquityUsdt, cancellationToken);
        var openPositionCount = await CountOpenFuturesPositionsAsync(profiles, cancellationToken);
        var recommendation = _recommender.Recommend(settings, candles, position.Size > 0m);
        settings = await TryApplyAutoRecommendationAsync(settings, recommendation, position, cancellationToken);
        await RecordPositionStrategyGuardIfNeededAsync(settings, recommendation, position, cancellationToken);
        var decision = _strategyRouter.Decide(new FuturesStrategyContext
        {
            Settings = settings,
            Candles = candles,
            Position = position,
            CurrentPrice = currentPrice,
            Instrument = instrumentRules
        });
        if (decision.TradeIntents.Count == 0)
        {
            await RecordStrategyNoTradeDecisionAsync(settings.Symbol, decision.Reason ?? "No futures entry signal.", cancellationToken);
        }

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

            var aggressiveBlockReason = await GetAggressiveEntryBlockReasonAsync(settings, position, intent, cancellationToken);
            if (aggressiveBlockReason is not null)
            {
                await RecordAggressiveGuardDecisionAsync(settings.Symbol, intent, aggressiveBlockReason, cancellationToken);
                _logger.LogInformation(
                    "Futures aggressive guard blocked intent for {Symbol}. Action: {Action}. Reason: {Reason}",
                    settings.Symbol,
                    intent.Action,
                    aggressiveBlockReason);
                continue;
            }

            var exposureNoTradeReason = GetStrategyExposureNoTradeReason(settings, position, intent);
            if (exposureNoTradeReason is not null)
            {
                await RecordStrategyNoTradeDecisionAsync(settings.Symbol, exposureNoTradeReason, cancellationToken, intent.Action);
                _logger.LogInformation(
                    "Futures strategy skipped intent for {Symbol}. Action: {Action}. Reason: {Reason}",
                    settings.Symbol,
                    intent.Action,
                    exposureNoTradeReason);
                continue;
            }

            var feeProtectionReason = GetFeeProtectedExitNoTradeReason(position, intent);
            if (feeProtectionReason is not null)
            {
                await RecordStrategyNoTradeDecisionAsync(settings.Symbol, feeProtectionReason, cancellationToken, intent.Action);
                _logger.LogInformation(
                    "Futures strategy skipped fee-negative exit for {Symbol}. Action: {Action}. Reason: {Reason}",
                    settings.Symbol,
                    intent.Action,
                    feeProtectionReason);
                continue;
            }

            var riskDecision = _riskManager.Evaluate(new FuturesRiskEvaluationContext
            {
                RiskOptions = ResolveProfileRiskOptions(settings),
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
            await ApplyPositionSnapshotToStateAsync(settings, state, position, cancellationToken);
            await _repository.UpsertFuturesPositionAsync(position, _appOptions.TradingMode, cancellationToken);
            if (position.Size > 0m)
            {
                await _protectionService.EnsureProtectiveStopAsync(settings, position, cancellationToken);
            }

            _logger.LogInformation(
                "Futures intent executed for {Symbol}. Action: {Action}. Paper: {IsPaper}. Message: {Message}",
                settings.Symbol,
                intent.Action,
                result.IsPaper,
                result.Message);
            await NotifyOrderSubmissionAsync(settings.Symbol, intent.Action, result.Order, result.Position, result.IsPaper, cancellationToken);
        }

        if (decision.TradeIntents.Count == 0)
        {
            await ApplyPositionSnapshotToStateAsync(settings, state, position, cancellationToken);
            await _repository.UpsertFuturesPositionAsync(position, _appOptions.TradingMode, cancellationToken);
        }
    }

    private async Task UpdateEquityDrawdownAsync(
        BotState state,
        decimal accountEquityUsdt,
        CancellationToken cancellationToken)
    {
        if (accountEquityUsdt <= 0m)
        {
            return;
        }

        state.PeakEquityUsdt = state.PeakEquityUsdt <= 0m
            ? accountEquityUsdt
            : decimal.Max(state.PeakEquityUsdt, accountEquityUsdt);
        state.CurrentDrawdownUsdt = decimal.Max(0m, state.PeakEquityUsdt - accountEquityUsdt);
        state.CurrentDrawdownPercent = state.PeakEquityUsdt > 0m
            ? state.CurrentDrawdownUsdt / state.PeakEquityUsdt * 100m
            : 0m;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);
    }

    private async Task<FuturesBotSettings> TryApplyAutoRecommendationAsync(
        FuturesBotSettings settings,
        FuturesAutoConfigRecommendation recommendation,
        FuturesPositionSnapshot position,
        CancellationToken cancellationToken)
    {
        if (!_futuresOptions.AutoApplyRecommendation)
        {
            return settings;
        }

        if (!FuturesAutoRecommendationSafety.CanApply(recommendation, position, AllowsShortPositions(), out var blockReason))
        {
            await RecordThrottledRiskDecisionAsync(
                settings.Symbol,
                "AutoRecommendationSkipped",
                false,
                blockReason,
                RiskSeverity.Warning,
                RiskSuggestedAction.BlockNewOrders,
                cancellationToken);
            return settings;
        }

        var recommendedSettings = BuildRecommendedSettings(settings, recommendation);
        if (!HasMeaningfulSettingsChange(settings, recommendedSettings))
        {
            return settings;
        }

        var holdReason = await GetAutoRecommendationHoldReasonAsync(settings, recommendation, cancellationToken);
        if (holdReason is not null)
        {
            await RecordThrottledRiskDecisionAsync(
                settings.Symbol,
                "AutoRecommendationSkipped",
                false,
                holdReason,
                RiskSeverity.Info,
                RiskSuggestedAction.BlockNewOrders,
                cancellationToken);
            return settings;
        }

        await _repository.SaveFuturesSettingsAsync(recommendedSettings, cancellationToken);
        var reason = $"Auto-applied futures recommendation: {recommendation.StrategyType}. {recommendation.Reason}";
        await RecordThrottledRiskDecisionAsync(
            settings.Symbol,
            "AutoRecommendationApply",
            true,
            reason,
            RiskSeverity.Info,
            RiskSuggestedAction.Allow,
            cancellationToken);
        await _notifier.NotifyAsync(
            $"Futures auto recommendation applied.\nSymbol: `{settings.Symbol}`\nStrategy: `{recommendation.StrategyType}`\nDirection: `{recommendation.Direction}`\nReason: `{recommendation.Reason}`",
            cancellationToken);
        _logger.LogInformation("Futures auto recommendation applied for {Symbol}. Strategy: {Strategy}. Reason: {Reason}",
            settings.Symbol,
            recommendation.StrategyType,
            recommendation.Reason);
        return recommendedSettings;
    }

    private async Task<string?> GetAutoRecommendationHoldReasonAsync(
        FuturesBotSettings settings,
        FuturesAutoConfigRecommendation recommendation,
        CancellationToken cancellationToken)
    {
        var recent = await _repository.GetFuturesRiskDecisionsAsync(settings.Symbol, 50, cancellationToken);
        var lastApply = recent
            .Where(decision => string.Equals(decision.Source, "AutoRecommendationApply", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(decision => decision.CreatedAt)
            .FirstOrDefault();
        var now = DateTimeOffset.UtcNow;
        var minInterval = TimeSpan.FromMinutes(Math.Max(0, _futuresOptions.AutoRecommendationMinApplyIntervalMinutes));
        if (minInterval > TimeSpan.Zero &&
            settings.UpdatedAt > now.Subtract(minInterval) &&
            (lastApply is null || settings.UpdatedAt > lastApply.CreatedAt))
        {
            return $"FUTURES_AUTO_RECOMMENDATION_MIN_APPLY_INTERVAL_MINUTES active after manual/scanner settings update at {settings.UpdatedAt:O}.";
        }

        if (lastApply is not null &&
            minInterval > TimeSpan.Zero &&
            now - lastApply.CreatedAt < minInterval)
        {
            return $"FUTURES_AUTO_RECOMMENDATION_MIN_APPLY_INTERVAL_MINUTES active. Last auto apply at {lastApply.CreatedAt:O}.";
        }

        if (!IsChurnSensitiveStrategySwitch(settings.StrategyType, recommendation.StrategyType) ||
            IsStrongRecommendation(recommendation) ||
            lastApply is null)
        {
            return null;
        }

        var hysteresisInterval = TimeSpan.FromMinutes(Math.Max(10, _futuresOptions.AutoRecommendationMinApplyIntervalMinutes * 3));
        if (now - lastApply.CreatedAt < hysteresisInterval)
        {
            return $"Futures auto recommendation hysteresis active for {settings.StrategyType}->{recommendation.StrategyType}. Last auto apply at {lastApply.CreatedAt:O}.";
        }

        return null;
    }

    private async Task RecordPositionStrategyGuardIfNeededAsync(
        FuturesBotSettings settings,
        FuturesAutoConfigRecommendation recommendation,
        FuturesPositionSnapshot position,
        CancellationToken cancellationToken)
    {
        if (position.Size <= 0m)
        {
            return;
        }

        if (!FuturesAutoRecommendationSafety.IsPositionIncompatible(settings, position))
        {
            return;
        }

        var compatible = FuturesAutoRecommendationSafety.ResolveCompatibleStrategy(settings.StrategyType, position.Side);
        var reason = $"Position side {position.Side} is incompatible with {settings.StrategyType}. Using {compatible} for this cycle. Switch to a compatible strategy or close the position first. Auto recommendation available: {recommendation.StrategyType}.";
        await RecordThrottledRiskDecisionAsync(
            settings.Symbol,
            "PositionStrategyGuard",
            true,
            reason,
            RiskSeverity.Info,
            RiskSuggestedAction.Allow,
            cancellationToken);
    }

    private async Task RecordThrottledRiskDecisionAsync(
        string symbol,
        string source,
        bool isAllowed,
        string reason,
        RiskSeverity severity,
        RiskSuggestedAction suggestedAction,
        CancellationToken cancellationToken)
    {
        var recent = await _repository.GetFuturesRiskDecisionsAsync(symbol, 20, cancellationToken);
        var duplicate = recent.Any(decision =>
            string.Equals(decision.Source, source, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(decision.Reason, reason, StringComparison.Ordinal) &&
            DateTimeOffset.UtcNow - decision.CreatedAt < TimeSpan.FromMinutes(5));
        if (duplicate)
        {
            return;
        }

        await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = symbol,
            Source = source,
            IsAllowed = isAllowed,
            Reason = reason,
            Severity = severity.ToString(),
            SuggestedAction = suggestedAction.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task NotifyOrderSubmissionAsync(
        string symbol,
        FuturesTradeAction action,
        GridOrder order,
        FuturesPositionSnapshot position,
        bool isPaper,
        CancellationToken cancellationToken)
    {
        var kind = action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort
            ? "entry"
            : "exit";
        var verb = isPaper ? "executed" : "submitted";
        await _notifier.NotifyAsync(
            $"Futures {kind} {verb}.\nSymbol: `{symbol}`\nAction: `{action}`\nQty: `{order.Quantity}`\nFilled: `{order.FilledQuantity}`\nAvg: `{order.AverageFillPrice}`\nRealized PnL: `{order.RealizedPnl}`\nPosition: `{position.Side} {position.Size}`",
            cancellationToken);
    }

    private async Task ApplyPositionSnapshotToStateAsync(
        FuturesBotSettings settings,
        BotState state,
        FuturesPositionSnapshot position,
        CancellationToken cancellationToken)
    {
        FuturesReconciliationService.ApplyPositionToState(state, position, _appOptions.TradingMode == TradingMode.Paper);
        if (_appOptions.TradingMode != TradingMode.Paper)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var fills = await _repository.GetFuturesFillsAsync(settings.Symbol, FuturesFillLedger.QueryLimit, cancellationToken);
            var ledger = FuturesFillLedger.Build(fills, today);
            state.TotalRealizedPnl = ledger.TotalRealizedPnl + ledger.TotalFunding;
            state.DailyRealizedPnl = ledger.DailyRealizedPnl + ledger.DailyFunding;
            state.DailyPnlDate = today;
            state.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _repository.SaveBotStateAsync(state, cancellationToken);
    }

    private async Task RecordUserStreamFallbackIfNeededAsync(FuturesBotSettings settings, CancellationToken cancellationToken)
    {
        if (_appOptions.TradingMode == TradingMode.Paper || !_futuresOptions.UserStreamEnabled)
        {
            return;
        }

        var snapshot = _userStreamTelemetry.GetSnapshot();
        var lastEventAge = snapshot.LastEventAt is null
            ? (TimeSpan?)null
            : DateTimeOffset.UtcNow - snapshot.LastEventAt.Value;
        var stale = !snapshot.IsConnected ||
            snapshot.LastEventAt is null ||
            lastEventAge > UserStreamStaleThreshold;
        if (!stale)
        {
            return;
        }

        var recentRiskDecisions = await _repository.GetFuturesRiskDecisionsAsync(settings.Symbol, 20, cancellationToken);
        var lastFallback = recentRiskDecisions
            .Where(decision => string.Equals(decision.Source, "UserStreamFallback", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(decision => decision.CreatedAt)
            .FirstOrDefault();
        if (lastFallback is not null &&
            DateTimeOffset.UtcNow - lastFallback.CreatedAt < UserStreamFallbackAlertCooldown)
        {
            return;
        }

        var reason = ResolveUserStreamFallbackReason(snapshot, lastEventAge);
        await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = settings.Symbol,
            Source = "UserStreamFallback",
            IsAllowed = true,
            Reason = reason,
            Severity = RiskSeverity.Warning.ToString(),
            SuggestedAction = RiskSuggestedAction.Allow.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
        await _notifier.NotifyAsync(
            $"Futures user stream fallback active.\nSymbol: `{settings.Symbol}`\nReason: `{reason}`",
            cancellationToken);
    }

    private static string ResolveUserStreamFallbackReason(
        BybitUserStreamTelemetrySnapshot snapshot,
        TimeSpan? lastEventAge)
    {
        if (!snapshot.IsConnected)
        {
            return "Futures user stream disconnected; REST reconciliation fallback is active.";
        }

        if (snapshot.LastEventAt is null)
        {
            return "Futures user stream has no handled events yet; REST reconciliation fallback is active.";
        }

        var ageSeconds = Math.Round(lastEventAge?.TotalSeconds ?? 0d);
        return $"Futures user stream stale for {ageSeconds}s; REST reconciliation fallback is active.";
    }

    private async Task<string?> GetStrategyEntryBlockReasonAsync(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        FuturesTradeIntent intent,
        IReadOnlyList<Candle> candles,
        CancellationToken cancellationToken)
    {
        if (!intent.IsPositionIncreasing)
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

    private string? GetStrategyExposureNoTradeReason(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        FuturesTradeIntent intent)
    {
        if (!intent.IsPositionIncreasing)
        {
            return null;
        }

        var maxNotional = ResolveProfileRiskOptions(settings).MaxNotionalUsdt;
        if (maxNotional <= 0m)
        {
            return null;
        }

        var currentNotional = decimal.Max(0m, position.PositionValueUsdt);
        var projectedNotional = currentNotional + intent.NotionalUsdt;
        if (projectedNotional <= maxNotional)
        {
            return null;
        }

        return $"Position is full; waiting for net-positive exit or breakout continuation. Current notional={currentNotional:F4}, next order={intent.NotionalUsdt:F4}, max={maxNotional:F4}.";
    }

    private static string? GetFeeProtectedExitNoTradeReason(
        FuturesPositionSnapshot position,
        FuturesTradeIntent intent)
    {
        if (!intent.IsReduceOnly || !IsFeeProtectedExitReason(intent.Reason))
        {
            return null;
        }

        if (position.Size <= 0m || position.EntryPrice <= 0m || intent.Price <= 0m || intent.Quantity <= 0m)
        {
            return null;
        }

        var grossPnl = intent.Action switch
        {
            FuturesTradeAction.CloseLong => (intent.Price - position.EntryPrice) * intent.Quantity,
            FuturesTradeAction.CloseShort => (position.EntryPrice - intent.Price) * intent.Quantity,
            _ => 0m
        };
        if (grossPnl <= 0m)
        {
            return null;
        }

        var entryFee = position.EntryPrice * intent.Quantity * DefaultFuturesFeeRatePercent / 100m;
        var exitFee = intent.NotionalUsdt * DefaultFuturesFeeRatePercent / 100m;
        var estimatedNetPnl = grossPnl - entryFee - exitFee;
        if (estimatedNetPnl > 0m)
        {
            return null;
        }

        return $"Fee protection blocked {intent.Reason} close because estimated net PnL after round-trip fees is not positive. Gross={grossPnl:F6}, fees={entryFee + exitFee:F6}, net={estimatedNetPnl:F6}.";
    }

    private static bool IsFeeProtectedExitReason(string reason) =>
        string.Equals(reason, "take-profit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(reason, "partial-take-profit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(reason, "trailing-profit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(reason, "exit-signal", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> GetAggressiveEntryBlockReasonAsync(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        FuturesTradeIntent intent,
        CancellationToken cancellationToken)
    {
        if (!intent.IsPositionIncreasing)
        {
            return null;
        }

        if (!settings.AggressiveModeEnabled)
        {
            return position.Size > 0m ? "Futures scale-in requires aggressive mode." : null;
        }

        if (settings.AggressiveModeKind == FuturesAggressiveModeKind.Test &&
            _appOptions.TradingMode != TradingMode.Paper)
        {
            return "Futures aggressive test mode is paper-only.";
        }

        if (position.Size > 0m && _appOptions.TradingMode != TradingMode.Paper)
        {
            return "Futures aggressive scale-in is paper-only until testnet soak validates the lifecycle.";
        }

        var fills = await _repository.GetFuturesFillsAsync(settings.Symbol, 1000, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var maxOrdersPerHour = ResolveAggressiveMaxOrdersPerHour(settings);
        if (maxOrdersPerHour > 0)
        {
            var cutoff = now.AddHours(-1);
            var entriesLastHour = fills.Count(fill =>
                fill.Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort &&
                fill.CreatedAt >= cutoff);
            if (entriesLastHour >= maxOrdersPerHour)
            {
                return "FUTURES_AGGRESSIVE_MAX_ORDERS_PER_HOUR limit reached.";
            }
        }

        var minSecondsBetweenEntries = ResolveAggressiveMinSecondsBetweenEntries(settings);
        if (minSecondsBetweenEntries > 0)
        {
            var lastEntry = fills
                .Where(fill => fill.Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort)
                .OrderByDescending(fill => fill.CreatedAt)
                .FirstOrDefault();
            if (lastEntry is not null &&
                now - lastEntry.CreatedAt < TimeSpan.FromSeconds(minSecondsBetweenEntries))
            {
                return "FUTURES_AGGRESSIVE_MIN_SECONDS_BETWEEN_ENTRIES is active.";
            }
        }

        var maxConsecutiveLosses = ResolveAggressiveMaxConsecutiveLosses(settings);
        if (maxConsecutiveLosses > 0 &&
            CountConsecutiveLosingExits(fills) >= maxConsecutiveLosses)
        {
            return "FUTURES_AGGRESSIVE_MAX_CONSECUTIVE_LOSSES limit reached.";
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
            .Where(fill => fill.Action is FuturesTradeAction.CloseLong or FuturesTradeAction.CloseShort or FuturesTradeAction.ReduceOnlyClose)
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

    private Task RecordAggressiveGuardDecisionAsync(
        string symbol,
        FuturesTradeIntent intent,
        string reason,
        CancellationToken cancellationToken) =>
        _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = symbol,
            Source = "AggressiveGuard",
            OrderLinkId = intent.OrderLinkId,
            Action = intent.Action,
            IsAllowed = false,
            Reason = reason,
            Severity = RiskSeverity.Warning.ToString(),
            SuggestedAction = RiskSuggestedAction.BlockNewOrders.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

    private async Task RecordStrategyNoTradeDecisionAsync(
        string symbol,
        string reason,
        CancellationToken cancellationToken,
        FuturesTradeAction? action = null)
    {
        var recent = await _repository.GetFuturesRiskDecisionsAsync(symbol, 20, cancellationToken);
        var reasonKey = NormalizeStrategyNoTradeReason(reason);
        var duplicate = recent.Any(decision =>
            string.Equals(decision.Source, "StrategyNoTrade", StringComparison.OrdinalIgnoreCase) &&
            decision.Action == action &&
            string.Equals(NormalizeStrategyNoTradeReason(decision.Reason), reasonKey, StringComparison.Ordinal) &&
            DateTimeOffset.UtcNow - decision.CreatedAt < TimeSpan.FromMinutes(5));
        if (duplicate)
        {
            return;
        }

        await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = symbol,
            Source = "StrategyNoTrade",
            Action = action,
            IsAllowed = false,
            Reason = reason,
            Severity = RiskSeverity.Info.ToString(),
            SuggestedAction = RiskSuggestedAction.Allow.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private static string NormalizeStrategyNoTradeReason(string reason) =>
        reason.StartsWith("Position is full;", StringComparison.Ordinal) ||
        reason.StartsWith("Position is at max notional;", StringComparison.Ordinal)
            ? "Position is full; waiting for net-positive exit or breakout continuation."
            : reason.StartsWith("Fee protection blocked ", StringComparison.Ordinal)
                ? "Fee protection blocked close because estimated net PnL after round-trip fees is not positive."
            : reason;

    private static int CountConsecutiveLosingExits(IReadOnlyCollection<FuturesFillRecord> fills)
    {
        var count = 0;
        foreach (var fill in fills.OrderByDescending(fill => fill.CreatedAt))
        {
            if (fill.Action is not (FuturesTradeAction.CloseLong or FuturesTradeAction.CloseShort or FuturesTradeAction.ReduceOnlyClose))
            {
                continue;
            }

            if (fill.RealizedPnl >= 0m)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private FuturesRiskOptions ResolveProfileRiskOptions(FuturesBotSettings settings) => new()
    {
        MaxNotionalUsdt = MinPositive(_riskOptions.MaxNotionalUsdt, settings.MaxNotionalUsdt),
        MaxMarginUsdt = MinPositive(_riskOptions.MaxMarginUsdt, settings.MaxMarginUsdt),
        MaxLeverage = _riskOptions.MaxLeverage,
        MinLiquidationBufferPercent = _riskOptions.MinLiquidationBufferPercent,
        MaxFundingCostUsdt = _riskOptions.MaxFundingCostUsdt,
        MaxDailyLossUsdt = _riskOptions.MaxDailyLossUsdt,
        MaxDailyLossEquityPercent = _riskOptions.MaxDailyLossEquityPercent,
        MaxDrawdownEquityPercent = _riskOptions.MaxDrawdownEquityPercent,
        MaxOpenPositions = _riskOptions.MaxOpenPositions,
        AggressiveMaxOrdersPerHour = ResolveAggressiveMaxOrdersPerHour(settings),
        AggressiveMinSecondsBetweenEntries = ResolveAggressiveMinSecondsBetweenEntries(settings),
        AggressiveMaxConsecutiveLosses = ResolveAggressiveMaxConsecutiveLosses(settings),
        EmergencyPause = _riskOptions.EmergencyPause,
        StopLossRequired = _riskOptions.StopLossRequired
    };

    private int ResolveAggressiveMaxOrdersPerHour(FuturesBotSettings settings) =>
        settings.AggressiveMaxOrdersPerHour >= 0 ? settings.AggressiveMaxOrdersPerHour : _riskOptions.AggressiveMaxOrdersPerHour;

    private int ResolveAggressiveMinSecondsBetweenEntries(FuturesBotSettings settings) =>
        settings.AggressiveMinSecondsBetweenEntries >= 0 ? settings.AggressiveMinSecondsBetweenEntries : _riskOptions.AggressiveMinSecondsBetweenEntries;

    private int ResolveAggressiveMaxConsecutiveLosses(FuturesBotSettings settings) =>
        settings.AggressiveMaxConsecutiveLosses >= 0 ? settings.AggressiveMaxConsecutiveLosses : _riskOptions.AggressiveMaxConsecutiveLosses;

    private static decimal MinPositive(decimal left, decimal right)
    {
        if (left <= 0m)
        {
            return Math.Max(0m, right);
        }

        if (right <= 0m)
        {
            return left;
        }

        return Math.Min(left, right);
    }

    private async Task<int> CountOpenFuturesPositionsAsync(
        IReadOnlyCollection<FuturesBotSettings> profiles,
        CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var profile in profiles.Where(static profile => profile.Enabled))
        {
            if (await HasOpenFuturesPositionAsync(profile, cancellationToken))
            {
                count++;
            }
        }

        return count;
    }

    private async Task<bool> HasOpenFuturesPositionAsync(
        FuturesBotSettings profile,
        CancellationToken cancellationToken)
    {
        var storedPosition = await _repository.GetFuturesPositionAsync(profile.Symbol, cancellationToken);
        var storedOpen = storedPosition?.Size > 0m;
        if (_appOptions.TradingMode != TradingMode.Paper)
        {
            return storedOpen;
        }

        var state = await _repository.GetBotStateAsync(FuturesStateKeys.ForSymbol(profile.Symbol), cancellationToken);
        if (state?.BaseAssetQuantity > 0m)
        {
            return true;
        }

        return storedOpen && state is null;
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
        var side = size > 0m ? state.PositionSide ?? "Buy" : "None";
        var unrealizedPnl = size > 0m && entry > 0m
            ? CalculateUnrealizedPnl(side, size, entry, currentPrice)
            : 0m;
        return new FuturesPositionSnapshot
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            Side = side,
            Size = size,
            EntryPrice = entry,
            MarkPrice = currentPrice,
            LiquidationPrice = state.LiquidationPrice,
            PositionValueUsdt = positionValue,
            MarginUsedUsdt = leverage > 0m ? positionValue / leverage : 0m,
            Leverage = leverage,
            UnrealizedPnl = unrealizedPnl,
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
            (settings.Direction != FuturesDirection.LongOnly && !AllowsShortPositions()) ||
            settings.Leverage > _futuresOptions.MvpMaxLeverage)
        {
            throw new InvalidOperationException("Futures worker supports shorts only in paper mode or with FUTURES_TESTNET_SHORTS_ENABLED=true on testnet; live remains linear, isolated, one-way, long-only within the leverage cap.");
        }
    }

    private bool AllowsShortPositions() =>
        _appOptions.TradingMode == TradingMode.Paper ||
        (_appOptions.TradingMode == TradingMode.Testnet && _futuresOptions.TestnetShortsEnabled);

    private static FuturesBotSettings BuildRecommendedSettings(
        FuturesBotSettings currentSettings,
        FuturesAutoConfigRecommendation recommendation) => new()
    {
        Enabled = currentSettings.Enabled,
        Symbol = currentSettings.Symbol,
        Category = currentSettings.Category,
        StrategyType = recommendation.StrategyType,
        StrategyConfigJson = recommendation.StrategyConfigJson,
        Leverage = recommendation.Leverage,
        MarginMode = recommendation.MarginMode,
        PositionMode = recommendation.PositionMode,
        Direction = recommendation.Direction,
        MaxNotionalUsdt = recommendation.MaxNotionalUsdt,
        MaxMarginUsdt = recommendation.MaxMarginUsdt,
        StopLossPercent = recommendation.StopLossPercent,
        TakeProfitPercent = recommendation.TakeProfitPercent,
        LiquidationBufferPercent = recommendation.LiquidationBufferPercent,
        ReduceOnlyEnabled = recommendation.ReduceOnlyEnabled,
        AggressiveModeEnabled = currentSettings.AggressiveModeEnabled,
        AggressiveModeKind = currentSettings.AggressiveModeKind,
        AggressiveEntryMultiplier = currentSettings.AggressiveEntryMultiplier,
        AggressiveMaxOrdersPerHour = currentSettings.AggressiveMaxOrdersPerHour,
        AggressiveMinSecondsBetweenEntries = currentSettings.AggressiveMinSecondsBetweenEntries,
        AggressiveMaxConsecutiveLosses = currentSettings.AggressiveMaxConsecutiveLosses,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static bool HasMeaningfulSettingsChange(FuturesBotSettings current, FuturesBotSettings updated) =>
        current.StrategyType != updated.StrategyType ||
        current.Leverage != updated.Leverage ||
        current.Direction != updated.Direction ||
        current.MaxNotionalUsdt != updated.MaxNotionalUsdt ||
        current.MaxMarginUsdt != updated.MaxMarginUsdt ||
        current.StopLossPercent != updated.StopLossPercent ||
        current.TakeProfitPercent != updated.TakeProfitPercent ||
        current.LiquidationBufferPercent != updated.LiquidationBufferPercent;

    private static bool IsChurnSensitiveStrategySwitch(
        FuturesStrategyType current,
        FuturesStrategyType recommended) =>
        current != recommended &&
        (IsLongChurnFamily(current) && IsLongChurnFamily(recommended) ||
            IsShortChurnFamily(current) && IsShortChurnFamily(recommended));

    private static bool IsLongChurnFamily(FuturesStrategyType strategyType) =>
        strategyType is FuturesStrategyType.GridLongOnly or FuturesStrategyType.TrendFollow or FuturesStrategyType.Breakout;

    private static bool IsShortChurnFamily(FuturesStrategyType strategyType) =>
        strategyType is FuturesStrategyType.GridShortOnly or FuturesStrategyType.TrendFollowShortOnly or FuturesStrategyType.BreakdownShort;

    private static bool IsStrongRecommendation(FuturesAutoConfigRecommendation recommendation)
    {
        var move = Math.Abs(recommendation.Metrics.MovePercent);
        return recommendation.StrategyType switch
        {
            FuturesStrategyType.TrendFollow or FuturesStrategyType.TrendFollowShortOnly => move >= 1.8m,
            FuturesStrategyType.Breakout or FuturesStrategyType.BreakdownShort => move >= 1.2m,
            _ => false
        };
    }

    private static decimal CalculateUnrealizedPnl(string side, decimal size, decimal entryPrice, decimal currentPrice) =>
        IsShort(side)
            ? (entryPrice - currentPrice) * size
            : (currentPrice - entryPrice) * size;

    private static bool IsShort(string side) =>
        string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Short", StringComparison.OrdinalIgnoreCase);

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
