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
    private readonly FuturesStrategyRouter _strategyRouter;
    private readonly ILogger<FuturesBotWorker> _logger;
    private readonly ITelegramNotifier _notifier;
    private readonly IGridRepository _repository;

    public FuturesBotWorker(
        IOptions<AppOptions> appOptions,
        IOptions<FuturesOptions> futuresOptions,
        IOptions<FuturesRiskOptions> riskOptions,
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
                    await RunProfileCycleAsync(profile, stoppingToken);
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

    private async Task RunProfileCycleAsync(FuturesBotSettings settings, CancellationToken cancellationToken)
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
            if (reconciliation.SyncedOrderCount > 0 || reconciliation.FixedHangingOrderCount > 0)
            {
                _logger.LogInformation(
                    "Futures reconciliation completed for {Symbol}. Open: {OpenCount}, History: {HistoryCount}, Synced: {SyncedCount}, Fixed hanging: {FixedCount}",
                    settings.Symbol,
                    reconciliation.RemoteOpenOrderCount,
                    reconciliation.RemoteHistoryOrderCount,
                    reconciliation.SyncedOrderCount,
                    reconciliation.FixedHangingOrderCount);
            }
        }

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
            var riskDecision = _riskManager.Evaluate(new FuturesRiskEvaluationContext
            {
                RiskOptions = _riskOptions,
                Intent = intent,
                Position = position,
                MarkPrice = currentPrice,
                AvailableMarginUsdt = decimal.Max(0m, settings.MaxMarginUsdt - position.MarginUsedUsdt),
                DailyRealizedPnl = state.DailyRealizedPnl,
                MaxDailyLossUsdt = 20m
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
            FuturesReconciliationService.ApplyPositionToState(state, position);
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
            FuturesReconciliationService.ApplyPositionToState(state, position);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            await _repository.UpsertFuturesPositionAsync(position, _appOptions.TradingMode, cancellationToken);
        }
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
}
