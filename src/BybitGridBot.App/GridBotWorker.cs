using System.Text.Json;
using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Notifications;
using BybitGridBot.Risk;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StrategyExecutionDecision = BybitGridBot.Strategy.StrategyDecision;

namespace BybitGridBot.App;

public sealed class GridBotWorker : BackgroundService
{
    private const string BtdEntryMarker = "btd-entry";
    private const string DcaEntryMarker = "dca-entry";
    private const string SignalEntryMarker = "signal-entry";
    private const string SignalExitMarker = "signal-exit";
    private const string TrendEntryMarker = "trend-entry";
    private const string TrendExitMarker = "trend-exit";
    private const string ReduceOnlyExitMarker = "reduce-only-exit";
    private const string GridSource = "Grid";
    private const string DcaSource = "DCA";
    private const string BtdSource = "BTD";
    private const string SignalSource = "Signal";
    private const string TrendSource = "Trend";
    private const string ReduceOnlySource = "ReduceOnly";
    private const string ComboGridSource = "Combo-Grid";
    private const string ComboDcaSource = "Combo-DCA";
    private const string ComboBtdSource = "Combo-BTD";
    private const string HybridGridSource = "Hybrid-Grid";
    private const string HybridDcaSource = "Hybrid-DCA";
    private const string HybridBtdSource = "Hybrid-BTD";
    private const decimal SignalMarketLikeLimitBufferPercent = 0.05m;
    private const string ReduceOnlyReasonDanger = "danger-regime";
    private const string ReduceOnlyReasonTrailing = "trailing-protection";
    private static readonly TimeSpan TimedAutoRecommendationApplyInterval = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions StrategyJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppOptions _appOptions;
    private readonly AutoStrategySelector _autoStrategySelector;
    private readonly BtdStrategy _btdStrategy;
    private readonly BybitOptions _bybitOptions;
    private readonly DcaStrategy _dcaStrategy;
    private readonly FuturesOptions _futuresOptions;
    private readonly GridOptions _defaultGridOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly ExpectedProfitFilter _expectedProfitFilter;
    private readonly ILogger<GridBotWorker> _logger;
    private readonly MarketRegimeAnalyzer _marketRegimeAnalyzer;
    private readonly MarketRegimeFilter _marketRegimeFilter;
    private readonly PriceActionPhaseDetector _priceActionPhaseDetector;
    private readonly BigRedCandleGuard _bigRedCandleGuard;
    private readonly ITelegramNotifier _notifier;
    private readonly IGridRepository _repository;
    private readonly RiskManager _riskManager;
    private readonly RiskOptions _riskOptions;
    private readonly SignalAnalyzer _signalAnalyzer;
    private readonly IGridTradingStrategy _strategy;
    private readonly Dictionary<string, CachedFeeRate> _feeRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastTimedAutoApplyChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GridOptions> _runningProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GridBotSettings> _runningSettings = new(StringComparer.OrdinalIgnoreCase);
    private GridOptions _gridOptions;
    private string _baseAsset;
    private string _quoteAsset;

    private readonly record struct SignalPositionSnapshot(decimal Quantity, decimal AverageEntryPrice);

    private readonly record struct TrendFollowingSignal(
        bool ShouldBuy,
        bool ShouldExit,
        string ExitReason,
        decimal TrendStrength,
        decimal Resistance,
        decimal VolumeRatio,
        decimal PullbackPercent);

    public GridBotWorker(
        IOptions<AppOptions> appOptions,
        IOptions<BybitOptions> bybitOptions,
        IOptions<FuturesOptions> futuresOptions,
        IOptions<GridOptions> gridOptions,
        IOptions<RiskOptions> riskOptions,
        AutoStrategySelector autoStrategySelector,
        IBybitRestClient bybitRestClient,
        BtdStrategy btdStrategy,
        DcaStrategy dcaStrategy,
        ExpectedProfitFilter expectedProfitFilter,
        IGridTradingStrategy strategy,
        RiskManager riskManager,
        MarketRegimeAnalyzer marketRegimeAnalyzer,
        MarketRegimeFilter marketRegimeFilter,
        PriceActionPhaseDetector priceActionPhaseDetector,
        BigRedCandleGuard bigRedCandleGuard,
        SignalAnalyzer signalAnalyzer,
        IGridRepository repository,
        ITelegramNotifier notifier,
        ILogger<GridBotWorker> logger)
    {
        _appOptions = appOptions.Value;
        _autoStrategySelector = autoStrategySelector;
        _bybitOptions = bybitOptions.Value;
        _futuresOptions = futuresOptions.Value;
        _defaultGridOptions = gridOptions.Value;
        _gridOptions = _defaultGridOptions;
        _riskOptions = riskOptions.Value;
        _bybitRestClient = bybitRestClient;
        _expectedProfitFilter = expectedProfitFilter;
        _btdStrategy = btdStrategy;
        _dcaStrategy = dcaStrategy;
        _strategy = strategy;
        _riskManager = riskManager;
        _marketRegimeAnalyzer = marketRegimeAnalyzer;
        _marketRegimeFilter = marketRegimeFilter;
        _priceActionPhaseDetector = priceActionPhaseDetector;
        _bigRedCandleGuard = bigRedCandleGuard;
        _signalAnalyzer = signalAnalyzer;
        _repository = repository;
        _notifier = notifier;
        _logger = logger;
        (_baseAsset, _quoteAsset) = ResolveAssets(_gridOptions.Symbol);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _repository.InitializeAsync(stoppingToken);
        ValidateAccountConfiguration();

        _logger.LogInformation("Starting Bybit grid bot. Mode: {TradingMode}", _appOptions.TradingMode);
        if (_appOptions.TradingMode == TradingMode.Mainnet)
        {
            _logger.LogWarning("MAINNET MODE ENABLED. Real orders can be submitted to Bybit.");
        }

        await _notifier.NotifyAsync(
            $"Bybit grid bot started.\nMode: `{_appOptions.TradingMode}`",
            stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_defaultGridOptions.BotLoopIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var profiles = await EnsureRuntimeGridProfilesAsync(stoppingToken);
                await CancelRemovedProfilesAsync(profiles, stoppingToken);

                foreach (var profile in profiles)
                {
                    try
                    {
                        await RunProfileCycleAsync(profile, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (BybitApiException exception)
                    {
                        _logger.LogError(exception, "Bybit API error for {Symbol} {RetCode}: {RetMsg}", profile.Symbol, exception.RetCode, exception.RetMsg);
                        await _notifier.NotifyAsync($"Bybit API error for `{profile.Symbol}`: `{exception.RetCode}` {exception.RetMsg}", stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Unhandled bot loop error for {Symbol}.", profile.Symbol);
                        await _notifier.NotifyAsync($"Bot loop error for `{profile.Symbol}`: `{exception.Message}`", stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (BybitApiException exception)
            {
                _logger.LogError(exception, "Bybit API error {RetCode}: {RetMsg}", exception.RetCode, exception.RetMsg);
                await _notifier.NotifyAsync($"Bybit API error: `{exception.RetCode}` {exception.RetMsg}", stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unhandled bot loop error.");
                await _notifier.NotifyAsync($"Bot loop error: `{exception.Message}`", stoppingToken);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _notifier.NotifyAsync("Bybit grid bot stopped.", cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<GridBotSettings>> EnsureRuntimeGridProfilesAsync(CancellationToken cancellationToken)
    {
        var persisted = await _repository.GetRuntimeSettingsProfilesAsync(cancellationToken);
        if (persisted.Count > 0)
        {
            return persisted;
        }

        var defaultSettings = RuntimeGridOptionsFactory.ToRuntimeSettings(_defaultGridOptions);
        await _repository.SaveRuntimeSettingsAsync(defaultSettings, cancellationToken);
        return [defaultSettings];
    }

    private async Task CancelRemovedProfilesAsync(IReadOnlyCollection<GridBotSettings> profiles, CancellationToken cancellationToken)
    {
        var activeSymbols = profiles.Select(profile => profile.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedSymbols = _runningProfiles.Keys.Where(symbol => !activeSymbols.Contains(symbol)).ToArray();

        foreach (var symbol in removedSymbols)
        {
            _gridOptions = _runningProfiles[symbol];
            (_baseAsset, _quoteAsset) = ResolveAssets(_gridOptions.Symbol);

            var activeOrders = await _repository.GetActiveOrdersAsync(symbol, cancellationToken);
            foreach (var order in activeOrders)
            {
                await CancelManagedOrderAsync(order, cancellationToken);
            }

            _runningProfiles.Remove(symbol);
            _runningSettings.Remove(symbol);
            _lastTimedAutoApplyChecks.Remove(symbol);
            _logger.LogInformation("Runtime grid profile removed. Symbol: {Symbol}", symbol);
            await _notifier.NotifyAsync($"Runtime profile removed. Symbol: `{symbol}`. Active orders cancelled.", cancellationToken);
        }
    }

    private async Task RunProfileCycleAsync(GridBotSettings profile, CancellationToken cancellationToken)
    {
        profile = await TryApplyTimedAutoRecommendationAsync(profile, cancellationToken);
        var refreshedGridOptions = RuntimeGridOptionsFactory.ToGridOptions(profile, _defaultGridOptions);
        var isKnownProfile = _runningProfiles.TryGetValue(refreshedGridOptions.Symbol, out var previousGridOptions);
        _runningSettings.TryGetValue(refreshedGridOptions.Symbol, out var previousSettings);

        _gridOptions = refreshedGridOptions;
        (_baseAsset, _quoteAsset) = ResolveAssets(_gridOptions.Symbol);
        ValidateStartupConfiguration();

        if (isKnownProfile &&
            (previousGridOptions is null ||
             previousSettings is null ||
             !RuntimeGridOptionsFactory.IsSameTradingConfiguration(previousSettings, profile)))
        {
            await ReconcileActiveOrdersForRuntimeSettingsChangeAsync(profile, cancellationToken);

            _logger.LogInformation(
                "Runtime grid settings updated. Symbol: {Symbol}, Range: {LowerPrice}-{UpperPrice}, Step: {Step}",
                _gridOptions.Symbol,
                _gridOptions.LowerPrice,
                _gridOptions.UpperPrice,
                _gridOptions.Step);

            await _notifier.NotifyAsync(
                $"Runtime settings updated.\nSymbol: `{_gridOptions.Symbol}`\nStrategy: `{profile.StrategyType}`\nRange: `{_gridOptions.LowerPrice}`-`{_gridOptions.UpperPrice}`\nStep: `{_gridOptions.Step}`",
                cancellationToken);
        }
        else if (!isKnownProfile)
        {
            _logger.LogInformation("Runtime grid profile activated. Symbol: {Symbol}", _gridOptions.Symbol);
            await _notifier.NotifyAsync($"Runtime profile activated. Symbol: `{_gridOptions.Symbol}`", cancellationToken);
        }

        _runningProfiles[_gridOptions.Symbol] = _gridOptions;
        _runningSettings[_gridOptions.Symbol] = profile;

        _logger.LogInformation(
            "Selected strategy for {Symbol}: {StrategyType}. SelectionMode: {SelectionMode}",
            _gridOptions.Symbol,
            profile.StrategyType,
            profile.StrategySelectionMode);

        var instrument = await _bybitRestClient.GetInstrumentInfoAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var state = await EnsureBotStateAsync(cancellationToken);
        if (profile.StrategyType == TradingStrategyType.ReduceOnly)
        {
            await RunReduceOnlyCycleAsync(profile, state, instrument, cancellationToken);
            return;
        }

        if (profile.StrategyType is TradingStrategyType.NoTrade or TradingStrategyType.Pause)
        {
            await _repository.SaveGridLevelsAsync(_gridOptions.Symbol, [], cancellationToken);
            await RunNoTradeCycleAsync(state, cancellationToken);
            return;
        }

        if (profile.StrategyType == TradingStrategyType.Dca)
        {
            await _repository.SaveGridLevelsAsync(_gridOptions.Symbol, [], cancellationToken);
            await RunDcaCycleAsync(profile, state, instrument, cancellationToken);
            return;
        }

        if (profile.StrategyType == TradingStrategyType.Btd)
        {
            await _repository.SaveGridLevelsAsync(_gridOptions.Symbol, [], cancellationToken);
            await RunBtdCycleAsync(profile, state, instrument, cancellationToken);
            return;
        }

        if (profile.StrategyType == TradingStrategyType.Signal)
        {
            await _repository.SaveGridLevelsAsync(_gridOptions.Symbol, [], cancellationToken);
            await RunSignalCycleAsync(profile, state, instrument, cancellationToken);
            return;
        }

        if (profile.StrategyType is TradingStrategyType.TrendFollow or TradingStrategyType.TrendFollowing or TradingStrategyType.Breakout)
        {
            await _repository.SaveGridLevelsAsync(_gridOptions.Symbol, [], cancellationToken);
            await RunTrendFollowCycleAsync(profile, state, instrument, cancellationToken);
            return;
        }

        var levels = await EnsureGridLevelsAsync(cancellationToken);
        if (profile.StrategyType == TradingStrategyType.Combo)
        {
            await RunComboCycleAsync(profile, state, levels, instrument, cancellationToken);
            return;
        }

        if (profile.StrategyType == TradingStrategyType.Hybrid)
        {
            await RunHybridCycleAsync(profile, state, levels, instrument, cancellationToken);
            return;
        }

        await RunCycleAsync(state, levels, instrument, profile, cancellationToken);
    }

    private async Task ReconcileActiveOrdersForRuntimeSettingsChangeAsync(
        GridBotSettings newProfile,
        CancellationToken cancellationToken)
    {
        var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        if (!ShouldPreserveProfitableSellOrdersOnAutoSwitch(newProfile))
        {
            foreach (var order in activeOrders)
            {
                await CancelManagedOrderAsync(order, cancellationToken);
            }

            return;
        }

        var state = await _repository.GetBotStateAsync(_gridOptions.Symbol, cancellationToken);
        foreach (var order in activeOrders)
        {
            if (state is not null &&
                await ShouldPreserveProfitableSellOrderAsync(newProfile, state, order, cancellationToken))
            {
                _logger.LogInformation(
                    "Preserved profitable sell order {OrderLinkId} during auto switch to {StrategyType}. Price: {Price}, average entry: {AverageEntryPrice}",
                    order.OrderLinkId,
                    newProfile.StrategyType,
                    order.Price,
                    state.AverageEntryPrice);
                continue;
            }

            await CancelManagedOrderAsync(order, cancellationToken);
        }
    }

    private static bool ShouldPreserveProfitableSellOrdersOnAutoSwitch(GridBotSettings newProfile)
    {
        return newProfile.StrategySelectionMode == StrategySelectionMode.Auto;
    }

    private async Task<bool> ShouldPreserveProfitableSellOrderAsync(
        GridBotSettings? profile,
        BotState state,
        GridOrder order,
        CancellationToken cancellationToken)
    {
        if (!order.IsActive ||
            order.Side != TradeSide.Sell ||
            order.Quantity <= order.FilledQuantity ||
            state.AverageEntryPrice <= 0m ||
            order.Price <= state.AverageEntryPrice)
        {
            return false;
        }

        return await HasMinimumNetProfitForOrderAsync(
            profile,
            state,
            order,
            order.Quantity - order.FilledQuantity,
            cancellationToken);
    }

    private async Task<GridBotSettings> TryApplyTimedAutoRecommendationAsync(
        GridBotSettings profile,
        CancellationToken cancellationToken)
    {
        if (profile.StrategySelectionMode != StrategySelectionMode.Auto ||
            !ShouldRunTimedAutoApplyCheck(profile, DateTimeOffset.UtcNow))
        {
            return profile;
        }

        _lastTimedAutoApplyChecks[profile.Symbol] = DateTimeOffset.UtcNow;
        var gridOptions = RuntimeGridOptionsFactory.ToGridOptions(profile, _defaultGridOptions);
        var candles = await _bybitRestClient.GetKlinesAsync(
            gridOptions.Category,
            gridOptions.Symbol,
            AnalysisDefaults.AutoRecommendationCandleInterval,
            AnalysisDefaults.AutoRecommendationLookbackCandles,
            cancellationToken);
        if (candles.Count == 0)
        {
            _logger.LogInformation("Timed auto-apply skipped for {Symbol}: market data is unavailable.", profile.Symbol);
            return profile;
        }

        var regime = _marketRegimeAnalyzer.Analyze(candles);
        _logger.LogInformation(
            "Strategy scores for {Symbol}: {StrategyScores}",
            profile.Symbol,
            FormatStrategyScoresForLog(regime));
        var recommendation = _autoStrategySelector.Recommend(gridOptions, regime, candles);
        var state = await _repository.GetBotStateAsync(profile.Symbol, cancellationToken);
        var recommendedSettings = new GridBotSettings
        {
            Symbol = profile.Symbol,
            Category = profile.Category,
            StrategySelectionMode = StrategySelectionMode.Auto,
            StrategyType = recommendation.StrategyType,
            StrategyConfigJson = recommendation.StrategyConfigJson,
            LowerPrice = recommendation.LowerPrice,
            UpperPrice = recommendation.UpperPrice,
            Step = recommendation.Step,
            OrderSizeUsdt = recommendation.OrderSizeUsdt,
            StopLowerPrice = recommendation.StopLowerPrice,
            StopUpperPrice = recommendation.StopUpperPrice,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        recommendedSettings = UseReduceOnlyWhenNoTradeWouldLeavePosition(recommendedSettings, state);

        var activeOrders = await _repository.GetActiveOrdersAsync(profile.Symbol, cancellationToken);
        var safetyErrors = AutoRecommendationApplySafety.Validate(
            profile,
            state,
            activeOrders,
            recommendation,
            recommendedSettings,
            _riskOptions,
            _strategy);
        if (safetyErrors.Count > 0)
        {
            _logger.LogInformation(
                "Timed auto-apply skipped for {Symbol}: {Reasons}",
                profile.Symbol,
                string.Join(" | ", safetyErrors));
            return profile;
        }

        await _repository.SaveRuntimeSettingsAsync(recommendedSettings, cancellationToken);
        _logger.LogInformation(
            "Timed auto-apply updated {Symbol}. Strategy: {Strategy}, Range: {LowerPrice}-{UpperPrice}, Step: {Step}, Order: {OrderSizeUsdt}",
            recommendedSettings.Symbol,
            recommendedSettings.StrategyType,
            recommendedSettings.LowerPrice,
            recommendedSettings.UpperPrice,
            recommendedSettings.Step,
            recommendedSettings.OrderSizeUsdt);
        await _notifier.NotifyAsync(
            $"Timed auto recommendation applied.\nSymbol: `{recommendedSettings.Symbol}`\nStrategy: `{recommendedSettings.StrategyType}`\nRange: `{recommendedSettings.LowerPrice}`-`{recommendedSettings.UpperPrice}`\nStep: `{recommendedSettings.Step}`\nOrder: `{recommendedSettings.OrderSizeUsdt}` USDT",
            cancellationToken);

        return recommendedSettings;
    }

    private static GridBotSettings UseReduceOnlyWhenNoTradeWouldLeavePosition(
        GridBotSettings settings,
        BotState? state)
    {
        if (settings.StrategyType != TradingStrategyType.NoTrade ||
            state is null ||
            state.BaseAssetQuantity <= 0m)
        {
            return settings;
        }

        return new GridBotSettings
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            StrategySelectionMode = settings.StrategySelectionMode,
            StrategyType = TradingStrategyType.ReduceOnly,
            StrategyConfigJson = settings.StrategyConfigJson,
            LowerPrice = settings.LowerPrice,
            UpperPrice = settings.UpperPrice,
            Step = settings.Step,
            OrderSizeUsdt = settings.OrderSizeUsdt,
            StopLowerPrice = settings.StopLowerPrice,
            StopUpperPrice = settings.StopUpperPrice,
            UpdatedAt = settings.UpdatedAt
        };
    }

    private bool ShouldRunTimedAutoApplyCheck(GridBotSettings profile, DateTimeOffset now)
    {
        var lastCheck = profile.UpdatedAt;
        if (_lastTimedAutoApplyChecks.TryGetValue(profile.Symbol, out var cachedCheck) &&
            cachedCheck > lastCheck)
        {
            lastCheck = cachedCheck;
        }

        return now - lastCheck >= TimedAutoRecommendationApplyInterval;
    }

    private static string FormatStrategyScoresForLog(MarketRegimeAnalysis regime)
    {
        var grid = regime.Regime == MarketRegimeType.Range ? 80 : 35;
        var btd = regime.Regime == MarketRegimeType.Trend && regime.MovePercent < 0m ? 70 : 40;
        var breakout = regime.Regime == MarketRegimeType.Breakout ? 82 : 25;
        var trend = regime.Regime == MarketRegimeType.Trend && regime.MovePercent > 0m ? 78 : 30;
        var pause = regime.Regime == MarketRegimeType.Danger ? 100 : 50;

        return $"Grid={grid}; BTD={btd}; Breakout={breakout}; Trend={trend}; Pause={pause}; regime={regime.Regime}; confidence={regime.Confidence}";
    }

    private async Task<BotState> RunNoTradeCycleAsync(
        BotState state,
        CancellationToken cancellationToken)
    {
        ResetDailyPnlIfNeeded(state);

        var ticker = await _bybitRestClient.GetTickerAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var currentPrice = ticker.LastPrice;
        state.LastObservedPrice = currentPrice;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation("NoTrade mode active for {Symbol}. Current price: {Price}. No new orders will be created.", _gridOptions.Symbol, currentPrice);

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            await SimulatePaperFillsAsync(state, [], null, currentPrice, cancellationToken);
        }
        else
        {
            await SynchronizeLiveOrdersAsync(state, [], null, cancellationToken);
        }

        state.IsInitialized = true;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);
        return state;
    }

    private async Task<BotState> RunReduceOnlyCycleAsync(
        GridBotSettings profile,
        BotState state,
        BybitInstrumentInfo instrument,
        CancellationToken cancellationToken)
    {
        ResetDailyPnlIfNeeded(state);

        var ticker = await _bybitRestClient.GetTickerAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var currentPrice = ticker.LastPrice;
        state.LastObservedPrice = currentPrice;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        var levels = await EnsureGridLevelsAsync(cancellationToken);
        _logger.LogInformation("ReduceOnly mode active for {Symbol}. Current price: {Price}. Buy orders are blocked.", _gridOptions.Symbol, currentPrice);

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            await ApplyReduceOnlyProtectionAsync(profile, state, levels, instrument, activeOrders, currentPrice, "manual-reduce-only", cancellationToken);
            await SimulatePaperFillsAsync(state, levels, profile, currentPrice, cancellationToken);
        }
        else
        {
            await SynchronizeLiveOrdersAsync(state, levels, profile, cancellationToken);
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            await ApplyReduceOnlyProtectionAsync(profile, state, levels, instrument, activeOrders, currentPrice, "manual-reduce-only", cancellationToken);
        }

        state.IsInitialized = true;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);
        return state;
    }

    private async Task<BotState> FinishProtectiveReduceOnlyCycleAsync(
        GridBotSettings? profile,
        BotState state,
        IReadOnlyList<GridLevel> levels,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        var reduceOnlyProfile = BuildTransientReduceOnlyProfile(profile);
        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            await SimulatePaperFillsAsync(state, levels, reduceOnlyProfile, currentPrice, cancellationToken);
        }
        else
        {
            await SynchronizeLiveOrdersAsync(state, levels, reduceOnlyProfile, cancellationToken);
        }

        state.IsInitialized = true;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);
        return state;
    }

    private GridBotSettings BuildTransientReduceOnlyProfile(GridBotSettings? profile) => new()
    {
        Symbol = profile?.Symbol ?? _gridOptions.Symbol,
        Category = profile?.Category ?? _gridOptions.Category,
        StrategySelectionMode = profile?.StrategySelectionMode ?? StrategySelectionMode.Manual,
        StrategyType = TradingStrategyType.ReduceOnly,
        StrategyConfigJson = profile?.StrategyConfigJson ?? "{}",
        LowerPrice = _gridOptions.LowerPrice,
        UpperPrice = _gridOptions.UpperPrice,
        Step = _gridOptions.Step,
        OrderSizeUsdt = _gridOptions.OrderSizeUsdt,
        StopLowerPrice = _gridOptions.StopLowerPrice,
        StopUpperPrice = _gridOptions.StopUpperPrice,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private async Task<BotState> RunDcaCycleAsync(
        GridBotSettings profile,
        BotState state,
        BybitInstrumentInfo instrument,
        CancellationToken cancellationToken)
    {
        ResetDailyPnlIfNeeded(state);

        var config = ParseDcaStrategyConfig(profile);
        var ticker = await _bybitRestClient.GetTickerAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var currentPrice = ticker.LastPrice;
        state.LastObservedPrice = currentPrice;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation("Current price for {Symbol}: {Price}", _gridOptions.Symbol, currentPrice);

        if (await TryApplyProtectiveReduceOnlyFromFreshMarketAsync(
                profile,
                state,
                [],
                instrument,
                currentPrice,
                cancellationToken))
        {
            return await FinishProtectiveReduceOnlyCycleAsync(profile, state, [], currentPrice, cancellationToken);
        }

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(profile, state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanDcaActiveOrdersAsync(state, activeOrders, cancellationToken);
            await SimulatePaperFillsAsync(state, [], profile, currentPrice, cancellationToken);
        }
        else
        {
            await SynchronizeLiveOrdersAsync(state, [], profile, cancellationToken);
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(profile, state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanDcaActiveOrdersAsync(state, activeOrders, cancellationToken);
        }

        if (state.IsPaused)
        {
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (state.DailyRealizedPnl <= -_riskOptions.MaxDailyLossUsdt)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            await PauseTradingAsync(
                state,
                "Daily loss limit reached.",
                activeOrders,
                null,
                "Daily loss limit reached. Trading paused.",
                cancellationToken);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        var refreshedActiveOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        var wallet = _appOptions.TradingMode == TradingMode.Paper
            ? null
            : await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, _quoteAsset, _baseAsset);

        await EnsureDcaEntryOrderAsync(
            profile,
            config,
            state,
            instrument,
            currentPrice,
            refreshedActiveOrders,
            wallet,
            cancellationToken);

        state.IsInitialized = true;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);

        return state;
    }

    private async Task<BotState> RunBtdCycleAsync(
        GridBotSettings profile,
        BotState state,
        BybitInstrumentInfo instrument,
        CancellationToken cancellationToken)
    {
        ResetDailyPnlIfNeeded(state);

        var config = ParseBtdStrategyConfig(profile);
        var ticker = await _bybitRestClient.GetTickerAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var currentPrice = ticker.LastPrice;
        state.LastObservedPrice = currentPrice;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation("Current price for {Symbol}: {Price}", _gridOptions.Symbol, currentPrice);

        if (await TryApplyProtectiveReduceOnlyFromFreshMarketAsync(profile, state, [], instrument, currentPrice, cancellationToken))
        {
            return await FinishProtectiveReduceOnlyCycleAsync(profile, state, [], currentPrice, cancellationToken);
        }

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(profile, state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanDcaActiveOrdersAsync(state, activeOrders, cancellationToken);
            await SimulatePaperFillsAsync(state, [], profile, currentPrice, cancellationToken);
        }
        else
        {
            await SynchronizeLiveOrdersAsync(state, [], profile, cancellationToken);
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(profile, state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanDcaActiveOrdersAsync(state, activeOrders, cancellationToken);
        }

        if (state.IsPaused)
        {
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (state.DailyRealizedPnl <= -_riskOptions.MaxDailyLossUsdt)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            await PauseTradingAsync(
                state,
                "Daily loss limit reached.",
                activeOrders,
                null,
                "Daily loss limit reached. Trading paused.",
                cancellationToken);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        await EnsureBtdOverlayOrderAsync(profile, config, state, instrument, currentPrice, cancellationToken);

        state.IsInitialized = true;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);

        return state;
    }

    private async Task<BotState> RunSignalCycleAsync(
        GridBotSettings profile,
        BotState state,
        BybitInstrumentInfo instrument,
        CancellationToken cancellationToken)
    {
        ResetDailyPnlIfNeeded(state);

        var config = ParseSignalStrategyConfig(profile);
        var ticker = await _bybitRestClient.GetTickerAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var currentPrice = ticker.LastPrice;
        state.LastObservedPrice = currentPrice;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation("Current price for {Symbol}: {Price}", _gridOptions.Symbol, currentPrice);

        if (await TryApplyProtectiveReduceOnlyFromFreshMarketAsync(profile, state, [], instrument, currentPrice, cancellationToken))
        {
            return await FinishProtectiveReduceOnlyCycleAsync(profile, state, [], currentPrice, cancellationToken);
        }

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(profile, state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanSignalActiveOrdersAsync(state, activeOrders, cancellationToken);
            await SimulatePaperFillsAsync(state, [], profile, currentPrice, cancellationToken);
        }
        else
        {
            await SynchronizeLiveOrdersAsync(state, [], profile, cancellationToken);
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(profile, state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanSignalActiveOrdersAsync(state, activeOrders, cancellationToken);
        }

        if (state.IsPaused)
        {
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (state.DailyRealizedPnl <= -_riskOptions.MaxDailyLossUsdt)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            await PauseTradingAsync(
                state,
                "Daily loss limit reached.",
                activeOrders,
                null,
                "Daily loss limit reached. Trading paused.",
                cancellationToken);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (!_appOptions.SignalTradingEnabled)
        {
            var candles = await _bybitRestClient.GetKlinesAsync(
                _gridOptions.Category,
                _gridOptions.Symbol,
                string.IsNullOrWhiteSpace(config.CandleInterval) ? "1" : config.CandleInterval,
                Math.Max(30, config.LookbackCandles),
                cancellationToken);
            var signal = _signalAnalyzer.Analyze(candles);
            _logger.LogInformation(
                "Signal diagnostics only. Signal: {Signal}, Confidence: {Confidence}, Reason: {Reason}. Set SIGNAL_TRADING_ENABLED=true to allow signal-mode trading.",
                signal.Signal,
                signal.Confidence,
                signal.Reason);
            state.IsInitialized = true;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        await EnsureSignalOverlayOrdersAsync(profile, config, state, instrument, currentPrice, cancellationToken);

        state.IsInitialized = true;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);

        return state;
    }

    private async Task<BotState> RunTrendFollowCycleAsync(
        GridBotSettings profile,
        BotState state,
        BybitInstrumentInfo instrument,
        CancellationToken cancellationToken)
    {
        ResetDailyPnlIfNeeded(state);

        var config = ParseTrendFollowingStrategyConfig(profile);
        var ticker = await _bybitRestClient.GetTickerAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var currentPrice = ticker.LastPrice;
        state.LastObservedPrice = currentPrice;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation("Current price for {Symbol}: {Price}", _gridOptions.Symbol, currentPrice);

        if (await TryApplyProtectiveReduceOnlyFromFreshMarketAsync(profile, state, [], instrument, currentPrice, cancellationToken))
        {
            return await FinishProtectiveReduceOnlyCycleAsync(profile, state, [], currentPrice, cancellationToken);
        }

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(profile, state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanSignalActiveOrdersAsync(state, activeOrders, cancellationToken);
            await SimulatePaperFillsAsync(state, [], profile, currentPrice, cancellationToken);
        }
        else
        {
            await SynchronizeLiveOrdersAsync(state, [], profile, cancellationToken);
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(profile, state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanSignalActiveOrdersAsync(state, activeOrders, cancellationToken);
        }

        if (state.IsPaused)
        {
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (state.DailyRealizedPnl <= -_riskOptions.MaxDailyLossUsdt)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            await PauseTradingAsync(
                state,
                "Daily loss limit reached.",
                activeOrders,
                null,
                "Daily loss limit reached. Trading paused.",
                cancellationToken);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        await EnsureTrendFollowingOverlayOrdersAsync(profile, config, state, instrument, currentPrice, cancellationToken);

        state.IsInitialized = true;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);

        return state;
    }

    private async Task<BotState> RunComboCycleAsync(
        GridBotSettings profile,
        BotState state,
        IReadOnlyList<GridLevel> levels,
        BybitInstrumentInfo instrument,
        CancellationToken cancellationToken)
    {
        var result = await RunCycleAsync(state, levels, instrument, profile, cancellationToken);
        if (result.IsPaused)
        {
            return result;
        }

        var currentPrice = result.LastObservedPrice;
        if (currentPrice is null)
        {
            return result;
        }

        if (await TryApplyProtectiveReduceOnlyFromFreshMarketAsync(
                profile,
                result,
                levels,
                instrument,
                currentPrice.Value,
                cancellationToken))
        {
            return await FinishProtectiveReduceOnlyCycleAsync(profile, result, levels, currentPrice.Value, cancellationToken);
        }

        var config = ParseComboStrategyConfig(profile);
        var dcaBelowPrice = config.DcaBelowPrice ?? _gridOptions.LowerPrice;
        if (currentPrice.Value > dcaBelowPrice)
        {
            return result;
        }

        var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        activeOrders = await CleanDcaActiveOrdersAsync(result, activeOrders, cancellationToken);
        var wallet = _appOptions.TradingMode == TradingMode.Paper
            ? null
            : await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, _quoteAsset, _baseAsset);

        await EnsureDcaEntryOrderAsync(
            profile,
            config,
            result,
            instrument,
            currentPrice.Value,
            activeOrders,
            wallet,
            cancellationToken);

        result.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(result, cancellationToken);
        return result;
    }

    private async Task<BotState> RunHybridCycleAsync(
        GridBotSettings profile,
        BotState state,
        IReadOnlyList<GridLevel> levels,
        BybitInstrumentInfo instrument,
        CancellationToken cancellationToken)
    {
        var result = await RunComboCycleAsync(profile, state, levels, instrument, cancellationToken);
        if (result.IsPaused || result.LastObservedPrice is null)
        {
            return result;
        }

        var currentPrice = result.LastObservedPrice.Value;
        await EnsureBtdOverlayOrderAsync(
            profile,
            ParseBtdStrategyConfig(profile),
            result,
            instrument,
            currentPrice,
            cancellationToken);

        await EnsureTrendFollowingOverlayOrdersAsync(
            profile,
            ParseTrendFollowingStrategyConfig(profile),
            result,
            instrument,
            currentPrice,
            cancellationToken);

        await EnsureSignalOverlayOrdersAsync(
            profile,
            ParseSignalStrategyConfig(profile),
            result,
            instrument,
            currentPrice,
            cancellationToken);

        result.IsInitialized = true;
        result.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(result, cancellationToken);
        return result;
    }

    private async Task EnsureBtdOverlayOrderAsync(
        GridBotSettings profile,
        BtdStrategyConfig config,
        BotState state,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        var candles = await _bybitRestClient.GetKlinesAsync(
            _gridOptions.Category,
            _gridOptions.Symbol,
            string.IsNullOrWhiteSpace(config.CandleInterval) ? "1" : config.CandleInterval,
            Math.Max(10, config.DipLookbackCandles),
            cancellationToken);
        var regime = _marketRegimeAnalyzer.Analyze(candles);
        if (regime.Regime == MarketRegimeType.Danger)
        {
            _logger.LogInformation("BTD entry skipped because market regime is danger.");
            return;
        }

        if (!_btdStrategy.IsDipTriggered(config, currentPrice, candles))
        {
            _logger.LogInformation("BTD entry skipped because dip trigger has not fired.");
            return;
        }

        var refreshedActiveOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        var allOrders = await _repository.GetOrdersAsync(_gridOptions.Symbol, cancellationToken);
        var btdOrders = GetBtdEntryScopeOrders(profile, allOrders).ToArray();
        if (!_btdStrategy.CanOpenBuy(config, btdOrders, DateTimeOffset.UtcNow))
        {
            _logger.LogInformation("BTD entry skipped because max buys or buy interval is active.");
            return;
        }

        var wallet = _appOptions.TradingMode == TradingMode.Paper
            ? null
            : await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, _quoteAsset, _baseAsset);

        await EnsureDipEntryOrderAsync(
            profile,
            config,
            state,
            instrument,
            currentPrice,
            refreshedActiveOrders,
            wallet,
            BtdEntryMarker,
            "BTD",
            cancellationToken);
    }

    private async Task EnsureTrendFollowingOverlayOrdersAsync(
        GridBotSettings profile,
        TrendFollowingStrategyConfig config,
        BotState state,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        var candles = await _bybitRestClient.GetKlinesAsync(
            _gridOptions.Category,
            _gridOptions.Symbol,
            string.IsNullOrWhiteSpace(config.CandleInterval) ? "1" : config.CandleInterval,
            Math.Max(30, config.LookbackCandles),
            cancellationToken);
        var trend = AnalyzeTrendFollowing(candles, config);
        var refreshedActiveOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        var wallet = _appOptions.TradingMode == TradingMode.Paper
            ? null
            : await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, _quoteAsset, _baseAsset);
        var trendPosition = await GetTrendPositionSnapshotAsync(cancellationToken);
        var isStopLoss = ShouldTrendStopLoss(config, trendPosition.Quantity, trendPosition.AverageEntryPrice, currentPrice);
        var isTakeProfit = ShouldTrendTakeProfit(config, trendPosition.Quantity, trendPosition.AverageEntryPrice, currentPrice);

        if (isStopLoss || isTakeProfit || (trendPosition.Quantity > 0m && trend.ShouldExit))
        {
            await CancelActiveTrendBuyOrdersAsync(refreshedActiveOrders, cancellationToken);
            await EnsureTrendSellOrderAsync(
                config,
                state,
                instrument,
                currentPrice,
                refreshedActiveOrders,
                wallet,
                isStopLoss ? "stop-loss" : isTakeProfit ? "take-profit" : trend.ExitReason,
                cancellationToken);
            return;
        }

        if (!trend.ShouldBuy)
        {
            _logger.LogInformation(
                "Trend-following overlay holds. TrendStrength: {TrendStrength}, Resistance: {Resistance}, VolumeRatio: {VolumeRatio}, Pullback: {PullbackPercent}",
                trend.TrendStrength,
                trend.Resistance,
                trend.VolumeRatio,
                trend.PullbackPercent);
            return;
        }

        var allOrders = await _repository.GetOrdersAsync(_gridOptions.Symbol, cancellationToken);
        if (IsTrendCooldownActive(profile, config, DateTimeOffset.UtcNow, allOrders))
        {
            return;
        }

        await EnsureTrendBuyOrderAsync(
            config,
            state,
            instrument,
            currentPrice,
            refreshedActiveOrders,
            wallet,
            cancellationToken);
    }

    private async Task EnsureSignalOverlayOrdersAsync(
        GridBotSettings profile,
        SignalStrategyConfig config,
        BotState state,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        var candles = await _bybitRestClient.GetKlinesAsync(
            _gridOptions.Category,
            _gridOptions.Symbol,
            string.IsNullOrWhiteSpace(config.CandleInterval) ? "1" : config.CandleInterval,
            Math.Max(30, config.LookbackCandles),
            cancellationToken);
        var signal = _signalAnalyzer.Analyze(candles);
        var refreshedActiveOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        var wallet = _appOptions.TradingMode == TradingMode.Paper
            ? null
            : await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, _quoteAsset, _baseAsset);
        var signalPosition = await GetSignalPositionSnapshotAsync(cancellationToken);
        var isTakeProfit = ShouldTakeProfit(config, signalPosition.Quantity, signalPosition.AverageEntryPrice, currentPrice);

        if (ShouldStopLoss(config, signalPosition.Quantity, signalPosition.AverageEntryPrice, currentPrice))
        {
            await CancelActiveSignalBuyOrdersAsync(refreshedActiveOrders, cancellationToken);
            await EnsureSignalSellOrderAsync(
                config,
                state,
                instrument,
                currentPrice,
                refreshedActiveOrders,
                wallet,
                "stop-loss",
                cancellationToken);
        }
        else if (isTakeProfit || signal.Signal == SignalType.Sell)
        {
            if (!isTakeProfit && signal.Signal == SignalType.Sell && signal.Confidence < config.MinConfidence)
            {
                _logger.LogInformation(
                    "Signal sell skipped because confidence {Confidence} is below minimum {MinConfidence}.",
                    signal.Confidence,
                    config.MinConfidence);
            }
            else
            {
                await CancelActiveSignalBuyOrdersAsync(refreshedActiveOrders, cancellationToken);
                if (isTakeProfit ||
                    !IsSignalCooldownActive(profile, config, DateTimeOffset.UtcNow, await _repository.GetOrdersAsync(_gridOptions.Symbol, cancellationToken)))
                {
                    await EnsureSignalSellOrderAsync(
                        config,
                        state,
                        instrument,
                        currentPrice,
                        refreshedActiveOrders,
                        wallet,
                        isTakeProfit ? "take-profit" : "sell-signal",
                        cancellationToken);
                }
            }
        }
        else if (signal.Signal == SignalType.Buy)
        {
            var allOrders = await _repository.GetOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (signal.Confidence < config.MinConfidence)
            {
                _logger.LogInformation(
                    "Signal buy skipped because confidence {Confidence} is below minimum {MinConfidence}.",
                    signal.Confidence,
                    config.MinConfidence);
            }
            else if (!IsSignalCooldownActive(profile, config, DateTimeOffset.UtcNow, allOrders))
            {
                await EnsureSignalBuyOrderAsync(
                    config,
                    state,
                    instrument,
                    currentPrice,
                    refreshedActiveOrders,
                    wallet,
                    cancellationToken);
            }
        }
        else
        {
            if (signal.Signal == SignalType.Avoid)
            {
                await CancelActiveSignalBuyOrdersAsync(refreshedActiveOrders, cancellationToken);
            }

            _logger.LogInformation(
                "Signal strategy holds. Signal: {Signal}, Confidence: {Confidence}, Reason: {Reason}",
                signal.Signal,
                signal.Confidence,
                signal.Reason);
        }
    }

    private async Task<BotState> RunCycleAsync(
        BotState state,
        IReadOnlyList<GridLevel> levels,
        BybitInstrumentInfo instrument,
        GridBotSettings? profile,
        CancellationToken cancellationToken)
    {
        ResetDailyPnlIfNeeded(state);

        var ticker = await _bybitRestClient.GetTickerAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var currentPrice = ticker.LastPrice;
        state.LastObservedPrice = currentPrice;
        state.UpdatedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation("Current price for {Symbol}: {Price}", _gridOptions.Symbol, currentPrice);

        if (await TryApplyProtectiveReduceOnlyFromFreshMarketAsync(profile, state, levels, instrument, currentPrice, cancellationToken))
        {
            return await FinishProtectiveReduceOnlyCycleAsync(profile, state, levels, currentPrice, cancellationToken);
        }

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            await BootstrapPaperInventoryIfNeededAsync(state, levels, instrument, currentPrice, cancellationToken);

            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(profile, state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanRiskyActiveOrdersAsync(profile, state, levels, activeOrders, cancellationToken);
            await SimulatePaperFillsAsync(state, levels, profile, currentPrice, cancellationToken);
        }
        else
        {
            await SynchronizeLiveOrdersAsync(state, levels, profile, cancellationToken);
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(profile, state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanRiskyActiveOrdersAsync(profile, state, levels, activeOrders, cancellationToken);
        }

        if (state.IsPaused)
        {
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (state.DailyRealizedPnl <= -_riskOptions.MaxDailyLossUsdt)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            await PauseTradingAsync(
                state,
                "Daily loss limit reached.",
                activeOrders,
                null,
                "Daily loss limit reached. Trading paused.",
                cancellationToken);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (await TryAutoRecenterGridAsync(state, currentPrice, cancellationToken))
        {
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (!_strategy.IsWithinTradingRange(_gridOptions, currentPrice))
        {
            _logger.LogInformation("Price is outside the trading range. No new orders will be created.");
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        var symbolCandles = await _bybitRestClient.GetKlinesAsync(
            _gridOptions.Category,
            _gridOptions.Symbol,
            _gridOptions.CandleInterval,
            Math.Max(120, Math.Max(_gridOptions.EmaSlow + 10, _gridOptions.VolumeSmaPeriod + _gridOptions.AtrPeriod * 3)),
            cancellationToken);

        var marketRegime = _marketRegimeAnalyzer.Analyze(symbolCandles);
        _logger.LogInformation(
            "MarketRegime for {Symbol}: {MarketRegime}. Confidence: {Confidence}. ADX: {Adx}. VolumeRatio: {VolumeRatio}. Recommendation: {Recommendation}",
            _gridOptions.Symbol,
            marketRegime.Regime,
            marketRegime.Confidence,
            marketRegime.Adx,
            marketRegime.VolumeRatio,
            marketRegime.Recommendation);

        var activeGridOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        if (await TryApplyProtectiveReduceOnlyAsync(
                profile,
                state,
                levels,
                instrument,
                activeGridOrders,
                symbolCandles,
                currentPrice,
                marketRegime,
                cancellationToken))
        {
            return await FinishProtectiveReduceOnlyCycleAsync(profile, state, levels, currentPrice, cancellationToken);
        }

        var btcCandles = _gridOptions.BtcFilterEnabled
            ? await _bybitRestClient.GetKlinesAsync("spot", "BTCUSDT", _gridOptions.CandleInterval, Math.Max(20, _gridOptions.BtcLookbackCandles), cancellationToken)
            : [];

        var marketPhase = _priceActionPhaseDetector.Detect(_gridOptions, currentPrice, symbolCandles, btcCandles);
        _logger.LogInformation(
            "MarketPhase for {Symbol}: {MarketPhase}. SelectedStrategy: {SuggestedStrategy}. Score: {Score}. Confidence: {Confidence}. Reason: {Reason}",
            _gridOptions.Symbol,
            marketPhase.Phase,
            marketPhase.SuggestedStrategy,
            marketPhase.Score,
            marketPhase.Confidence,
            marketPhase.Reason);

        var bigRedGuard = _bigRedCandleGuard.Evaluate(_gridOptions, symbolCandles, btcCandles);
        if (bigRedGuard.IsActive)
        {
            _logger.LogWarning(
                "NoTradeReason={NoTradeReason}. {Reason}. BlocksBuy={BlocksBuy}. CancelGridBuyOrders={CancelGridBuyOrders}.",
                bigRedGuard.NoTradeReason,
                bigRedGuard.Reason,
                bigRedGuard.BlocksBuy,
                bigRedGuard.CancelGridBuyOrders);

            if (bigRedGuard.CancelGridBuyOrders)
            {
                await CancelActiveBuyOrdersAsync(activeGridOrders, cancellationToken);
                activeGridOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            }

            if (state.BaseAssetQuantity > 0m)
            {
                await ApplyReduceOnlyProtectionAsync(
                    profile,
                    state,
                    levels,
                    instrument,
                    activeGridOrders,
                    currentPrice,
                    $"big-red-candle-guard: {bigRedGuard.Reason}",
                    cancellationToken);
                return await FinishProtectiveReduceOnlyCycleAsync(profile, state, levels, currentPrice, cancellationToken);
            }

            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (!IsGridSidewaysMarket(marketRegime, marketPhase))
        {
            _logger.LogInformation(
                "Grid orders skipped for {Symbol}: grid is allowed only in sideways markets. Regime={Regime}, Move={MovePercent:F4}%, Phase={Phase}, Reason={Reason}",
                _gridOptions.Symbol,
                marketRegime.Regime,
                marketRegime.MovePercent,
                marketPhase.Phase,
                marketPhase.Reason);

            await CancelActiveBuyOrdersAsync(activeGridOrders, cancellationToken);
            activeGridOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (state.BaseAssetQuantity > 0m)
            {
                activeGridOrders = await ApplyReduceOnlyProtectionAsync(
                    profile,
                    state,
                    levels,
                    instrument,
                    activeGridOrders,
                    currentPrice,
                    $"grid-non-sideways-market: regime={marketRegime.Regime}, move={marketRegime.MovePercent:0.####}%, phase={marketPhase.Phase}",
                    cancellationToken);
                return await FinishProtectiveReduceOnlyCycleAsync(profile, state, levels, currentPrice, cancellationToken);
            }

            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (IsProtectiveSellOnlyPhase(marketPhase.Phase))
        {
            await CancelActiveBuyOrdersAsync(activeGridOrders, cancellationToken);
            activeGridOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (state.BaseAssetQuantity <= 0m)
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            activeGridOrders = await ApplyReduceOnlyProtectionAsync(
                profile,
                state,
                levels,
                instrument,
                activeGridOrders,
                currentPrice,
                $"market-phase-{marketPhase.Phase.ToString().ToLowerInvariant()}: {marketPhase.Reason}",
                cancellationToken);
            return await FinishProtectiveReduceOnlyCycleAsync(profile, state, levels, currentPrice, cancellationToken);
        }

        if (!_strategy.CanCreateGridIntents(_gridOptions, marketPhase, currentPrice, bigRedGuard.IsActive))
        {
            _logger.LogInformation(
                "NoTradeReason={NoTradeReason}. Grid orders skipped for {Symbol}. MarketPhase={MarketPhase}, SuggestedStrategy={SuggestedStrategy}, Reason={Reason}",
                marketPhase.Phase is MarketPhase.Unknown ? NoTradeReason.UnknownMarketPhase :
                    marketPhase.Phase is MarketPhase.HighVolatility ? NoTradeReason.HighVolatility :
                    marketPhase.Phase is MarketPhase.Dump ? NoTradeReason.DumpDetected :
                    NoTradeReason.ScoreTooLow,
                _gridOptions.Symbol,
                marketPhase.Phase,
                marketPhase.SuggestedStrategy,
                marketPhase.Reason);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (_marketRegimeFilter.ShouldBlockNewOrders(_gridOptions, symbolCandles, btcCandles))
        {
            _logger.LogInformation("Market regime filter blocks new grid orders for the current cycle.");
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        activeGridOrders = await CleanRiskyActiveOrdersAsync(profile, state, levels, activeGridOrders, cancellationToken);
        var wallet = _appOptions.TradingMode == TradingMode.Paper
            ? null
            : await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, _quoteAsset, _baseAsset);

        await EnsureGridOrdersAsync(profile, state, levels, instrument, currentPrice, activeGridOrders, wallet, cancellationToken);
        state.IsInitialized = true;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);

        return state;
    }

    private async Task<IReadOnlyList<GridLevel>> EnsureGridLevelsAsync(CancellationToken cancellationToken)
    {
        var expectedLevels = _strategy.BuildGrid(_gridOptions);
        var storedLevels = await _repository.GetGridLevelsAsync(_gridOptions.Symbol, cancellationToken);

        if (storedLevels.Count != expectedLevels.Count ||
            storedLevels.Where((storedLevel, index) => storedLevel.Price != expectedLevels[index].Price).Any())
        {
            await _repository.SaveGridLevelsAsync(_gridOptions.Symbol, expectedLevels, cancellationToken);
            return expectedLevels;
        }

        return storedLevels;
    }

    private async Task<BotState> EnsureBotStateAsync(CancellationToken cancellationToken)
    {
        var state = await _repository.GetBotStateAsync(_gridOptions.Symbol, cancellationToken);
        if (state is not null)
        {
            state.TradingMode = _appOptions.TradingMode;
            ResetDailyPnlIfNeeded(state);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        state = new BotState
        {
            Symbol = _gridOptions.Symbol,
            TradingMode = _appOptions.TradingMode,
            QuoteAssetBalance = _gridOptions.PaperInitialUsdt,
            BaseAssetQuantity = _gridOptions.PaperInitialBaseAssetQuantity,
            AverageEntryPrice = 0m,
            IsInitialized = false,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _repository.SaveBotStateAsync(state, cancellationToken);
        return state;
    }

    private void ResetDailyPnlIfNeeded(BotState state)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (state.DailyPnlDate == today)
        {
            return;
        }

        state.DailyPnlDate = today;
        state.DailyRealizedPnl = 0m;
    }

    private async Task BootstrapPaperInventoryIfNeededAsync(
        BotState state,
        IReadOnlyList<GridLevel> levels,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        if (_appOptions.TradingMode != TradingMode.Paper ||
            !_gridOptions.PaperBootstrapInventoryEnabled ||
            state.IsInitialized ||
            state.BaseAssetQuantity > 0m)
        {
            return;
        }

        var sellLevels = _strategy.GetSellLevels(levels, currentPrice);
        var requiredBase = sellLevels.Sum(level => instrument.RoundQuantity(_gridOptions.OrderSizeUsdt / level.Price));
        if (requiredBase <= 0m)
        {
            return;
        }

        state.BaseAssetQuantity = requiredBase;
        state.AverageEntryPrice = currentPrice;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);

        _logger.LogInformation(
            "Bootstrapped paper inventory for symmetric spot grid. Base asset quantity: {BaseAssetQuantity}",
            requiredBase);
    }

    private async Task SimulatePaperFillsAsync(
        BotState state,
        IReadOnlyList<GridLevel> levels,
        GridBotSettings? profile,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        foreach (var order in activeOrders)
        {
            var shouldFill = order.Side == TradeSide.Buy
                ? currentPrice <= order.Price
                : currentPrice >= order.Price;

            if (!shouldFill)
            {
                continue;
            }

            var remainingQuantity = order.Quantity - order.FilledQuantity;
            if (remainingQuantity <= 0m)
            {
                continue;
            }

            if (order.Side == TradeSide.Sell &&
                !IsSignalOrder(order) &&
                !IsTrendOrder(order) &&
                !await HasMinimumNetProfitForOrderAsync(profile, state, order, remainingQuantity, cancellationToken))
            {
                await CancelManagedOrderAsync(order, cancellationToken);
                _logger.LogInformation(
                    "Paper sell fill at {Price} blocked because expected net profit is below minimum. Average entry: {AverageEntryPrice}",
                    order.Price,
                    state.AverageEntryPrice);
                continue;
            }

            var fillFee = CalculateFee(order.Price * remainingQuantity);
            var pnlDelta = ApplyFillDelta(state, order.Side, remainingQuantity, order.Price, fillFee);

            order.FilledQuantity = order.Quantity;
            order.AverageFillPrice = order.Price;
            order.FeePaid += fillFee;
            order.RealizedPnl += pnlDelta;
            order.Status = OrderStatus.Filled;
            order.FilledAt = DateTimeOffset.UtcNow;
            order.UpdatedAt = DateTimeOffset.UtcNow;

            await _repository.UpsertOrderAsync(order, cancellationToken);
            await _repository.SaveBotStateAsync(state, cancellationToken);

            _logger.LogInformation(
                "Paper order filled. Side: {Side}, Price: {Price}, Qty: {Qty}, RealizedPnlDelta: {PnlDelta}",
                order.Side,
                order.Price,
                order.Quantity,
                pnlDelta);

            await _notifier.NotifyAsync(
                $"Order filled: `{order.Side}` `{order.Quantity}` `{_baseAsset}` at `{order.Price}`. PnL delta: `{pnlDelta}`",
                cancellationToken);

            if (ShouldUseDcaFollowUp(profile, order))
            {
                await EnsureDcaTakeProfitOrderAsync(state, ParseDcaStrategyConfig(profile!), order, cancellationToken);
            }
            else if (ShouldUseBtdFollowUp(profile, order))
            {
                await EnsureDcaTakeProfitOrderAsync(state, ParseBtdStrategyConfig(profile!), order, cancellationToken);
            }
            else if (ShouldSkipGridFollowUp(profile, order))
            {
                _logger.LogInformation(
                    "Grid follow-up skipped for {OrderLinkId} because it belongs to a standalone overlay.",
                    order.OrderLinkId);
            }
            else
            {
                await EnsureOppositeGridOrderAsync(state, levels, order, cancellationToken);
            }
        }
    }

    private async Task<bool> TryAutoRecenterGridAsync(
        BotState state,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        if (!_gridOptions.AutoRecenterEnabled)
        {
            return false;
        }

        var candles = await _bybitRestClient.GetKlinesAsync(
            _gridOptions.Category,
            _gridOptions.Symbol,
            _gridOptions.AutoRecenterCandleInterval,
            _gridOptions.AutoRecenterLookbackCandles,
            cancellationToken);
        if (candles.Count < 5)
        {
            return false;
        }

        var recentLow = candles.Min(candle => candle.Low);
        var recentHigh = candles.Max(candle => candle.High);
        var padding = _gridOptions.Step * _gridOptions.AutoRecenterPaddingSteps;
        var proposedLower = FloorToStep(Math.Min(recentLow, currentPrice) - padding, _gridOptions.Step);
        var proposedUpper = CeilingToStep(Math.Max(recentHigh, currentPrice) + padding, _gridOptions.Step);
        if (proposedLower <= 0m || proposedUpper <= proposedLower)
        {
            return false;
        }

        var minShift = _gridOptions.Step * _gridOptions.AutoRecenterMinShiftSteps;
        if (Math.Abs(proposedLower - _gridOptions.LowerPrice) < minShift &&
            Math.Abs(proposedUpper - _gridOptions.UpperPrice) < minShift)
        {
            return false;
        }

        var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        foreach (var order in activeOrders)
        {
            if (await ShouldPreserveProfitableSellOrderAsync(null, state, order, cancellationToken))
            {
                _logger.LogInformation(
                    "Preserved profitable sell order {OrderLinkId} during auto-recenter. Price: {Price}, average entry: {AverageEntryPrice}",
                    order.OrderLinkId,
                    order.Price,
                    state.AverageEntryPrice);
                continue;
            }

            await CancelManagedOrderAsync(order, cancellationToken);
        }

        await _repository.SaveRuntimeSettingsAsync(
            new GridBotSettings
            {
                Symbol = _gridOptions.Symbol,
                Category = _gridOptions.Category,
                StrategySelectionMode = StrategySelectionMode.Manual,
                StrategyType = TradingStrategyType.Grid,
                StrategyConfigJson = "{}",
                LowerPrice = proposedLower,
                UpperPrice = proposedUpper,
                Step = _gridOptions.Step,
                OrderSizeUsdt = _gridOptions.OrderSizeUsdt,
                StopLowerPrice = Math.Max(_gridOptions.Step, proposedLower - padding),
                StopUpperPrice = proposedUpper + padding,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);

        _logger.LogInformation(
            "Auto-recentered grid for {Symbol}. Old range: {OldLower}-{OldUpper}. New range: {NewLower}-{NewUpper}.",
            _gridOptions.Symbol,
            _gridOptions.LowerPrice,
            _gridOptions.UpperPrice,
            proposedLower,
            proposedUpper);

        await _notifier.NotifyAsync(
            $"Grid auto-recentered for `{_gridOptions.Symbol}`.\nRange: `{proposedLower}`-`{proposedUpper}`",
            cancellationToken);

        state.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private async Task SynchronizeLiveOrdersAsync(
        BotState state,
        IReadOnlyList<GridLevel> levels,
        GridBotSettings? profile,
        CancellationToken cancellationToken)
    {
        var localOrders = (await _repository.GetOrdersAsync(_gridOptions.Symbol, cancellationToken)).ToDictionary(order => order.OrderLinkId);
        var remoteOpenOrders = await _bybitRestClient.GetOpenOrdersAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var remoteHistory = await _bybitRestClient.GetOrderHistoryAsync(_gridOptions.Category, _gridOptions.Symbol, null, cancellationToken);
        var snapshots = remoteOpenOrders
            .Concat(remoteHistory)
            .Where(snapshot => IsManagedOrder(snapshot.OrderLinkId))
            .GroupBy(snapshot => snapshot.OrderLinkId)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .ToArray();

        foreach (var snapshot in snapshots)
        {
            if (!localOrders.TryGetValue(snapshot.OrderLinkId, out var order))
            {
                order = new GridOrder
                {
                    OrderLinkId = snapshot.OrderLinkId,
                    BybitOrderId = snapshot.OrderId,
                    Symbol = snapshot.Symbol,
                    Category = _gridOptions.Category,
                    Side = ParseSide(snapshot.Side),
                    Price = snapshot.Price,
                    Quantity = snapshot.Quantity,
                    FilledQuantity = 0m,
                    Status = MapStatus(snapshot.OrderStatus),
                    TradingMode = _appOptions.TradingMode,
                    CreatedAt = snapshot.CreatedAt,
                    UpdatedAt = snapshot.UpdatedAt
                };
            }

            var previousFilledQuantity = order.FilledQuantity;
            var previousFee = order.FeePaid;
            var previousStatus = order.Status;

            order.BybitOrderId = snapshot.OrderId;
            order.Price = snapshot.Price == 0m ? order.Price : snapshot.Price;
            order.FilledQuantity = snapshot.CumExecQty;
            order.AverageFillPrice = snapshot.AveragePrice == 0m ? order.AverageFillPrice : snapshot.AveragePrice;
            order.FeePaid = snapshot.FeePaid;
            order.Status = MapStatus(snapshot.OrderStatus);
            order.UpdatedAt = snapshot.UpdatedAt;
            order.FilledAt = order.Status == OrderStatus.Filled ? snapshot.UpdatedAt : order.FilledAt;

            var fillDelta = snapshot.CumExecQty - previousFilledQuantity;
            if (fillDelta > 0m)
            {
                var feeDelta = Math.Max(0m, snapshot.FeePaid - previousFee);
                var fillPrice = snapshot.AveragePrice > 0m ? snapshot.AveragePrice : order.Price;
                var pnlDelta = ApplyFillDelta(state, order.Side, fillDelta, fillPrice, feeDelta);
                order.RealizedPnl += pnlDelta;

                _logger.LogInformation(
                    "Exchange execution synced. Side: {Side}, FillDelta: {FillDelta}, Price: {Price}, PnL delta: {PnlDelta}",
                    order.Side,
                    fillDelta,
                    fillPrice,
                    pnlDelta);
            }

            await _repository.UpsertOrderAsync(order, cancellationToken);

            if (previousStatus != OrderStatus.Filled && order.Status == OrderStatus.Filled)
            {
                await _notifier.NotifyAsync(
                    $"Order filled: `{order.Side}` `{order.Quantity}` `{_baseAsset}` at `{order.AverageFillPrice}`.",
                    cancellationToken);

                if (ShouldUseDcaFollowUp(profile, order))
                {
                    await EnsureDcaTakeProfitOrderAsync(state, ParseDcaStrategyConfig(profile!), order, cancellationToken);
                }
                else if (ShouldUseBtdFollowUp(profile, order))
                {
                    await EnsureDcaTakeProfitOrderAsync(state, ParseBtdStrategyConfig(profile!), order, cancellationToken);
                }
                else if (ShouldSkipGridFollowUp(profile, order))
                {
                    _logger.LogInformation(
                        "Grid follow-up skipped for {OrderLinkId} because it belongs to a standalone overlay.",
                        order.OrderLinkId);
                }
                else
                {
                    await EnsureOppositeGridOrderAsync(state, levels, order, cancellationToken);
                }
            }
        }

        await _repository.SaveBotStateAsync(state, cancellationToken);
    }

    private async Task<bool> HandleStopConditionsAsync(
        GridBotSettings profile,
        BotState state,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        if (_strategy.IsBelowStop(_gridOptions, currentPrice))
        {
            if (await TryForceRefreshCurrentStrategyAfterStopAsync(profile, state, currentPrice, cancellationToken))
            {
                return true;
            }

            await PauseTradingAsync(
                state,
                "Price moved below STOP_LOWER_PRICE.",
                activeOrders,
                TradeSide.Buy,
                $"Price moved below stop: `{currentPrice}`. Buy orders cancelled.",
                cancellationToken);
            return true;
        }

        if (_strategy.IsAboveStop(_gridOptions, currentPrice))
        {
            if (await TryForceRefreshCurrentStrategyAfterStopAsync(profile, state, currentPrice, cancellationToken))
            {
                return true;
            }

            await PauseTradingAsync(
                state,
                "Price moved above STOP_UPPER_PRICE.",
                activeOrders,
                TradeSide.Sell,
                $"Price moved above stop: `{currentPrice}`. Sell orders cancelled.",
                cancellationToken);
            return true;
        }

        return false;
    }

    private async Task<bool> TryForceRefreshCurrentStrategyAfterStopAsync(
        GridBotSettings profile,
        BotState state,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        var gridOptions = RuntimeGridOptionsFactory.ToGridOptions(profile, _defaultGridOptions);
        var candles = await _bybitRestClient.GetKlinesAsync(
            gridOptions.Category,
            gridOptions.Symbol,
            AnalysisDefaults.AutoRecommendationCandleInterval,
            AnalysisDefaults.AutoRecommendationLookbackCandles,
            cancellationToken);
        if (candles.Count == 0)
        {
            return false;
        }

        var regime = _marketRegimeAnalyzer.Analyze(candles);
        var recommendation = _autoStrategySelector.RecommendForStrategy(gridOptions, regime, candles, profile.StrategyType);
        if (currentPrice < recommendation.StopLowerPrice || currentPrice > recommendation.StopUpperPrice)
        {
            return false;
        }

        var refreshedSettings = new GridBotSettings
        {
            Symbol = profile.Symbol,
            Category = profile.Category,
            StrategySelectionMode = profile.StrategySelectionMode,
            StrategyType = profile.StrategyType,
            StrategyConfigJson = recommendation.StrategyConfigJson,
            LowerPrice = recommendation.LowerPrice,
            UpperPrice = recommendation.UpperPrice,
            Step = recommendation.Step,
            OrderSizeUsdt = recommendation.OrderSizeUsdt,
            StopLowerPrice = recommendation.StopLowerPrice,
            StopUpperPrice = recommendation.StopUpperPrice,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _repository.SaveRuntimeSettingsAsync(refreshedSettings, cancellationToken);

        if (state.IsPaused)
        {
            state.IsPaused = false;
            state.PauseReason = null;
        }

        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);

        _logger.LogInformation(
            "Forced same-strategy recommendation refresh kept {Symbol} running after stop boundary breach. Strategy: {Strategy}, Stop: {StopLower}-{StopUpper}",
            refreshedSettings.Symbol,
            refreshedSettings.StrategyType,
            refreshedSettings.StopLowerPrice,
            refreshedSettings.StopUpperPrice);
        await _notifier.NotifyAsync(
            $"Stop boundary auto-refresh applied.\nSymbol: `{refreshedSettings.Symbol}`\nStrategy kept: `{refreshedSettings.StrategyType}`\nStop: `{refreshedSettings.StopLowerPrice}`-`{refreshedSettings.StopUpperPrice}`",
            cancellationToken);

        return true;
    }

    private async Task PauseTradingAsync(
        BotState state,
        string reason,
        IReadOnlyCollection<GridOrder> activeOrders,
        TradeSide? sideToCancel,
        string notificationMessage,
        CancellationToken cancellationToken)
    {
        if (state.IsPaused && string.Equals(state.PauseReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        state.IsPaused = true;
        state.PauseReason = reason;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);

        foreach (var order in activeOrders.Where(order => sideToCancel is null || order.Side == sideToCancel))
        {
            await CancelManagedOrderAsync(order, cancellationToken);
        }

        _logger.LogWarning(reason);
        await _notifier.NotifyAsync(notificationMessage, cancellationToken);
    }

    private async Task<bool> TryApplyProtectiveReduceOnlyFromFreshMarketAsync(
        GridBotSettings? profile,
        BotState state,
        IReadOnlyList<GridLevel> levels,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        var candles = await _bybitRestClient.GetKlinesAsync(
            _gridOptions.Category,
            _gridOptions.Symbol,
            _gridOptions.TrailingProtectionEnabled
                ? _gridOptions.TrailingProtectionCandleInterval
                : AnalysisDefaults.AutoRecommendationCandleInterval,
            _gridOptions.TrailingProtectionEnabled
                ? Math.Max(10, _gridOptions.TrailingProtectionLookbackCandles)
                : AnalysisDefaults.AutoRecommendationLookbackCandles,
            cancellationToken);
        if (candles.Count == 0)
        {
            return false;
        }

        var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        return await TryApplyProtectiveReduceOnlyAsync(
            profile,
            state,
            levels,
            instrument,
            activeOrders,
            candles,
            currentPrice,
            _marketRegimeAnalyzer.Analyze(candles),
            cancellationToken);
    }

    private async Task<bool> TryApplyProtectiveReduceOnlyAsync(
        GridBotSettings? profile,
        BotState state,
        IReadOnlyList<GridLevel> levels,
        BybitInstrumentInfo instrument,
        IReadOnlyCollection<GridOrder> activeOrders,
        IReadOnlyList<Candle> candles,
        decimal currentPrice,
        MarketRegimeAnalysis marketRegime,
        CancellationToken cancellationToken)
    {
        if (profile?.StrategyType == TradingStrategyType.ReduceOnly)
        {
            return false;
        }

        var reason = marketRegime.Regime == MarketRegimeType.Danger
            ? ReduceOnlyReasonDanger
            : ShouldApplyTrailingProtection(candles, currentPrice, out var pumpPercent, out var pullbackPercent)
                ? $"{ReduceOnlyReasonTrailing}: pump={pumpPercent:0.####}%, pullback={pullbackPercent:0.####}%"
                : null;
        if (reason is null)
        {
            return false;
        }

        if (state.BaseAssetQuantity <= 0m)
        {
            foreach (var order in activeOrders.Where(order => order.IsActive && order.Side == TradeSide.Buy).ToArray())
            {
                await CancelManagedOrderAsync(order, cancellationToken);
            }

            _logger.LogWarning(
                "Protective no-buy mode active for {Symbol}. Reason: {Reason}. Buy orders were cancelled and no new buy orders will be created.",
                _gridOptions.Symbol,
                reason);
            return true;
        }

        await ApplyReduceOnlyProtectionAsync(
            profile,
            state,
            levels,
            instrument,
            activeOrders,
            currentPrice,
            reason,
            cancellationToken);

        return true;
    }

    private bool ShouldApplyTrailingProtection(
        IReadOnlyList<Candle> candles,
        decimal currentPrice,
        out decimal pumpPercent,
        out decimal pullbackPercent)
    {
        pumpPercent = 0m;
        pullbackPercent = 0m;
        if (!_gridOptions.TrailingProtectionEnabled || candles.Count < 5 || currentPrice <= 0m)
        {
            return false;
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var firstOpen = ordered[0].Open;
        var recentHigh = ordered.Max(candle => candle.High);
        if (firstOpen <= 0m || recentHigh <= 0m)
        {
            return false;
        }

        pumpPercent = (recentHigh - firstOpen) / firstOpen * 100m;
        pullbackPercent = (recentHigh - currentPrice) / recentHigh * 100m;

        return pumpPercent >= _gridOptions.TrailingProtectionPumpPercent &&
            pullbackPercent >= _gridOptions.TrailingProtectionPullbackPercent;
    }

    private async Task<IReadOnlyList<GridOrder>> ApplyReduceOnlyProtectionAsync(
        GridBotSettings? profile,
        BotState state,
        IReadOnlyList<GridLevel> levels,
        BybitInstrumentInfo instrument,
        IReadOnlyCollection<GridOrder> activeOrders,
        decimal currentPrice,
        string reason,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "ReduceOnly protection active for {Symbol}. Reason: {Reason}. Buy orders will be cancelled, profitable sells will be preserved.",
            _gridOptions.Symbol,
            reason);

        var cancelledBuyCount = 0;
        foreach (var order in activeOrders.Where(order => order.IsActive && order.Side == TradeSide.Buy).ToArray())
        {
            await CancelManagedOrderAsync(order, cancellationToken);
            cancelledBuyCount++;
        }

        var remainingOrders = activeOrders.Where(order => order.IsActive).ToArray();
        remainingOrders = (await CancelUnprofitableSellOrdersAsync(profile, state, remainingOrders, cancellationToken)).ToArray();

        var wallet = _appOptions.TradingMode == TradingMode.Paper
            ? null
            : await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, _quoteAsset, _baseAsset);
        var createdSellCount = await EnsureReduceOnlySellOrdersAsync(profile, state, levels, instrument, remainingOrders, wallet, currentPrice, cancellationToken);
        if (cancelledBuyCount > 0 || createdSellCount > 0)
        {
            await _notifier.NotifyAsync(
                $"ReduceOnly protection active for `{_gridOptions.Symbol}`.\nReason: `{reason}`\nBuy orders cancelled: `{cancelledBuyCount}`\nSell orders created: `{createdSellCount}`",
                cancellationToken);
        }

        return (await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken)).ToArray();
    }

    private async Task<int> EnsureReduceOnlySellOrdersAsync(
        GridBotSettings? profile,
        BotState state,
        IReadOnlyList<GridLevel> levels,
        BybitInstrumentInfo instrument,
        IReadOnlyCollection<GridOrder> activeOrders,
        BybitWalletBalance? wallet,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        if (state.BaseAssetQuantity <= 0m)
        {
            return 0;
        }

        var availableBase = GetAvailableBaseBalance(state, activeOrders, wallet);
        if (availableBase <= 0m)
        {
            return 0;
        }

        var createdSellCount = 0;
        var sellLevels = BuildReduceOnlySellLevels(levels, currentPrice, state.AverageEntryPrice).ToArray();
        foreach (var price in sellLevels)
        {
            if (availableBase <= 0m ||
                HasActiveOrderAtLevel(activeOrders, price) ||
                await _repository.GetActiveOrderAtLevelAsync(_gridOptions.Symbol, TradeSide.Sell, price, cancellationToken) is not null)
            {
                continue;
            }

            var targetQuantity = instrument.RoundQuantity(_gridOptions.OrderSizeUsdt / price);
            var quantity = instrument.RoundQuantity(decimal.Min(availableBase, targetQuantity));
            if (quantity <= 0m ||
                quantity < instrument.MinOrderQty ||
                quantity * price < instrument.MinOrderAmount)
            {
                continue;
            }

            if (!await HasMinimumNetProfitAsync(TradeSide.Sell, state.AverageEntryPrice, price, quantity, 0m, cancellationToken))
            {
                continue;
            }

            var expectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(state.AverageEntryPrice, price);

            var createdOrders = await ExecuteStrategyDecisionAsync(
                new StrategyExecutionDecision
                {
                    OrderIntents = [new OrderIntent(TradeSide.Sell, price, quantity, ReduceOnlyExitMarker, ReduceOnlySource, expectedProfitPercent)]
                },
                cancellationToken);
            if (createdOrders.Count == 0)
            {
                continue;
            }

            var createdOrder = createdOrders[0];
            activeOrders = activeOrders.Append(createdOrder).ToArray();
            availableBase -= quantity;
            createdSellCount++;
        }

        return createdSellCount;
    }

    private IReadOnlyList<decimal> BuildReduceOnlySellLevels(
        IReadOnlyList<GridLevel> levels,
        decimal currentPrice,
        decimal averageEntryPrice)
    {
        var minSellPrice = decimal.Max(currentPrice, averageEntryPrice);
        var candidateLevels = levels.Count > 0
            ? levels.Select(level => level.Price)
            : _strategy.BuildGrid(_gridOptions).Select(level => level.Price);

        var sellLevels = candidateLevels
            .Where(price => price > minSellPrice)
            .OrderBy(price => price)
            .Distinct()
            .ToArray();
        if (sellLevels.Length > 0)
        {
            return sellLevels;
        }

        var firstFallback = CeilingToStep(minSellPrice + _gridOptions.Step, _gridOptions.Step);
        return Enumerable.Range(0, Math.Min(5, _riskOptions.MaxOpenOrders))
            .Select(index => decimal.Round(firstFallback + _gridOptions.Step * index, 8, MidpointRounding.AwayFromZero))
            .Where(price => price > minSellPrice)
            .ToArray();
    }

    private async Task EnsureGridOrdersAsync(
        GridBotSettings? profile,
        BotState state,
        IReadOnlyList<GridLevel> levels,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders,
        BybitWalletBalance? wallet,
        CancellationToken cancellationToken)
    {
        var buyLevels = _strategy.GetBuyLevels(levels, currentPrice).OrderByDescending(level => level.Price).ToArray();
        var sellLevels = _strategy.GetSellLevels(levels, currentPrice).OrderBy(level => level.Price).ToArray();

        foreach (var level in buyLevels)
        {
            if (HasActiveOrderAtLevel(activeOrders, level.Price) ||
                await _repository.GetActiveOrderAtLevelAsync(_gridOptions.Symbol, TradeSide.Buy, level.Price, cancellationToken) is not null)
            {
                continue;
            }

            var targetSellLevel = _strategy.GetNextUpperLevel(levels, level.Price);
            if (targetSellLevel is null)
            {
                _logger.LogInformation("Grid buy at {Price} skipped because no upper sell level exists for expected profit calculation.", level.Price);
                continue;
            }

            var buyExpectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(level.Price, targetSellLevel.Price);

            var orderSizeUsdt = GetOrderSizeUsdt(TradeSide.Buy, level.Price, state);
            var quantity = instrument.RoundQuantity(orderSizeUsdt / level.Price);
            if (quantity <= 0m || quantity < instrument.MinOrderQty)
            {
                continue;
            }

            var availableUsdt = GetAvailableQuoteBalance(state, activeOrders, wallet);
            var violations = _riskManager.ValidateOrderPlacement(
                _riskOptions,
                _gridOptions,
                state,
                activeOrders,
                currentPrice,
                orderSizeUsdt,
                instrument.MinOrderAmount,
                availableUsdt);

            if (violations.Count > 0)
            {
                _logger.LogWarning("Buy order at {Price} skipped: {Violations}", level.Price, string.Join(" | ", violations));
                continue;
            }

            _logger.LogInformation(
                "Risk decision for {Symbol}: Allow buy at {Price}. Capital allocation requested: {OrderSizeUsdt} USDT, available: {AvailableUsdt} USDT.",
                _gridOptions.Symbol,
                level.Price,
                orderSizeUsdt,
                availableUsdt);

            var decision = new StrategyExecutionDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Buy, level.Price, quantity, StrategySource: ResolveGridSource(profile), ExpectedProfitPercent: buyExpectedProfitPercent)]
            };
            var createdOrders = await ExecuteStrategyDecisionAsync(decision, cancellationToken);
            if (createdOrders.Count == 0)
            {
                continue;
            }

            var createdOrder = createdOrders[0];
            activeOrders = activeOrders.Append(createdOrder).ToArray();
        }

        foreach (var level in sellLevels)
        {
            if (HasActiveOrderAtLevel(activeOrders, level.Price) ||
                await _repository.GetActiveOrderAtLevelAsync(_gridOptions.Symbol, TradeSide.Sell, level.Price, cancellationToken) is not null)
            {
                continue;
            }

            var orderSizeUsdt = GetOrderSizeUsdt(TradeSide.Sell, level.Price, state);
            var quantity = instrument.RoundQuantity(orderSizeUsdt / level.Price);
            if (quantity <= 0m || quantity < instrument.MinOrderQty)
            {
                continue;
            }

            if (!await HasMinimumNetProfitAsync(TradeSide.Sell, state.AverageEntryPrice, level.Price, quantity, 0m, cancellationToken))
            {
                _logger.LogInformation("Sell order at {Price} skipped because net profit is below minimum.", level.Price);
                continue;
            }

            var sellExpectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(state.AverageEntryPrice, level.Price);

            var availableBase = GetAvailableBaseBalance(state, activeOrders, wallet);
            if (availableBase < quantity)
            {
                _logger.LogInformation("Sell order at {Price} skipped because base asset inventory is insufficient.", level.Price);
                continue;
            }

            _logger.LogInformation(
                "Risk decision for {Symbol}: Allow sell at {Price}. Capital allocation requested: 0 USDT, available base: {AvailableBase}.",
                _gridOptions.Symbol,
                level.Price,
                availableBase);

            var decision = new StrategyExecutionDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Sell, level.Price, quantity, StrategySource: ResolveGridSource(profile), ExpectedProfitPercent: sellExpectedProfitPercent)]
            };
            var createdOrders = await ExecuteStrategyDecisionAsync(decision, cancellationToken);
            if (createdOrders.Count == 0)
            {
                continue;
            }

            var createdOrder = createdOrders[0];
            activeOrders = activeOrders.Append(createdOrder).ToArray();
        }
    }

    private async Task EnsureDcaEntryOrderAsync(
        GridBotSettings profile,
        DcaStrategyConfig config,
        BotState state,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders,
        BybitWalletBalance? wallet,
        CancellationToken cancellationToken)
    {
        var dcaActiveBuyOrders = GetDcaEntryScopeOrders(profile, activeOrders)
            .Count(order => order.Side == TradeSide.Buy);
        if (dcaActiveBuyOrders >= Math.Max(1, config.MaxActiveBuyOrders))
        {
            return;
        }

        var allOrders = await _repository.GetOrdersAsync(_gridOptions.Symbol, cancellationToken);
        var dcaHistoryOrders = GetDcaEntryScopeOrders(profile, allOrders).ToArray();
        if (!_dcaStrategy.IsDueForEntry(config, dcaHistoryOrders, DateTimeOffset.UtcNow))
        {
            return;
        }

        if (config.DipPercent > 0m)
        {
            var candles = await _bybitRestClient.GetKlinesAsync(
                _gridOptions.Category,
                _gridOptions.Symbol,
                string.IsNullOrWhiteSpace(config.CandleInterval) ? "1" : config.CandleInterval,
                Math.Max(1, config.DipLookbackCandles),
                cancellationToken);
            if (!_dcaStrategy.IsDipAllowed(config, currentPrice, candles))
            {
                _logger.LogInformation("DCA entry skipped because dip filter has not triggered.");
                return;
            }
        }

        await EnsureDipEntryOrderAsync(
            profile,
            config,
            state,
            instrument,
            currentPrice,
            activeOrders,
            wallet,
            DcaEntryMarker,
            "DCA",
            cancellationToken);
    }

    private async Task EnsureDipEntryOrderAsync(
        GridBotSettings profile,
        DcaStrategyConfig config,
        BotState state,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders,
        BybitWalletBalance? wallet,
        string entryMarker,
        string strategyName,
        CancellationToken cancellationToken)
    {
        var orderSizeUsdt = GetDcaOrderSizeUsdt(config, state);
        var limitPrice = instrument.RoundPrice(_dcaStrategy.CalculateLimitBuyPrice(currentPrice, config));
        if (limitPrice <= 0m)
        {
            return;
        }

        if (limitPrice < _gridOptions.StopLowerPrice || limitPrice > _gridOptions.StopUpperPrice)
        {
            _logger.LogInformation("{StrategyName} entry at {Price} skipped because it is outside stop boundaries.", strategyName, limitPrice);
            return;
        }

        if (HasActiveOrderAtLevel(activeOrders, limitPrice))
        {
            return;
        }

        var quantity = instrument.RoundQuantity(orderSizeUsdt / limitPrice);
        if (quantity <= 0m || quantity < instrument.MinOrderQty)
        {
            return;
        }

        var maxPositionUsdt = config.MaxPositionUsdt is > 0m ? config.MaxPositionUsdt.Value : _riskOptions.MaxPositionUsdt;
        var riskOptions = new RiskOptions
        {
            MaxDailyLossUsdt = _riskOptions.MaxDailyLossUsdt,
            MaxOpenOrders = _riskOptions.MaxOpenOrders,
            MaxPositionUsdt = maxPositionUsdt,
            MinOrderSizeUsdt = _riskOptions.MinOrderSizeUsdt
        };
        var dipRiskGridOptions = new GridOptions
        {
            Symbol = _gridOptions.Symbol,
            Category = _gridOptions.Category,
            LowerPrice = _gridOptions.StopLowerPrice,
            UpperPrice = _gridOptions.StopUpperPrice,
            Step = _gridOptions.Step,
            OrderSizeUsdt = _gridOptions.OrderSizeUsdt,
            StopLowerPrice = _gridOptions.StopLowerPrice,
            StopUpperPrice = _gridOptions.StopUpperPrice
        };
        var violations = _riskManager.ValidateOrderPlacement(
            riskOptions,
            dipRiskGridOptions,
            state,
            activeOrders,
            currentPrice,
            orderSizeUsdt,
            instrument.MinOrderAmount,
            GetAvailableQuoteBalance(state, activeOrders, wallet));

        if (violations.Count > 0)
        {
            _logger.LogWarning("{StrategyName} buy order at {Price} skipped: {Violations}", strategyName, limitPrice, string.Join(" | ", violations));
            return;
        }

        var takeProfitPrice = _dcaStrategy.CalculateTakeProfitPrice(limitPrice, config);
        var expectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(limitPrice, takeProfitPrice);
        var createdOrders = await ExecuteStrategyDecisionAsync(
            new StrategyExecutionDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Buy, limitPrice, quantity, entryMarker, ResolveDipEntrySource(profile, entryMarker), expectedProfitPercent)]
            },
            cancellationToken);
        if (createdOrders.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "{StrategyName} entry order created for {Symbol}. Price: {Price}, Quantity: {Quantity}, Config: {StrategyConfig}",
            strategyName,
            profile.Symbol,
            limitPrice,
            quantity,
            profile.StrategyConfigJson);
    }

    private async Task EnsureSignalBuyOrderAsync(
        SignalStrategyConfig config,
        BotState state,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders,
        BybitWalletBalance? wallet,
        CancellationToken cancellationToken)
    {
        if (activeOrders.Any(order => order.IsActive && order.Side == TradeSide.Buy && IsSignalOrder(order)))
        {
            return;
        }

        var orderSizeUsdt = GetSignalOrderSizeUsdt(config, state);
        var limitPrice = instrument.RoundPrice(CalculateSignalBuyPrice(currentPrice, config));
        if (limitPrice <= 0m)
        {
            return;
        }

        if (HasActiveOrderAtLevel(activeOrders, limitPrice))
        {
            return;
        }

        var quantity = instrument.RoundQuantity(orderSizeUsdt / limitPrice);
        if (quantity <= 0m || quantity < instrument.MinOrderQty)
        {
            return;
        }

        var riskOptions = new RiskOptions
        {
            MaxDailyLossUsdt = _riskOptions.MaxDailyLossUsdt,
            MaxOpenOrders = _riskOptions.MaxOpenOrders,
            MaxPositionUsdt = GetSignalMaxPositionUsdt(config),
            MinOrderSizeUsdt = _riskOptions.MinOrderSizeUsdt
        };
        var violations = _riskManager.ValidateOrderPlacement(
            riskOptions,
            _gridOptions,
            state,
            activeOrders,
            currentPrice,
            orderSizeUsdt,
            instrument.MinOrderAmount,
            GetAvailableQuoteBalance(state, activeOrders, wallet));

        if (violations.Count > 0)
        {
            _logger.LogWarning("Signal buy order at {Price} skipped: {Violations}", limitPrice, string.Join(" | ", violations));
            return;
        }

        var expectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(
            limitPrice,
            limitPrice * (1m + GetSignalTakeProfitPercent(config) / 100m));
        var createdOrders = await ExecuteStrategyDecisionAsync(
            new StrategyExecutionDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Buy, limitPrice, quantity, SignalEntryMarker, SignalSource, expectedProfitPercent)]
            },
            cancellationToken);
        if (createdOrders.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Signal buy order created for {Symbol}. Price: {Price}, Quantity: {Quantity}",
            _gridOptions.Symbol,
            limitPrice,
            quantity);
    }

    private async Task EnsureSignalSellOrderAsync(
        SignalStrategyConfig config,
        BotState state,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders,
        BybitWalletBalance? wallet,
        string reason,
        CancellationToken cancellationToken)
    {
        if (activeOrders.Any(order => order.IsActive && order.Side == TradeSide.Sell && IsSignalOrder(order)))
        {
            return;
        }

        var signalPositionQuantity = await GetSignalPositionQuantityAsync(cancellationToken);
        var availableBase = GetAvailableBaseBalance(state, activeOrders, wallet);
        var quantity = instrument.RoundQuantity(decimal.Min(availableBase, signalPositionQuantity));
        if (quantity <= 0m || quantity < instrument.MinOrderQty)
        {
            _logger.LogInformation(
                "Signal sell skipped because signal-owned base inventory is insufficient. Signal quantity: {SignalQuantity}, available base: {AvailableBase}.",
                signalPositionQuantity,
                availableBase);
            return;
        }

        var limitPrice = instrument.RoundPrice(CalculateSignalSellPrice(currentPrice, config));
        if (limitPrice <= 0m)
        {
            return;
        }

        if (HasActiveOrderAtLevel(activeOrders, limitPrice))
        {
            return;
        }

        var skipExpectedProfitFilter = !string.Equals(reason, "take-profit", StringComparison.OrdinalIgnoreCase);
        var expectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(signalPosition.AverageEntryPrice, limitPrice);
        var createdOrders = await ExecuteStrategyDecisionAsync(
            new StrategyExecutionDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Sell, limitPrice, quantity, SignalExitMarker, SignalSource, expectedProfitPercent, skipExpectedProfitFilter)]
            },
            cancellationToken);
        if (createdOrders.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Signal sell order created for {Symbol}. Reason: {Reason}, Price: {Price}, Quantity: {Quantity}",
            _gridOptions.Symbol,
            reason,
            limitPrice,
            quantity);
    }

    private async Task EnsureTrendBuyOrderAsync(
        TrendFollowingStrategyConfig config,
        BotState state,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders,
        BybitWalletBalance? wallet,
        CancellationToken cancellationToken)
    {
        if (activeOrders.Any(order => order.IsActive && IsTrendOrder(order)))
        {
            return;
        }

        var orderSizeUsdt = GetTrendOrderSizeUsdt(config, state);
        var limitPrice = instrument.RoundPrice(CalculateTrendBuyPrice(currentPrice, config));
        if (limitPrice <= 0m || HasActiveOrderAtLevel(activeOrders, limitPrice))
        {
            return;
        }

        var quantity = instrument.RoundQuantity(orderSizeUsdt / limitPrice);
        if (quantity <= 0m || quantity < instrument.MinOrderQty)
        {
            return;
        }

        var riskOptions = new RiskOptions
        {
            MaxDailyLossUsdt = _riskOptions.MaxDailyLossUsdt,
            MaxOpenOrders = _riskOptions.MaxOpenOrders,
            MaxPositionUsdt = config.MaxPositionUsdt is > 0m ? config.MaxPositionUsdt.Value : _riskOptions.MaxPositionUsdt,
            MinOrderSizeUsdt = _riskOptions.MinOrderSizeUsdt
        };
        var violations = _riskManager.ValidateOrderPlacement(
            riskOptions,
            _gridOptions,
            state,
            activeOrders,
            currentPrice,
            orderSizeUsdt,
            instrument.MinOrderAmount,
            GetAvailableQuoteBalance(state, activeOrders, wallet));

        if (violations.Count > 0)
        {
            _logger.LogWarning("Trend-following buy order at {Price} skipped: {Violations}", limitPrice, string.Join(" | ", violations));
            return;
        }

        var expectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(
            limitPrice,
            limitPrice * (1m + config.TakeProfitPercent / 100m));
        var createdOrders = await ExecuteStrategyDecisionAsync(
            new StrategyExecutionDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Buy, limitPrice, quantity, TrendEntryMarker, TrendSource, expectedProfitPercent)]
            },
            cancellationToken);
        if (createdOrders.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Trend-following buy order created for {Symbol}. Price: {Price}, Quantity: {Quantity}",
            _gridOptions.Symbol,
            limitPrice,
            quantity);
    }

    private async Task EnsureTrendSellOrderAsync(
        TrendFollowingStrategyConfig config,
        BotState state,
        BybitInstrumentInfo instrument,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders,
        BybitWalletBalance? wallet,
        string reason,
        CancellationToken cancellationToken)
    {
        if (activeOrders.Any(order => order.IsActive && order.Side == TradeSide.Sell && IsTrendOrder(order)))
        {
            return;
        }

        var trendPosition = await GetTrendPositionSnapshotAsync(cancellationToken);
        var availableBase = GetAvailableBaseBalance(state, activeOrders, wallet);
        var quantity = instrument.RoundQuantity(decimal.Min(availableBase, trendPosition.Quantity));
        if (quantity <= 0m || quantity < instrument.MinOrderQty)
        {
            _logger.LogInformation(
                "Trend-following sell skipped because trend-owned inventory is insufficient. Trend quantity: {TrendQuantity}, available base: {AvailableBase}.",
                trendPosition.Quantity,
                availableBase);
            return;
        }

        var limitPrice = instrument.RoundPrice(CalculateTrendSellPrice(currentPrice, config));
        if (limitPrice <= 0m || HasActiveOrderAtLevel(activeOrders, limitPrice))
        {
            return;
        }

        var skipExpectedProfitFilter = !string.Equals(reason, "take-profit", StringComparison.OrdinalIgnoreCase);
        var expectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(trendPosition.AverageEntryPrice, limitPrice);
        var createdOrders = await ExecuteStrategyDecisionAsync(
            new StrategyExecutionDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Sell, limitPrice, quantity, TrendExitMarker, TrendSource, expectedProfitPercent, skipExpectedProfitFilter)]
            },
            cancellationToken);
        if (createdOrders.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Trend-following sell order created for {Symbol}. Reason: {Reason}, Price: {Price}, Quantity: {Quantity}",
            _gridOptions.Symbol,
            reason,
            limitPrice,
            quantity);
    }

    private async Task CancelActiveSignalBuyOrdersAsync(
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        foreach (var order in activeOrders.Where(order => order.IsActive && order.Side == TradeSide.Buy && IsSignalOrder(order)).ToArray())
        {
            await CancelManagedOrderAsync(order, cancellationToken);
        }
    }

    private async Task CancelActiveGridBuyOrdersAsync(
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        foreach (var order in activeOrders.Where(order =>
                     order.IsActive &&
                     order.Side == TradeSide.Buy &&
                     string.IsNullOrWhiteSpace(order.ParentOrderLinkId)).ToArray())
        {
            await CancelManagedOrderAsync(order, cancellationToken);
        }
    }

    private async Task CancelActiveBuyOrdersAsync(
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        foreach (var order in activeOrders.Where(order => order.IsActive && order.Side == TradeSide.Buy).ToArray())
        {
            await CancelManagedOrderAsync(order, cancellationToken);
        }
    }

    private async Task CancelActiveTrendBuyOrdersAsync(
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        foreach (var order in activeOrders.Where(order => order.IsActive && order.Side == TradeSide.Buy && IsTrendOrder(order)).ToArray())
        {
            await CancelManagedOrderAsync(order, cancellationToken);
        }
    }

    private async Task<decimal> GetSignalPositionQuantityAsync(CancellationToken cancellationToken)
    {
        var snapshot = await GetSignalPositionSnapshotAsync(cancellationToken);
        return snapshot.Quantity;
    }

    private async Task<SignalPositionSnapshot> GetSignalPositionSnapshotAsync(CancellationToken cancellationToken)
    {
        var orders = await _repository.GetOrdersAsync(_gridOptions.Symbol, cancellationToken);
        var signalBuyQuantity = orders
            .Where(order => order.Side == TradeSide.Buy &&
                string.Equals(order.ParentOrderLinkId, SignalEntryMarker, StringComparison.Ordinal))
            .Sum(order => order.FilledQuantity);
        var signalBuyCost = orders
            .Where(order => order.Side == TradeSide.Buy &&
                string.Equals(order.ParentOrderLinkId, SignalEntryMarker, StringComparison.Ordinal))
            .Sum(order => order.FilledQuantity * (order.AverageFillPrice > 0m ? order.AverageFillPrice : order.Price));
        var signalSells = orders
            .Where(order => order.Side == TradeSide.Sell &&
                string.Equals(order.ParentOrderLinkId, SignalExitMarker, StringComparison.Ordinal))
            .Sum(order => order.FilledQuantity);
        var quantity = decimal.Max(0m, signalBuyQuantity - signalSells);
        var averageEntryPrice = signalBuyQuantity > 0m ? signalBuyCost / signalBuyQuantity : 0m;

        return new SignalPositionSnapshot(quantity, averageEntryPrice);
    }

    private async Task<SignalPositionSnapshot> GetTrendPositionSnapshotAsync(CancellationToken cancellationToken)
    {
        var orders = await _repository.GetOrdersAsync(_gridOptions.Symbol, cancellationToken);
        var trendBuyOrders = orders
            .Where(order => order.Side == TradeSide.Buy &&
                string.Equals(order.ParentOrderLinkId, TrendEntryMarker, StringComparison.Ordinal))
            .ToArray();
        var trendBuyQuantity = trendBuyOrders.Sum(order => order.FilledQuantity);
        var trendBuyCost = trendBuyOrders.Sum(order => order.FilledQuantity * (order.AverageFillPrice > 0m ? order.AverageFillPrice : order.Price));
        var trendSells = orders
            .Where(order => order.Side == TradeSide.Sell &&
                string.Equals(order.ParentOrderLinkId, TrendExitMarker, StringComparison.Ordinal))
            .Sum(order => order.FilledQuantity);
        var quantity = decimal.Max(0m, trendBuyQuantity - trendSells);
        var averageEntryPrice = trendBuyQuantity > 0m ? trendBuyCost / trendBuyQuantity : 0m;

        return new SignalPositionSnapshot(quantity, averageEntryPrice);
    }

    private async Task EnsureDcaTakeProfitOrderAsync(
        BotState state,
        DcaStrategyConfig config,
        GridOrder filledOrder,
        CancellationToken cancellationToken)
    {
        if (filledOrder.Side != TradeSide.Buy || filledOrder.FilledQuantity <= 0m)
        {
            return;
        }

        var instrument = await _bybitRestClient.GetInstrumentInfoAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var entryPrice = filledOrder.AverageFillPrice > 0m ? filledOrder.AverageFillPrice : filledOrder.Price;
        var takeProfitPrice = instrument.RoundPrice(_dcaStrategy.CalculateTakeProfitPrice(entryPrice, config));
        if (takeProfitPrice <= entryPrice)
        {
            _logger.LogInformation("DCA take-profit skipped because target price is not above entry price.");
            return;
        }

        var quantity = instrument.RoundQuantity(filledOrder.FilledQuantity);
        if (quantity <= 0m || quantity < instrument.MinOrderQty)
        {
            return;
        }

        if (!await HasMinimumNetProfitAsync(TradeSide.Sell, entryPrice, takeProfitPrice, quantity, filledOrder.FeePaid, cancellationToken))
        {
            _logger.LogInformation(
                "DCA take-profit sell at {Price} skipped because net profit is below minimum.",
                takeProfitPrice);
            return;
        }

        var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        if (HasActiveOrderAtLevel(activeOrders, takeProfitPrice) ||
            await _repository.GetActiveOrderAtLevelAsync(_gridOptions.Symbol, TradeSide.Sell, takeProfitPrice, cancellationToken) is not null)
        {
            return;
        }

        var wallet = _appOptions.TradingMode == TradingMode.Paper
            ? null
            : await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, _quoteAsset, _baseAsset);
        if (GetAvailableBaseBalance(state, activeOrders, wallet) < quantity)
        {
            _logger.LogInformation("DCA take-profit sell at {Price} skipped because base asset inventory is insufficient.", takeProfitPrice);
            return;
        }

        var expectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(entryPrice, takeProfitPrice);
        await ExecuteStrategyDecisionAsync(
            new StrategyExecutionDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Sell, takeProfitPrice, quantity, filledOrder.OrderLinkId, filledOrder.StrategySource, expectedProfitPercent)]
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<GridOrder>> CleanRiskyActiveOrdersAsync(
        GridBotSettings? profile,
        BotState state,
        IReadOnlyList<GridLevel> levels,
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        var cleanedOrders = await CancelUnprofitableSellOrdersAsync(profile, state, activeOrders, cancellationToken);
        cleanedOrders = await CancelCrossSideOrdersAtSameLevelAsync(cleanedOrders, cancellationToken);
        cleanedOrders = await CancelGridBuyOrdersBelowExpectedProfitAsync(levels, cleanedOrders, cancellationToken);
        cleanedOrders = await ReduceBuyExposureAfterDailyTakeProfitAsync(state, cleanedOrders, cancellationToken);

        return cleanedOrders.ToArray();
    }

    private async Task<IReadOnlyCollection<GridOrder>> CancelGridBuyOrdersBelowExpectedProfitAsync(
        IReadOnlyList<GridLevel> levels,
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        foreach (var order in activeOrders.Where(order => order.IsActive && order.Side == TradeSide.Buy && IsGridSource(order.StrategySource)).ToArray())
        {
            var targetSellLevel = _strategy.GetNextUpperLevel(levels, order.Price);
            if (targetSellLevel is null)
            {
                await CancelManagedOrderAsync(order, cancellationToken);
                _logger.LogInformation(
                    "Grid buy order {OrderLinkId} at {Price} cancelled because no upper sell level exists for expected profit calculation.",
                    order.OrderLinkId,
                    order.Price);
                continue;
            }

            var expectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(order.Price, targetSellLevel.Price);
            var decision = _expectedProfitFilter.Evaluate(_gridOptions, order.Side, expectedProfitPercent, order.StrategySource);
            if (decision.IsAllowed)
            {
                continue;
            }

            await CancelManagedOrderAsync(order, cancellationToken);
            _logger.LogInformation(
                "Grid buy order {OrderLinkId} at {Price} cancelled by expected profit filter. Expected: {ExpectedProfitPercent:F4}%, required: {RequiredProfitPercent:F4}%.",
                order.OrderLinkId,
                order.Price,
                decision.ExpectedProfitPercent,
                decision.RequiredProfitPercent);
        }

        return activeOrders.Where(order => order.IsActive).ToArray();
    }

    private async Task<IReadOnlyList<GridOrder>> CleanDcaActiveOrdersAsync(
        BotState state,
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        var cleanedOrders = await CancelCrossSideOrdersAtSameLevelAsync(activeOrders, cancellationToken);
        cleanedOrders = await ReduceBuyExposureAfterDailyTakeProfitAsync(state, cleanedOrders, cancellationToken);

        return cleanedOrders.ToArray();
    }

    private async Task<IReadOnlyList<GridOrder>> CleanSignalActiveOrdersAsync(
        BotState state,
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        var cleanedOrders = await CancelCrossSideOrdersAtSameLevelAsync(activeOrders, cancellationToken);
        cleanedOrders = await ReduceBuyExposureAfterDailyTakeProfitAsync(state, cleanedOrders, cancellationToken);

        return cleanedOrders.ToArray();
    }

    private async Task<IReadOnlyCollection<GridOrder>> ReduceBuyExposureAfterDailyTakeProfitAsync(
        BotState state,
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        if (_gridOptions.DailyTakeProfitUsdt <= 0m ||
            state.DailyRealizedPnl < _gridOptions.DailyTakeProfitUsdt ||
            _gridOptions.DailyTakeProfitOrderMultiplier >= 1m)
        {
            return activeOrders;
        }

        foreach (var order in activeOrders.Where(order => order.Side == TradeSide.Buy).ToArray())
        {
            var remainingNotional = (order.Quantity - order.FilledQuantity) * order.Price;
            var targetNotional = GetOrderSizeUsdt(TradeSide.Buy, order.Price, state);
            if (remainingNotional <= targetNotional * 1.05m)
            {
                continue;
            }

            await CancelManagedOrderAsync(order, cancellationToken);
            _logger.LogInformation(
                "Buy order {OrderLinkId} cancelled after daily take-profit to reduce exposure. Current notional: {CurrentNotional}, target notional: {TargetNotional}",
                order.OrderLinkId,
                remainingNotional,
                targetNotional);
        }

        return activeOrders.Where(order => order.IsActive).ToArray();
    }

    private async Task<IReadOnlyCollection<GridOrder>> CancelUnprofitableSellOrdersAsync(
        GridBotSettings? profile,
        BotState state,
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        if (state.AverageEntryPrice <= 0m)
        {
            return activeOrders;
        }

        foreach (var order in activeOrders.Where(order => order.Side == TradeSide.Sell).ToArray())
        {
            if (IsSignalOrder(order) || IsTrendOrder(order))
            {
                continue;
            }

            var remainingQuantity = order.Quantity - order.FilledQuantity;
            if (remainingQuantity <= 0m)
            {
                continue;
            }

            if (await HasMinimumNetProfitForOrderAsync(profile, state, order, remainingQuantity, cancellationToken))
            {
                continue;
            }

            await CancelManagedOrderAsync(order, cancellationToken);
            _logger.LogInformation(
                "Sell order {OrderLinkId} at {Price} cancelled because expected net profit is below minimum. Average entry: {AverageEntryPrice}",
                order.OrderLinkId,
                order.Price,
                state.AverageEntryPrice);
        }

        return activeOrders.Where(order => order.IsActive).ToArray();
    }

    private async Task<IReadOnlyCollection<GridOrder>> CancelCrossSideOrdersAtSameLevelAsync(
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        var conflictingGroups = activeOrders
            .Where(order => order.IsActive)
            .GroupBy(order => order.Price)
            .Where(group => group.Any(order => order.Side == TradeSide.Buy) && group.Any(order => order.Side == TradeSide.Sell))
            .ToArray();

        foreach (var group in conflictingGroups)
        {
            foreach (var order in group)
            {
                await CancelManagedOrderAsync(order, cancellationToken);
            }

            _logger.LogInformation(
                "Cancelled cross-side active orders at the same grid level {Price} to prevent fee churn.",
                group.Key);
        }

        return activeOrders.Where(order => order.IsActive).ToArray();
    }

    private async Task EnsureOppositeGridOrderAsync(
        BotState state,
        IReadOnlyList<GridLevel> levels,
        GridOrder filledOrder,
        CancellationToken cancellationToken)
    {
        if (state.IsPaused)
        {
            return;
        }

        GridLevel? nextLevel = filledOrder.Side == TradeSide.Buy
            ? _strategy.GetNextUpperLevel(levels, filledOrder.Price)
            : _strategy.GetNextLowerLevel(levels, filledOrder.Price);

        if (nextLevel is null)
        {
            return;
        }

        if (!_strategy.IsWithinTradingRange(_gridOptions, nextLevel.Price))
        {
            return;
        }

        if (!IsAtLeastOneStepAway(filledOrder.Side, filledOrder.Price, nextLevel.Price))
        {
            _logger.LogInformation(
                "Follow-up order at {NextPrice} skipped after {Side} fill at {FillPrice} because it is not at least one grid step away.",
                nextLevel.Price,
                filledOrder.Side,
                filledOrder.Price);
            return;
        }

        var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        if (HasActiveOrderAtLevel(activeOrders, nextLevel.Price) ||
            await _repository.GetActiveOrderAtLevelAsync(
                _gridOptions.Symbol,
                filledOrder.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy,
                nextLevel.Price,
                cancellationToken) is not null)
        {
            return;
        }

        var wallet = _appOptions.TradingMode == TradingMode.Paper
            ? null
            : await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, _quoteAsset, _baseAsset);
        var quantity = filledOrder.Quantity;
        if (quantity <= 0m)
        {
            return;
        }

        if (filledOrder.Side == TradeSide.Buy)
        {
            if (GetAvailableBaseBalance(state, activeOrders, wallet) < quantity)
            {
                return;
            }

            if (!await HasMinimumNetProfitAsync(TradeSide.Sell, filledOrder.Price, nextLevel.Price, quantity, filledOrder.FeePaid, cancellationToken))
            {
                _logger.LogInformation(
                    "Follow-up sell order at {Price} skipped after buy because net profit is below minimum.",
                    nextLevel.Price);
                return;
            }
        }
        else
        {
            var orderNotional = quantity * nextLevel.Price;
            var violations = _riskManager.ValidateOrderPlacement(
                _riskOptions,
                _gridOptions,
                state,
                activeOrders,
                state.LastObservedPrice ?? nextLevel.Price,
                orderNotional,
                _riskOptions.MinOrderSizeUsdt,
                GetAvailableQuoteBalance(state, activeOrders, wallet));

            if (violations.Count > 0)
            {
                _logger.LogWarning(
                    "Follow-up buy order at {Price} skipped after fill: {Violations}",
                    nextLevel.Price,
                    string.Join(" | ", violations));
                return;
            }
        }

        var followUpSide = filledOrder.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        var expectedProfitPercent = filledOrder.Side == TradeSide.Buy
            ? ExpectedProfitFilter.CalculateLongRoundTripPercent(filledOrder.Price, nextLevel.Price)
            : ExpectedProfitFilter.CalculateLongRoundTripPercent(nextLevel.Price, filledOrder.Price);

        await ExecuteStrategyDecisionAsync(
            new StrategyExecutionDecision
            {
                OrderIntents =
                [
                    new OrderIntent(
                        followUpSide,
                        nextLevel.Price,
                        quantity,
                        filledOrder.OrderLinkId,
                        filledOrder.StrategySource,
                        expectedProfitPercent)
                ]
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<GridOrder>> ExecuteStrategyDecisionAsync(
        StrategyExecutionDecision decision,
        CancellationToken cancellationToken)
    {
        var createdOrders = new List<GridOrder>();

        foreach (var intent in decision.OrderIntents)
        {
            if (!ShouldAllowOrderByExpectedProfit(intent))
            {
                continue;
            }

            createdOrders.Add(await ExecuteOrderIntentAsync(intent, cancellationToken));
        }

        return createdOrders;
    }

    private bool ShouldAllowOrderByExpectedProfit(OrderIntent intent)
    {
        if (intent.SkipExpectedProfitFilter)
        {
            _logger.LogInformation(
                "Expected profit filter bypassed for {Source} {Side} at {Price} because the order is a risk-reduction exit.",
                NormalizeOrderSource(intent.StrategySource, intent.ParentOrderLinkId),
                intent.Side,
                intent.Price);
            return true;
        }

        var source = NormalizeOrderSource(intent.StrategySource, intent.ParentOrderLinkId);
        var expectedProfitPercent = intent.ExpectedProfitPercent ?? 0m;
        var decision = _expectedProfitFilter.Evaluate(_gridOptions, intent.Side, expectedProfitPercent, source);
        if (decision.IsAllowed)
        {
            return true;
        }

        _logger.LogInformation(
            "{Source} {Side} order at {Price} skipped by expected profit filter. Expected: {ExpectedProfitPercent:F4}%, required: {RequiredProfitPercent:F4}%.",
            source,
            intent.Side,
            intent.Price,
            decision.ExpectedProfitPercent,
            decision.RequiredProfitPercent);
        return false;
    }

    private Task<GridOrder> ExecuteOrderIntentAsync(OrderIntent intent, CancellationToken cancellationToken) =>
        PlaceOrderAsync(intent.Side, intent.Price, intent.Quantity, intent.ParentOrderLinkId, intent.StrategySource, cancellationToken);

    private async Task<GridOrder> PlaceOrderAsync(
        TradeSide side,
        decimal price,
        decimal quantity,
        string? parentOrderLinkId,
        string? strategySource,
        CancellationToken cancellationToken)
    {
        var orderLinkId = OrderLinkIdFactory.Create(side);
        var now = DateTimeOffset.UtcNow;
        var order = new GridOrder
        {
            OrderLinkId = orderLinkId,
            Symbol = _gridOptions.Symbol,
            Category = _gridOptions.Category,
            Side = side,
            Price = price,
            Quantity = quantity,
            Status = OrderStatus.New,
            TradingMode = _appOptions.TradingMode,
            ParentOrderLinkId = parentOrderLinkId,
            StrategySource = NormalizeOrderSource(strategySource, parentOrderLinkId),
            CreatedAt = now,
            UpdatedAt = now
        };

        if (_appOptions.TradingMode != TradingMode.Paper)
        {
            var ack = await _bybitRestClient.CreateOrderAsync(
                new BybitCreateOrderRequest
                {
                    Category = _gridOptions.Category,
                    Symbol = _gridOptions.Symbol,
                    Side = side.ToString(),
                    OrderType = "Limit",
                    Qty = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Price = price.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    TimeInForce = "GTC",
                    OrderLinkId = orderLinkId
                },
                cancellationToken);

            order.BybitOrderId = ack.OrderId;
        }

        await _repository.UpsertOrderAsync(order, cancellationToken);
        _logger.LogInformation("Created order. Side: {Side}, Price: {Price}, Qty: {Qty}, LinkId: {OrderLinkId}", side, price, quantity, orderLinkId);
        await _notifier.NotifyAsync(
            $"Order created: `{side}` `{quantity}` `{_baseAsset}` at `{price}`. LinkId: `{orderLinkId}`",
            cancellationToken);

        return order;
    }

    private async Task CancelManagedOrderAsync(GridOrder order, CancellationToken cancellationToken)
    {
        if (!order.IsActive)
        {
            return;
        }

        if (_appOptions.TradingMode != TradingMode.Paper)
        {
            await _bybitRestClient.CancelOrderAsync(
                order.Category,
                order.Symbol,
                order.BybitOrderId,
                order.OrderLinkId,
                cancellationToken);
        }

        order.Status = OrderStatus.Cancelled;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.UpsertOrderAsync(order, cancellationToken);
        _logger.LogInformation("Cancelled order {OrderLinkId}.", order.OrderLinkId);
    }

    private decimal ApplyFillDelta(BotState state, TradeSide side, decimal fillQuantity, decimal fillPrice, decimal feeDelta)
    {
        if (fillQuantity <= 0m)
        {
            return 0m;
        }

        decimal pnlDelta;
        if (side == TradeSide.Buy)
        {
            var totalCostBefore = state.BaseAssetQuantity * state.AverageEntryPrice;
            var totalCostAfter = totalCostBefore + (fillQuantity * fillPrice);
            state.BaseAssetQuantity += fillQuantity;
            state.AverageEntryPrice = state.BaseAssetQuantity > 0m ? totalCostAfter / state.BaseAssetQuantity : 0m;

            if (_appOptions.TradingMode == TradingMode.Paper)
            {
                state.QuoteAssetBalance -= (fillQuantity * fillPrice) + feeDelta;
            }

            pnlDelta = -feeDelta;
        }
        else
        {
            var effectiveQuantity = Math.Min(fillQuantity, state.BaseAssetQuantity);
            var grossPnl = (effectiveQuantity * fillPrice) - (effectiveQuantity * state.AverageEntryPrice) - feeDelta;
            state.BaseAssetQuantity = Math.Max(0m, state.BaseAssetQuantity - effectiveQuantity);
            if (state.BaseAssetQuantity == 0m)
            {
                state.AverageEntryPrice = 0m;
            }

            if (_appOptions.TradingMode == TradingMode.Paper)
            {
                state.QuoteAssetBalance += (effectiveQuantity * fillPrice) - feeDelta;
            }

            pnlDelta = grossPnl;
        }

        state.TotalRealizedPnl += pnlDelta;
        state.DailyRealizedPnl += pnlDelta;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        return pnlDelta;
    }

    private decimal GetAvailableQuoteBalance(
        BotState state,
        IReadOnlyCollection<GridOrder> activeOrders,
        BybitWalletBalance? wallet)
    {
        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var reserved = activeOrders
                .Where(order => order.Side == TradeSide.Buy)
                .Sum(order => (order.Quantity - order.FilledQuantity) * order.Price);

            return state.QuoteAssetBalance - reserved;
        }

        return Math.Max(0m, wallet?.GetCoinWalletBalance(_quoteAsset) - wallet?.GetCoinLockedBalance(_quoteAsset) ?? 0m);
    }

    private decimal GetAvailableBaseBalance(
        BotState state,
        IReadOnlyCollection<GridOrder> activeOrders,
        BybitWalletBalance? wallet)
    {
        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var reserved = activeOrders
                .Where(order => order.Side == TradeSide.Sell)
                .Sum(order => order.Quantity - order.FilledQuantity);

            return state.BaseAssetQuantity - reserved;
        }

        return Math.Max(0m, wallet?.GetCoinWalletBalance(_baseAsset) - wallet?.GetCoinLockedBalance(_baseAsset) ?? 0m);
    }

    private decimal GetOrderSizeUsdt(TradeSide side, decimal levelPrice, BotState state)
    {
        var orderSize = _gridOptions.OrderSizeUsdt;

        if (_gridOptions.DynamicOrderSizeEnabled)
        {
            var midpoint = (_gridOptions.LowerPrice + _gridOptions.UpperPrice) / 2m;
            var multiplier = side == TradeSide.Buy && levelPrice < midpoint
                ? _gridOptions.DynamicLowerOrderMultiplier
                : _gridOptions.DynamicUpperOrderMultiplier;
            orderSize *= multiplier;
        }

        if (_gridOptions.DailyTakeProfitUsdt > 0m &&
            state.DailyRealizedPnl >= _gridOptions.DailyTakeProfitUsdt)
        {
            orderSize *= _gridOptions.DailyTakeProfitOrderMultiplier;
        }

        return Math.Max(orderSize, 0m);
    }

    private decimal GetDcaOrderSizeUsdt(DcaStrategyConfig config, BotState state)
    {
        var orderSize = config.OrderSizeUsdt is > 0m
            ? config.OrderSizeUsdt.Value
            : _gridOptions.OrderSizeUsdt;

        if (_gridOptions.DailyTakeProfitUsdt > 0m &&
            state.DailyRealizedPnl >= _gridOptions.DailyTakeProfitUsdt)
        {
            orderSize *= _gridOptions.DailyTakeProfitOrderMultiplier;
        }

        return Math.Max(orderSize, 0m);
    }

    private decimal GetSignalOrderSizeUsdt(SignalStrategyConfig config, BotState state)
    {
        var configuredOrderSize = config.SignalOrderSizeUsdt is > 0m
            ? config.SignalOrderSizeUsdt
            : config.OrderSizeUsdt;
        var orderSize = configuredOrderSize is > 0m
            ? configuredOrderSize.Value
            : _gridOptions.OrderSizeUsdt;

        if (_gridOptions.DailyTakeProfitUsdt > 0m &&
            state.DailyRealizedPnl >= _gridOptions.DailyTakeProfitUsdt)
        {
            orderSize *= _gridOptions.DailyTakeProfitOrderMultiplier;
        }

        return Math.Max(orderSize, 0m);
    }

    private decimal GetTrendOrderSizeUsdt(TrendFollowingStrategyConfig config, BotState state)
    {
        var configuredOrderSize = config.TrendOrderSizeUsdt is > 0m
            ? config.TrendOrderSizeUsdt
            : config.OrderSizeUsdt;
        var orderSize = configuredOrderSize is > 0m
            ? configuredOrderSize.Value
            : _gridOptions.OrderSizeUsdt;

        if (_gridOptions.DailyTakeProfitUsdt > 0m &&
            state.DailyRealizedPnl >= _gridOptions.DailyTakeProfitUsdt)
        {
            orderSize *= _gridOptions.DailyTakeProfitOrderMultiplier;
        }

        return Math.Max(orderSize, 0m);
    }

    private static decimal CalculateSignalBuyPrice(decimal currentPrice, SignalStrategyConfig config)
    {
        var offset = Math.Max(SignalMarketLikeLimitBufferPercent, GetSignalLimitOffsetPercent(config)) / 100m;
        return currentPrice * (1m + offset);
    }

    private static decimal CalculateSignalSellPrice(decimal currentPrice, SignalStrategyConfig config)
    {
        var offset = Math.Max(SignalMarketLikeLimitBufferPercent, GetSignalLimitOffsetPercent(config)) / 100m;
        return currentPrice * (1m - offset);
    }

    private static decimal CalculateTrendBuyPrice(decimal currentPrice, TrendFollowingStrategyConfig config)
    {
        var offset = Math.Max(SignalMarketLikeLimitBufferPercent, config.LimitOffsetPercent) / 100m;
        return currentPrice * (1m + offset);
    }

    private static decimal CalculateTrendSellPrice(decimal currentPrice, TrendFollowingStrategyConfig config)
    {
        var offset = Math.Max(SignalMarketLikeLimitBufferPercent, config.LimitOffsetPercent) / 100m;
        return currentPrice * (1m - offset);
    }

    private static bool ShouldStopLoss(
        SignalStrategyConfig config,
        decimal positionQuantity,
        decimal averageEntryPrice,
        decimal currentPrice)
    {
        var stopLossPercent = GetSignalStopLossPercent(config);
        if (positionQuantity <= 0m || averageEntryPrice <= 0m || stopLossPercent <= 0m)
        {
            return false;
        }

        return currentPrice <= averageEntryPrice * (1m - stopLossPercent / 100m);
    }

    private static bool ShouldTakeProfit(
        SignalStrategyConfig config,
        decimal positionQuantity,
        decimal averageEntryPrice,
        decimal currentPrice)
    {
        var takeProfitPercent = GetSignalTakeProfitPercent(config);
        if (positionQuantity <= 0m || averageEntryPrice <= 0m || takeProfitPercent <= 0m)
        {
            return false;
        }

        return currentPrice >= averageEntryPrice * (1m + takeProfitPercent / 100m);
    }

    private decimal GetSignalMaxPositionUsdt(SignalStrategyConfig config)
    {
        if (config.SignalMaxPositionUsdt is > 0m)
        {
            return config.SignalMaxPositionUsdt.Value;
        }

        return config.MaxPositionUsdt is > 0m ? config.MaxPositionUsdt.Value : _riskOptions.MaxPositionUsdt;
    }

    private static decimal GetSignalStopLossPercent(SignalStrategyConfig config) =>
        config.SignalStopLossPercent is > 0m ? config.SignalStopLossPercent.Value : config.StopLossPercent;

    private static decimal GetSignalTakeProfitPercent(SignalStrategyConfig config) =>
        config.SignalTakeProfitPercent is > 0m ? config.SignalTakeProfitPercent.Value : config.TakeProfitPercent;

    private static decimal GetSignalLimitOffsetPercent(SignalStrategyConfig config) =>
        config.SignalLimitOffsetPercent is > 0m ? config.SignalLimitOffsetPercent.Value : config.LimitOffsetPercent;

    private static bool ShouldTrendStopLoss(
        TrendFollowingStrategyConfig config,
        decimal positionQuantity,
        decimal averageEntryPrice,
        decimal currentPrice)
    {
        if (positionQuantity <= 0m || averageEntryPrice <= 0m || config.StopLossPercent <= 0m)
        {
            return false;
        }

        return currentPrice <= averageEntryPrice * (1m - config.StopLossPercent / 100m);
    }

    private static bool ShouldTrendTakeProfit(
        TrendFollowingStrategyConfig config,
        decimal positionQuantity,
        decimal averageEntryPrice,
        decimal currentPrice)
    {
        if (positionQuantity <= 0m || averageEntryPrice <= 0m || config.TakeProfitPercent <= 0m)
        {
            return false;
        }

        return currentPrice >= averageEntryPrice * (1m + config.TakeProfitPercent / 100m);
    }

    private static TrendFollowingSignal AnalyzeTrendFollowing(
        IReadOnlyCollection<Candle> candles,
        TrendFollowingStrategyConfig config)
    {
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (ordered.Length < 5)
        {
            return new TrendFollowingSignal(false, false, "insufficient-data", 0m, 0m, 0m, 0m);
        }

        var latest = ordered[^1];
        var lookback = Math.Min(Math.Max(2, config.BreakoutLookbackCandles), ordered.Length - 1);
        var previous = ordered.Take(ordered.Length - 1).TakeLast(lookback).ToArray();
        var resistance = previous.Max(candle => candle.High);
        var recentHigh = ordered.TakeLast(lookback).Max(candle => candle.High);
        var emaFast = CalculateEma(ordered.Select(candle => candle.Close), 9);
        var emaSlow = CalculateEma(ordered.Select(candle => candle.Close), 21);
        var trendStrength = latest.Close > 0m ? (emaFast - emaSlow) / latest.Close * 100m : 0m;
        var averageVolume = previous.TakeLast(Math.Min(20, previous.Length)).Average(candle => candle.Volume);
        var volumeRatio = averageVolume > 0m ? latest.Volume / averageVolume : 0m;
        var breakoutPrice = resistance * (1m + Math.Max(0m, config.BreakoutBufferPercent) / 100m);
        var pullbackPercent = recentHigh > 0m ? (recentHigh - latest.Close) / recentHigh * 100m : 0m;
        var shouldBuy = latest.Close >= breakoutPrice &&
            trendStrength >= config.MinTrendStrengthPercent &&
            volumeRatio >= config.MinVolumeRatio;
        var shouldExit = latest.Close < emaSlow ||
            trendStrength <= -config.MinTrendStrengthPercent ||
            pullbackPercent >= config.PullbackExitPercent;
        var exitReason = latest.Close < emaSlow
            ? "ema-breakdown"
            : trendStrength <= -config.MinTrendStrengthPercent
                ? "trend-reversal"
                : "pullback-exit";

        return new TrendFollowingSignal(
            shouldBuy,
            shouldExit,
            exitReason,
            decimal.Round(trendStrength, 4, MidpointRounding.AwayFromZero),
            decimal.Round(resistance, 8, MidpointRounding.AwayFromZero),
            decimal.Round(volumeRatio, 4, MidpointRounding.AwayFromZero),
            decimal.Round(pullbackPercent, 4, MidpointRounding.AwayFromZero));
    }

    private static decimal CalculateEma(IEnumerable<decimal> values, int period)
    {
        var orderedValues = values.ToArray();
        if (orderedValues.Length == 0)
        {
            return 0m;
        }

        var multiplier = 2m / (period + 1m);
        var ema = orderedValues[0];
        foreach (var value in orderedValues.Skip(1))
        {
            ema = (value - ema) * multiplier + ema;
        }

        return ema;
    }

    private static bool IsSignalCooldownActive(
        GridBotSettings profile,
        SignalStrategyConfig config,
        DateTimeOffset now,
        IReadOnlyCollection<GridOrder> orders)
    {
        var cooldownMinutes = Math.Max(0, config.CooldownMinutes);
        if (cooldownMinutes == 0)
        {
            return false;
        }

        var latestSignalOrder = GetSignalScopeOrders(profile, orders)
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault();

        return latestSignalOrder is not null &&
            now - latestSignalOrder.CreatedAt < TimeSpan.FromMinutes(cooldownMinutes);
    }

    private static bool IsTrendCooldownActive(
        GridBotSettings profile,
        TrendFollowingStrategyConfig config,
        DateTimeOffset now,
        IReadOnlyCollection<GridOrder> orders)
    {
        var cooldownMinutes = Math.Max(0, config.CooldownMinutes);
        if (cooldownMinutes == 0)
        {
            return false;
        }

        var latestTrendOrder = GetTrendScopeOrders(profile, orders)
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault();

        return latestTrendOrder is not null &&
            now - latestTrendOrder.CreatedAt < TimeSpan.FromMinutes(cooldownMinutes);
    }

    private static IEnumerable<GridOrder> GetDcaEntryScopeOrders(
        GridBotSettings profile,
        IEnumerable<GridOrder> orders)
    {
        return profile.StrategyType is TradingStrategyType.Combo or TradingStrategyType.Hybrid
            ? orders.Where(order => string.Equals(order.ParentOrderLinkId, DcaEntryMarker, StringComparison.Ordinal))
            : orders;
    }

    private static IEnumerable<GridOrder> GetBtdEntryScopeOrders(
        GridBotSettings profile,
        IEnumerable<GridOrder> orders)
    {
        return profile.StrategyType == TradingStrategyType.Hybrid
            ? orders.Where(order => string.Equals(order.ParentOrderLinkId, BtdEntryMarker, StringComparison.Ordinal))
            : orders;
    }

    private static IEnumerable<GridOrder> GetSignalScopeOrders(
        GridBotSettings profile,
        IEnumerable<GridOrder> orders)
    {
        return profile.StrategyType is TradingStrategyType.Signal or TradingStrategyType.Hybrid
            ? orders.Where(IsSignalOrder)
            : orders;
    }

    private static IEnumerable<GridOrder> GetTrendScopeOrders(
        GridBotSettings profile,
        IEnumerable<GridOrder> orders)
    {
        return profile.StrategyType is TradingStrategyType.TrendFollow
                or TradingStrategyType.TrendFollowing
                or TradingStrategyType.Breakout
                or TradingStrategyType.Hybrid
            ? orders.Where(IsTrendOrder)
            : orders;
    }

    private static bool IsSignalOrder(GridOrder order)
    {
        return string.Equals(order.ParentOrderLinkId, SignalEntryMarker, StringComparison.Ordinal) ||
            string.Equals(order.ParentOrderLinkId, SignalExitMarker, StringComparison.Ordinal);
    }

    private static bool IsTrendOrder(GridOrder order)
    {
        return string.Equals(order.ParentOrderLinkId, TrendEntryMarker, StringComparison.Ordinal) ||
            string.Equals(order.ParentOrderLinkId, TrendExitMarker, StringComparison.Ordinal);
    }

    private static bool IsProtectiveSellOnlyPhase(MarketPhase phase)
    {
        return phase is MarketPhase.Dump
            or MarketPhase.BreakoutDown
            or MarketPhase.HighVolatility
            or MarketPhase.Exhaustion;
    }

    private static bool IsGridSidewaysMarket(MarketRegimeAnalysis marketRegime, MarketPhaseResult marketPhase)
    {
        return marketRegime.Regime == MarketRegimeType.Range &&
            marketPhase.Phase == MarketPhase.RangeBound;
    }

    private static string ResolveGridSource(GridBotSettings? profile)
    {
        return profile?.StrategyType switch
        {
            TradingStrategyType.Combo => ComboGridSource,
            TradingStrategyType.Hybrid => HybridGridSource,
            _ => GridSource
        };
    }

    private static string ResolveDipEntrySource(GridBotSettings profile, string entryMarker)
    {
        return (profile.StrategyType, entryMarker) switch
        {
            (TradingStrategyType.Combo, DcaEntryMarker) => ComboDcaSource,
            (TradingStrategyType.Hybrid, DcaEntryMarker) => HybridDcaSource,
            (TradingStrategyType.Hybrid, BtdEntryMarker) => HybridBtdSource,
            (_, DcaEntryMarker) => DcaSource,
            (_, BtdEntryMarker) => BtdSource,
            _ => NormalizeOrderSource(null, entryMarker)
        };
    }

    private static bool IsGridSource(string? source)
    {
        return string.Equals(source, GridSource, StringComparison.Ordinal) ||
            string.Equals(source, ComboGridSource, StringComparison.Ordinal) ||
            string.Equals(source, HybridGridSource, StringComparison.Ordinal);
    }

    private static string NormalizeOrderSource(string? source, string? parentOrderLinkId)
    {
        if (!string.IsNullOrWhiteSpace(source) &&
            !string.Equals(source, "Managed", StringComparison.OrdinalIgnoreCase))
        {
            return source.Trim();
        }

        return parentOrderLinkId switch
        {
            DcaEntryMarker => DcaSource,
            BtdEntryMarker => BtdSource,
            SignalEntryMarker or SignalExitMarker => SignalSource,
            TrendEntryMarker or TrendExitMarker => TrendSource,
            ReduceOnlyExitMarker => ReduceOnlySource,
            _ => GridSource
        };
    }

    private DcaStrategyConfig ParseDcaStrategyConfig(GridBotSettings profile)
    {
        try
        {
            return JsonSerializer.Deserialize<DcaStrategyConfig>(
                    string.IsNullOrWhiteSpace(profile.StrategyConfigJson) ? "{}" : profile.StrategyConfigJson,
                    StrategyJsonOptions)
                ?? new DcaStrategyConfig();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Invalid DCA strategy config for {Symbol}. Falling back to defaults.",
                profile.Symbol);
            return new DcaStrategyConfig();
        }
    }

    private BtdStrategyConfig ParseBtdStrategyConfig(GridBotSettings profile)
    {
        try
        {
            return JsonSerializer.Deserialize<BtdStrategyConfig>(
                    string.IsNullOrWhiteSpace(profile.StrategyConfigJson) ? "{}" : profile.StrategyConfigJson,
                    StrategyJsonOptions)
                ?? new BtdStrategyConfig();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Invalid BTD strategy config for {Symbol}. Falling back to defaults.",
                profile.Symbol);
            return new BtdStrategyConfig();
        }
    }

    private SignalStrategyConfig ParseSignalStrategyConfig(GridBotSettings profile)
    {
        try
        {
            return JsonSerializer.Deserialize<SignalStrategyConfig>(
                    string.IsNullOrWhiteSpace(profile.StrategyConfigJson) ? "{}" : profile.StrategyConfigJson,
                    StrategyJsonOptions)
                ?? new SignalStrategyConfig();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Invalid signal strategy config for {Symbol}. Falling back to defaults.",
                profile.Symbol);
            return new SignalStrategyConfig();
        }
    }

    private TrendFollowingStrategyConfig ParseTrendFollowingStrategyConfig(GridBotSettings profile)
    {
        try
        {
            return JsonSerializer.Deserialize<TrendFollowingStrategyConfig>(
                    string.IsNullOrWhiteSpace(profile.StrategyConfigJson) ? "{}" : profile.StrategyConfigJson,
                    StrategyJsonOptions)
                ?? new TrendFollowingStrategyConfig();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Invalid trend-following strategy config for {Symbol}. Falling back to defaults.",
                profile.Symbol);
            return new TrendFollowingStrategyConfig();
        }
    }

    private ComboStrategyConfig ParseComboStrategyConfig(GridBotSettings profile)
    {
        try
        {
            return JsonSerializer.Deserialize<ComboStrategyConfig>(
                    string.IsNullOrWhiteSpace(profile.StrategyConfigJson) ? "{}" : profile.StrategyConfigJson,
                    StrategyJsonOptions)
                ?? new ComboStrategyConfig();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Invalid combo strategy config for {Symbol}. Falling back to defaults.",
                profile.Symbol);
            return new ComboStrategyConfig();
        }
    }

    private static bool ShouldUseDcaFollowUp(GridBotSettings? profile, GridOrder order)
    {
        if (profile?.StrategyType == TradingStrategyType.Dca)
        {
            return true;
        }

        return profile?.StrategyType is (TradingStrategyType.Combo or TradingStrategyType.Hybrid) &&
            string.Equals(order.ParentOrderLinkId, DcaEntryMarker, StringComparison.Ordinal);
    }

    private static bool ShouldUseBtdFollowUp(GridBotSettings? profile, GridOrder order)
    {
        return profile?.StrategyType is (TradingStrategyType.Btd or TradingStrategyType.Hybrid) &&
            string.Equals(order.ParentOrderLinkId, BtdEntryMarker, StringComparison.Ordinal);
    }

    private static bool ShouldSkipGridFollowUp(GridBotSettings? profile, GridOrder order)
    {
        if (profile?.StrategyType is TradingStrategyType.ReduceOnly
                or TradingStrategyType.NoTrade
                or TradingStrategyType.Pause)
        {
            return true;
        }

        return profile?.StrategyType is (TradingStrategyType.Signal
                or TradingStrategyType.TrendFollow
                or TradingStrategyType.TrendFollowing
                or TradingStrategyType.Breakout
                or TradingStrategyType.Hybrid) &&
            (IsSignalOrder(order) || IsTrendOrder(order));
    }

    private async Task<bool> HasMinimumNetProfitForOrderAsync(
        GridBotSettings? profile,
        BotState state,
        GridOrder order,
        decimal remainingQuantity,
        CancellationToken cancellationToken)
    {
        var entryPrice = state.AverageEntryPrice;
        var entryFee = 0m;

        if (profile?.StrategyType is (TradingStrategyType.Dca
                or TradingStrategyType.Combo
                or TradingStrategyType.Btd
                or TradingStrategyType.Hybrid) &&
            !string.IsNullOrWhiteSpace(order.ParentOrderLinkId))
        {
            var parentOrder = await _repository.GetOrderByLinkIdAsync(order.ParentOrderLinkId, cancellationToken);
            if (parentOrder is null ||
                parentOrder.Side != TradeSide.Buy ||
                parentOrder.FilledQuantity <= 0m)
            {
                _logger.LogInformation(
                    "Sell order {OrderLinkId} skipped because parent buy {ParentOrderLinkId} is missing or not filled.",
                    order.OrderLinkId,
                    order.ParentOrderLinkId);
                return false;
            }

            entryPrice = parentOrder.AverageFillPrice > 0m ? parentOrder.AverageFillPrice : parentOrder.Price;
            entryFee = parentOrder.FeePaid > 0m
                ? parentOrder.FeePaid * decimal.Min(1m, remainingQuantity / parentOrder.FilledQuantity)
                : 0m;
        }

        var hasMinimumNetProfit = await HasMinimumNetProfitAsync(
            TradeSide.Sell,
            entryPrice,
            order.Price,
            remainingQuantity,
            entryFee,
            cancellationToken);
        if (!hasMinimumNetProfit)
        {
            return false;
        }

        var expectedProfitPercent = ExpectedProfitFilter.CalculateLongRoundTripPercent(entryPrice, order.Price);
        return _expectedProfitFilter
            .Evaluate(_gridOptions, TradeSide.Sell, expectedProfitPercent, NormalizeOrderSource(order.StrategySource, order.ParentOrderLinkId))
            .IsAllowed;
    }

    private async Task<bool> HasMinimumNetProfitAsync(
        TradeSide closingSide,
        decimal entryPrice,
        decimal exitPrice,
        decimal quantity,
        decimal knownEntryFee,
        CancellationToken cancellationToken)
    {
        if (quantity <= 0m || entryPrice <= 0m)
        {
            return true;
        }

        var exitFee = await EstimateFeeAsync(exitPrice * quantity, cancellationToken);
        var entryFee = knownEntryFee > 0m ? knownEntryFee : await EstimateFeeAsync(entryPrice * quantity, cancellationToken);
        var grossPnl = closingSide == TradeSide.Sell
            ? (exitPrice - entryPrice) * quantity
            : (entryPrice - exitPrice) * quantity;
        var netPnl = grossPnl - entryFee - exitFee;

        return netPnl >= _gridOptions.MinNetProfitUsdt;
    }

    private decimal CalculateFee(decimal tradedNotional) => tradedNotional * (_gridOptions.FeePercent / 100m);

    private async Task<decimal> EstimateFeeAsync(decimal tradedNotional, CancellationToken cancellationToken)
    {
        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            return CalculateFee(tradedNotional);
        }

        var feeRate = await GetConservativeFeeRateAsync(cancellationToken);
        return tradedNotional * feeRate;
    }

    private async Task<decimal> GetConservativeFeeRateAsync(CancellationToken cancellationToken)
    {
        var cachedKey = $"{_gridOptions.Category}:{_gridOptions.Symbol}";
        if (_feeRates.TryGetValue(cachedKey, out var cachedFeeRate) &&
            cachedFeeRate.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cachedFeeRate.FeeRate;
        }

        var fallbackFeeRate = _gridOptions.FeePercent / 100m;
        try
        {
            var bybitFeeRate = await _bybitRestClient.GetFeeRateAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
            var conservativeFeeRate = Math.Max(bybitFeeRate.MakerFeeRate, bybitFeeRate.TakerFeeRate);
            if (conservativeFeeRate <= 0m)
            {
                conservativeFeeRate = fallbackFeeRate;
            }

            _feeRates[cachedKey] = new CachedFeeRate(conservativeFeeRate, DateTimeOffset.UtcNow.AddMinutes(30));
            return conservativeFeeRate;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to fetch Bybit fee rate for {Symbol}. Falling back to FEE_PERCENT={FeePercent}.",
                _gridOptions.Symbol,
                _gridOptions.FeePercent);

            _feeRates[cachedKey] = new CachedFeeRate(fallbackFeeRate, DateTimeOffset.UtcNow.AddMinutes(5));
            return fallbackFeeRate;
        }
    }

    private static bool HasActiveOrderAtLevel(IEnumerable<GridOrder> activeOrders, decimal price) =>
        activeOrders.Any(order => order.IsActive && order.Price == price);

    private sealed record CachedFeeRate(decimal FeeRate, DateTimeOffset ExpiresAt);

    private bool IsAtLeastOneStepAway(TradeSide filledSide, decimal fillPrice, decimal nextPrice)
    {
        var minStep = _gridOptions.Step * 0.999m;
        return filledSide == TradeSide.Buy
            ? nextPrice - fillPrice >= minStep
            : fillPrice - nextPrice >= minStep;
    }

    private void ValidateAccountConfiguration()
    {
        if (_appOptions.TradingMode is TradingMode.Testnet or TradingMode.Mainnet &&
            (string.IsNullOrWhiteSpace(_bybitOptions.ApiKey) || string.IsNullOrWhiteSpace(_bybitOptions.ApiSecret)))
        {
            throw new InvalidOperationException("BYBIT_API_KEY and BYBIT_API_SECRET must be configured for testnet/mainnet.");
        }

        if (_appOptions.TradingMode == TradingMode.Mainnet)
        {
            _logger.LogWarning("Bot is configured for mainnet trading.");
        }
    }

    private void ValidateStartupConfiguration()
    {
        TradingCategoryGuard.ValidateSpotWorkerCategory(_gridOptions.Category, _futuresOptions.Enabled);

        if (_gridOptions.LowerPrice >= _gridOptions.UpperPrice)
        {
            throw new InvalidOperationException("GRID_LOWER_PRICE must be lower than GRID_UPPER_PRICE.");
        }

        if (_gridOptions.StopLowerPrice >= _gridOptions.LowerPrice)
        {
            throw new InvalidOperationException("STOP_LOWER_PRICE must be lower than GRID_LOWER_PRICE.");
        }

        if (_gridOptions.StopUpperPrice <= _gridOptions.UpperPrice)
        {
            throw new InvalidOperationException("STOP_UPPER_PRICE must be higher than GRID_UPPER_PRICE.");
        }

        if (_gridOptions.DynamicLowerOrderMultiplier <= 0m ||
            _gridOptions.DynamicUpperOrderMultiplier <= 0m ||
            _gridOptions.DailyTakeProfitOrderMultiplier <= 0m)
        {
            throw new InvalidOperationException("Dynamic and take-profit order multipliers must be positive.");
        }

        if (_gridOptions.AutoRecenterEnabled &&
            (_gridOptions.AutoRecenterLookbackCandles < 5 ||
             _gridOptions.AutoRecenterMinShiftSteps <= 0 ||
             _gridOptions.AutoRecenterPaddingSteps < 0))
        {
            throw new InvalidOperationException("Auto-recenter settings are invalid.");
        }

        if (_gridOptions.TrailingProtectionEnabled &&
            (_gridOptions.TrailingProtectionLookbackCandles < 5 ||
             _gridOptions.TrailingProtectionPumpPercent < 0m ||
             _gridOptions.TrailingProtectionPullbackPercent < 0m))
        {
            throw new InvalidOperationException("Trailing protection settings are invalid.");
        }

        if (_appOptions.TradingMode is TradingMode.Testnet or TradingMode.Mainnet &&
            (string.IsNullOrWhiteSpace(_bybitOptions.ApiKey) || string.IsNullOrWhiteSpace(_bybitOptions.ApiSecret)))
        {
            throw new InvalidOperationException("BYBIT_API_KEY and BYBIT_API_SECRET must be configured for testnet/mainnet.");
        }

        if (_appOptions.TradingMode == TradingMode.Mainnet)
        {
            if (_gridOptions.OrderSizeUsdt <= 0m)
            {
                throw new InvalidOperationException("ORDER_SIZE_USDT must be positive for mainnet.");
            }

            if (_riskOptions.MaxDailyLossUsdt <= 0m)
            {
                throw new InvalidOperationException("MAX_DAILY_LOSS_USDT must be positive for mainnet.");
            }
        }
    }

    private static (string BaseAsset, string QuoteAsset) ResolveAssets(string symbol)
    {
        foreach (var quote in new[] { "USDT", "USDC", "BTC", "ETH" })
        {
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
            {
                return (symbol[..^quote.Length], quote);
            }
        }

        throw new InvalidOperationException($"Cannot infer base/quote assets from symbol '{symbol}'.");
    }

    private static bool IsManagedOrder(string orderLinkId) =>
        orderLinkId.StartsWith("gb", StringComparison.OrdinalIgnoreCase);

    private static decimal FloorToStep(decimal value, decimal step)
    {
        return decimal.Round(Math.Floor(value / step) * step, 8, MidpointRounding.ToZero);
    }

    private static decimal CeilingToStep(decimal value, decimal step)
    {
        return decimal.Round(Math.Ceiling(value / step) * step, 8, MidpointRounding.AwayFromZero);
    }

    private static TradeSide ParseSide(string side) =>
        Enum.TryParse<TradeSide>(side, true, out var parsed) ? parsed : TradeSide.Buy;

    private static OrderStatus MapStatus(string orderStatus)
    {
        return orderStatus switch
        {
            "New" => OrderStatus.New,
            "PartiallyFilled" => OrderStatus.PartiallyFilled,
            "Filled" => OrderStatus.Filled,
            "Cancelled" or "PartiallyFilledCanceled" or "Deactivated" => OrderStatus.Cancelled,
            "Rejected" => OrderStatus.Rejected,
            _ => OrderStatus.Rejected
        };
    }
}
