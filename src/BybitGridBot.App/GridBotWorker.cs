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

namespace BybitGridBot.App;

public sealed class GridBotWorker : BackgroundService
{
    private const string BtdEntryMarker = "btd-entry";
    private const string DcaEntryMarker = "dca-entry";
    private static readonly TimeSpan TimedAutoRecommendationApplyInterval = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions StrategyJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppOptions _appOptions;
    private readonly AutoStrategySelector _autoStrategySelector;
    private readonly BtdStrategy _btdStrategy;
    private readonly BybitOptions _bybitOptions;
    private readonly DcaStrategy _dcaStrategy;
    private readonly GridOptions _defaultGridOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly ILogger<GridBotWorker> _logger;
    private readonly MarketRegimeAnalyzer _marketRegimeAnalyzer;
    private readonly MarketRegimeFilter _marketRegimeFilter;
    private readonly ITelegramNotifier _notifier;
    private readonly IGridRepository _repository;
    private readonly RiskManager _riskManager;
    private readonly RiskOptions _riskOptions;
    private readonly IGridTradingStrategy _strategy;
    private readonly Dictionary<string, CachedFeeRate> _feeRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastTimedAutoApplyChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GridOptions> _runningProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GridBotSettings> _runningSettings = new(StringComparer.OrdinalIgnoreCase);
    private GridOptions _gridOptions;
    private string _baseAsset;
    private string _quoteAsset;

    public GridBotWorker(
        IOptions<AppOptions> appOptions,
        IOptions<BybitOptions> bybitOptions,
        IOptions<GridOptions> gridOptions,
        IOptions<RiskOptions> riskOptions,
        AutoStrategySelector autoStrategySelector,
        IBybitRestClient bybitRestClient,
        BtdStrategy btdStrategy,
        DcaStrategy dcaStrategy,
        IGridTradingStrategy strategy,
        RiskManager riskManager,
        MarketRegimeAnalyzer marketRegimeAnalyzer,
        MarketRegimeFilter marketRegimeFilter,
        IGridRepository repository,
        ITelegramNotifier notifier,
        ILogger<GridBotWorker> logger)
    {
        _appOptions = appOptions.Value;
        _autoStrategySelector = autoStrategySelector;
        _bybitOptions = bybitOptions.Value;
        _defaultGridOptions = gridOptions.Value;
        _gridOptions = _defaultGridOptions;
        _riskOptions = riskOptions.Value;
        _bybitRestClient = bybitRestClient;
        _btdStrategy = btdStrategy;
        _dcaStrategy = dcaStrategy;
        _strategy = strategy;
        _riskManager = riskManager;
        _marketRegimeAnalyzer = marketRegimeAnalyzer;
        _marketRegimeFilter = marketRegimeFilter;
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
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            foreach (var order in activeOrders)
            {
                await CancelManagedOrderAsync(order, cancellationToken);
            }

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

        var instrument = await _bybitRestClient.GetInstrumentInfoAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var state = await EnsureBotStateAsync(cancellationToken);
        if (profile.StrategyType == TradingStrategyType.NoTrade)
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

        var levels = await EnsureGridLevelsAsync(cancellationToken);
        if (profile.StrategyType == TradingStrategyType.Combo)
        {
            await RunComboCycleAsync(profile, state, levels, instrument, cancellationToken);
            return;
        }

        await RunCycleAsync(state, levels, instrument, null, cancellationToken);
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
            "1",
            120,
            cancellationToken);
        if (candles.Count == 0)
        {
            _logger.LogInformation("Timed auto-apply skipped for {Symbol}: market data is unavailable.", profile.Symbol);
            return profile;
        }

        var regime = _marketRegimeAnalyzer.Analyze(candles);
        var recommendation = _autoStrategySelector.Recommend(gridOptions, regime, candles);
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

        var state = await _repository.GetBotStateAsync(profile.Symbol, cancellationToken);
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

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(state, currentPrice, activeOrders, cancellationToken))
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
            if (await HandleStopConditionsAsync(state, currentPrice, activeOrders, cancellationToken))
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

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(state, currentPrice, activeOrders, cancellationToken))
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
            if (await HandleStopConditionsAsync(state, currentPrice, activeOrders, cancellationToken))
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
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        if (!_btdStrategy.IsDipTriggered(config, currentPrice, candles))
        {
            _logger.LogInformation("BTD entry skipped because dip trigger has not fired.");
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        var refreshedActiveOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        var allOrders = await _repository.GetOrdersAsync(_gridOptions.Symbol, cancellationToken);
        if (!_btdStrategy.CanOpenBuy(config, allOrders, DateTimeOffset.UtcNow))
        {
            _logger.LogInformation("BTD entry skipped because max buys or buy interval is active.");
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
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

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            await BootstrapPaperInventoryIfNeededAsync(state, levels, instrument, currentPrice, cancellationToken);

            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanRiskyActiveOrdersAsync(state, activeOrders, cancellationToken);
            await SimulatePaperFillsAsync(state, levels, profile, currentPrice, cancellationToken);
        }
        else
        {
            await SynchronizeLiveOrdersAsync(state, levels, profile, cancellationToken);
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }

            await CleanRiskyActiveOrdersAsync(state, activeOrders, cancellationToken);
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
            50,
            cancellationToken);

        var btcCandles = _gridOptions.BtcFilterEnabled
            ? await _bybitRestClient.GetKlinesAsync("spot", "BTCUSDT", _gridOptions.CandleInterval, 20, cancellationToken)
            : [];

        if (_marketRegimeFilter.ShouldBlockNewOrders(_gridOptions, symbolCandles, btcCandles))
        {
            _logger.LogInformation("Market regime filter blocks new grid orders for the current cycle.");
            await _repository.SaveBotStateAsync(state, cancellationToken);
            return state;
        }

        var activeGridOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
        activeGridOrders = await CleanRiskyActiveOrdersAsync(state, activeGridOrders, cancellationToken);
        var wallet = _appOptions.TradingMode == TradingMode.Paper
            ? null
            : await _bybitRestClient.GetWalletBalanceAsync(cancellationToken, _quoteAsset, _baseAsset);

        await EnsureGridOrdersAsync(state, levels, instrument, currentPrice, activeGridOrders, wallet, cancellationToken);
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
                else
                {
                    await EnsureOppositeGridOrderAsync(state, levels, order, cancellationToken);
                }
            }
        }

        await _repository.SaveBotStateAsync(state, cancellationToken);
    }

    private async Task<bool> HandleStopConditionsAsync(
        BotState state,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        if (_strategy.IsBelowStop(_gridOptions, currentPrice))
        {
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

    private async Task EnsureGridOrdersAsync(
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

            var decision = new StrategyDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Buy, level.Price, quantity)]
            };
            var createdOrders = await ExecuteStrategyDecisionAsync(decision, cancellationToken);
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

            var availableBase = GetAvailableBaseBalance(state, activeOrders, wallet);
            if (availableBase < quantity)
            {
                _logger.LogInformation("Sell order at {Price} skipped because base asset inventory is insufficient.", level.Price);
                continue;
            }

            var decision = new StrategyDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Sell, level.Price, quantity)]
            };
            var createdOrders = await ExecuteStrategyDecisionAsync(decision, cancellationToken);
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

        await ExecuteStrategyDecisionAsync(
            new StrategyDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Buy, limitPrice, quantity, entryMarker)]
            },
            cancellationToken);

        _logger.LogInformation(
            "{StrategyName} entry order created for {Symbol}. Price: {Price}, Quantity: {Quantity}, Config: {StrategyConfig}",
            strategyName,
            profile.Symbol,
            limitPrice,
            quantity,
            profile.StrategyConfigJson);
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

        await ExecuteStrategyDecisionAsync(
            new StrategyDecision
            {
                OrderIntents = [new OrderIntent(TradeSide.Sell, takeProfitPrice, quantity, filledOrder.OrderLinkId)]
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<GridOrder>> CleanRiskyActiveOrdersAsync(
        BotState state,
        IReadOnlyCollection<GridOrder> activeOrders,
        CancellationToken cancellationToken)
    {
        var cleanedOrders = await CancelUnprofitableSellOrdersAsync(state, activeOrders, cancellationToken);
        cleanedOrders = await CancelCrossSideOrdersAtSameLevelAsync(cleanedOrders, cancellationToken);
        cleanedOrders = await ReduceBuyExposureAfterDailyTakeProfitAsync(state, cleanedOrders, cancellationToken);

        return cleanedOrders.ToArray();
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
            var remainingQuantity = order.Quantity - order.FilledQuantity;
            if (remainingQuantity <= 0m)
            {
                continue;
            }

            if (await HasMinimumNetProfitAsync(TradeSide.Sell, state.AverageEntryPrice, order.Price, remainingQuantity, 0m, cancellationToken))
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

        await ExecuteStrategyDecisionAsync(
            new StrategyDecision
            {
                OrderIntents =
                [
                    new OrderIntent(
                        filledOrder.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy,
                        nextLevel.Price,
                        quantity,
                        filledOrder.OrderLinkId)
                ]
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<GridOrder>> ExecuteStrategyDecisionAsync(
        StrategyDecision decision,
        CancellationToken cancellationToken)
    {
        var createdOrders = new List<GridOrder>();

        foreach (var intent in decision.OrderIntents)
        {
            createdOrders.Add(await ExecuteOrderIntentAsync(intent, cancellationToken));
        }

        return createdOrders;
    }

    private Task<GridOrder> ExecuteOrderIntentAsync(OrderIntent intent, CancellationToken cancellationToken) =>
        PlaceOrderAsync(intent.Side, intent.Price, intent.Quantity, intent.ParentOrderLinkId, cancellationToken);

    private async Task<GridOrder> PlaceOrderAsync(
        TradeSide side,
        decimal price,
        decimal quantity,
        string? parentOrderLinkId,
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

    private static IEnumerable<GridOrder> GetDcaEntryScopeOrders(
        GridBotSettings profile,
        IEnumerable<GridOrder> orders)
    {
        return profile.StrategyType == TradingStrategyType.Combo
            ? orders.Where(order => string.Equals(order.ParentOrderLinkId, DcaEntryMarker, StringComparison.Ordinal))
            : orders;
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

        return profile?.StrategyType == TradingStrategyType.Combo &&
            string.Equals(order.ParentOrderLinkId, DcaEntryMarker, StringComparison.Ordinal);
    }

    private static bool ShouldUseBtdFollowUp(GridBotSettings? profile, GridOrder order)
    {
        return profile?.StrategyType == TradingStrategyType.Btd &&
            string.Equals(order.ParentOrderLinkId, BtdEntryMarker, StringComparison.Ordinal);
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

        if (profile?.StrategyType is TradingStrategyType.Dca or TradingStrategyType.Combo or TradingStrategyType.Btd &&
            !string.IsNullOrWhiteSpace(order.ParentOrderLinkId))
        {
            var parentOrder = await _repository.GetOrderByLinkIdAsync(order.ParentOrderLinkId, cancellationToken);
            if (parentOrder is not null)
            {
                entryPrice = parentOrder.AverageFillPrice > 0m ? parentOrder.AverageFillPrice : parentOrder.Price;
                entryFee = parentOrder.FeePaid;
            }
        }

        return await HasMinimumNetProfitAsync(
            TradeSide.Sell,
            entryPrice,
            order.Price,
            remainingQuantity,
            entryFee,
            cancellationToken);
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
