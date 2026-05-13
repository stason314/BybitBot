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
    private readonly AppOptions _appOptions;
    private readonly BybitOptions _bybitOptions;
    private readonly GridOptions _defaultGridOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly ILogger<GridBotWorker> _logger;
    private readonly MarketRegimeFilter _marketRegimeFilter;
    private readonly ITelegramNotifier _notifier;
    private readonly IGridRepository _repository;
    private readonly RiskManager _riskManager;
    private readonly RiskOptions _riskOptions;
    private readonly GridStrategy _strategy;
    private readonly Dictionary<string, GridOptions> _runningProfiles = new(StringComparer.OrdinalIgnoreCase);
    private GridOptions _gridOptions;
    private string _baseAsset;
    private string _quoteAsset;

    public GridBotWorker(
        IOptions<AppOptions> appOptions,
        IOptions<BybitOptions> bybitOptions,
        IOptions<GridOptions> gridOptions,
        IOptions<RiskOptions> riskOptions,
        IBybitRestClient bybitRestClient,
        GridStrategy strategy,
        RiskManager riskManager,
        MarketRegimeFilter marketRegimeFilter,
        IGridRepository repository,
        ITelegramNotifier notifier,
        ILogger<GridBotWorker> logger)
    {
        _appOptions = appOptions.Value;
        _bybitOptions = bybitOptions.Value;
        _defaultGridOptions = gridOptions.Value;
        _gridOptions = _defaultGridOptions;
        _riskOptions = riskOptions.Value;
        _bybitRestClient = bybitRestClient;
        _strategy = strategy;
        _riskManager = riskManager;
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
            _logger.LogInformation("Runtime grid profile removed. Symbol: {Symbol}", symbol);
            await _notifier.NotifyAsync($"Runtime profile removed. Symbol: `{symbol}`. Active orders cancelled.", cancellationToken);
        }
    }

    private async Task RunProfileCycleAsync(GridBotSettings profile, CancellationToken cancellationToken)
    {
        var refreshedGridOptions = RuntimeGridOptionsFactory.ToGridOptions(profile, _defaultGridOptions);
        var isKnownProfile = _runningProfiles.TryGetValue(refreshedGridOptions.Symbol, out var previousGridOptions);

        _gridOptions = refreshedGridOptions;
        (_baseAsset, _quoteAsset) = ResolveAssets(_gridOptions.Symbol);
        ValidateStartupConfiguration();

        if (isKnownProfile && previousGridOptions is not null &&
            !RuntimeGridOptionsFactory.IsSameTradingConfiguration(previousGridOptions, refreshedGridOptions))
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
                $"Runtime settings updated.\nSymbol: `{_gridOptions.Symbol}`\nRange: `{_gridOptions.LowerPrice}`-`{_gridOptions.UpperPrice}`\nStep: `{_gridOptions.Step}`",
                cancellationToken);
        }
        else if (!isKnownProfile)
        {
            _logger.LogInformation("Runtime grid profile activated. Symbol: {Symbol}", _gridOptions.Symbol);
            await _notifier.NotifyAsync($"Runtime profile activated. Symbol: `{_gridOptions.Symbol}`", cancellationToken);
        }

        _runningProfiles[_gridOptions.Symbol] = _gridOptions;

        var levels = await EnsureGridLevelsAsync(cancellationToken);
        var instrument = await _bybitRestClient.GetInstrumentInfoAsync(_gridOptions.Category, _gridOptions.Symbol, cancellationToken);
        var state = await EnsureBotStateAsync(cancellationToken);
        await RunCycleAsync(state, levels, instrument, cancellationToken);
    }

    private async Task<BotState> RunCycleAsync(
        BotState state,
        IReadOnlyList<GridLevel> levels,
        BybitInstrumentInfo instrument,
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

            await SimulatePaperFillsAsync(state, levels, currentPrice, cancellationToken);
        }
        else
        {
            await SynchronizeLiveOrdersAsync(state, cancellationToken);
            var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
            if (await HandleStopConditionsAsync(state, currentPrice, activeOrders, cancellationToken))
            {
                await _repository.SaveBotStateAsync(state, cancellationToken);
                return state;
            }
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
        if (_appOptions.TradingMode != TradingMode.Paper || state.IsInitialized || state.BaseAssetQuantity > 0m)
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

            await EnsureOppositeGridOrderAsync(state, levels, order, cancellationToken);
        }
    }

    private async Task SynchronizeLiveOrdersAsync(BotState state, CancellationToken cancellationToken)
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

                await EnsureOppositeGridOrderAsync(state, await _repository.GetGridLevelsAsync(_gridOptions.Symbol, cancellationToken), order, cancellationToken);
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
            if (await _repository.GetActiveOrderAtLevelAsync(_gridOptions.Symbol, TradeSide.Buy, level.Price, cancellationToken) is not null)
            {
                continue;
            }

            var quantity = instrument.RoundQuantity(_gridOptions.OrderSizeUsdt / level.Price);
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
                _gridOptions.OrderSizeUsdt,
                instrument.MinOrderAmount,
                availableUsdt);

            if (violations.Count > 0)
            {
                _logger.LogWarning("Buy order at {Price} skipped: {Violations}", level.Price, string.Join(" | ", violations));
                continue;
            }

            var createdOrder = await PlaceOrderAsync(TradeSide.Buy, level.Price, quantity, null, cancellationToken);
            activeOrders = activeOrders.Append(createdOrder).ToArray();
        }

        foreach (var level in sellLevels)
        {
            if (await _repository.GetActiveOrderAtLevelAsync(_gridOptions.Symbol, TradeSide.Sell, level.Price, cancellationToken) is not null)
            {
                continue;
            }

            var quantity = instrument.RoundQuantity(_gridOptions.OrderSizeUsdt / level.Price);
            if (quantity <= 0m || quantity < instrument.MinOrderQty)
            {
                continue;
            }

            var availableBase = GetAvailableBaseBalance(state, activeOrders, wallet);
            if (availableBase < quantity)
            {
                _logger.LogInformation("Sell order at {Price} skipped because base asset inventory is insufficient.", level.Price);
                continue;
            }

            var createdOrder = await PlaceOrderAsync(TradeSide.Sell, level.Price, quantity, null, cancellationToken);
            activeOrders = activeOrders.Append(createdOrder).ToArray();
        }
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

        if (await _repository.GetActiveOrderAtLevelAsync(
                _gridOptions.Symbol,
                filledOrder.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy,
                nextLevel.Price,
                cancellationToken) is not null)
        {
            return;
        }

        var activeOrders = await _repository.GetActiveOrdersAsync(_gridOptions.Symbol, cancellationToken);
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

        await PlaceOrderAsync(
            filledOrder.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy,
            nextLevel.Price,
            quantity,
            filledOrder.OrderLinkId,
            cancellationToken);
    }

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

    private decimal CalculateFee(decimal tradedNotional) => tradedNotional * (_gridOptions.FeePercent / 100m);

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
