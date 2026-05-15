using System.Globalization;
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
    private readonly FuturesRiskManager _riskManager;
    private readonly FuturesRiskOptions _riskOptions;
    private readonly ILogger<FuturesBotWorker> _logger;
    private readonly ITelegramNotifier _notifier;
    private readonly IGridRepository _repository;
    private readonly HashSet<string> _configuredTestnetSymbols = new(StringComparer.OrdinalIgnoreCase);

    public FuturesBotWorker(
        IOptions<AppOptions> appOptions,
        IOptions<FuturesOptions> futuresOptions,
        IOptions<FuturesRiskOptions> riskOptions,
        IBybitRestClient bybitRestClient,
        FuturesExecutionService executionService,
        FuturesRiskManager riskManager,
        IGridRepository repository,
        ITelegramNotifier notifier,
        ILogger<FuturesBotWorker> logger)
    {
        _appOptions = appOptions.Value;
        _futuresOptions = futuresOptions.Value;
        _riskOptions = riskOptions.Value;
        _bybitRestClient = bybitRestClient;
        _executionService = executionService;
        _riskManager = riskManager;
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

        if (_appOptions.TradingMode == TradingMode.Mainnet)
        {
            throw new InvalidOperationException("Futures worker refuses mainnet. Use paper/testnet until futures mainnet guard is implemented.");
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
        ValidateMvpSettings(settings);
        var ticker = await _bybitRestClient.GetTickerAsync(settings.Category, settings.Symbol, cancellationToken);
        var currentPrice = ticker.LastPrice;
        var instrument = await _bybitRestClient.GetInstrumentInfoAsync(settings.Category, settings.Symbol, cancellationToken);
        var candles = await _bybitRestClient.GetKlinesAsync(
            settings.Category,
            settings.Symbol,
            AnalysisDefaults.AutoRecommendationCandleInterval,
            AnalysisDefaults.AutoRecommendationLookbackCandles,
            cancellationToken);
        var state = await EnsureFuturesStateAsync(settings, currentPrice, cancellationToken);
        var position = _appOptions.TradingMode == TradingMode.Paper
            ? MapStateToPosition(settings, state, currentPrice)
            : await SyncLivePositionAsync(settings, currentPrice, state, cancellationToken);

        if (_appOptions.TradingMode == TradingMode.Testnet)
        {
            await EnsureTestnetConfigurationAsync(settings, cancellationToken);
        }

        var decision = BuildStrategyDecision(settings, candles, position, currentPrice, instrument);
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
                MarkPrice = currentPrice
            }, cancellationToken);

            position = result.Position;
            ApplyPositionToState(state, position);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            _logger.LogInformation(
                "Futures intent executed for {Symbol}. Action: {Action}. Paper: {IsPaper}. Message: {Message}",
                settings.Symbol,
                intent.Action,
                result.IsPaper,
                result.Message);
        }

        if (decision.TradeIntents.Count == 0)
        {
            ApplyPositionToState(state, position);
            await _repository.SaveBotStateAsync(state, cancellationToken);
        }
    }

    private async Task EnsureTestnetConfigurationAsync(FuturesBotSettings settings, CancellationToken cancellationToken)
    {
        if (!_configuredTestnetSymbols.Add(settings.Symbol))
        {
            return;
        }

        var leverage = FormatDecimal(settings.Leverage);
        await _bybitRestClient.SwitchPositionModeAsync(new BybitSwitchPositionModeRequest
        {
            Category = settings.Category,
            Symbol = settings.Symbol,
            Mode = 0
        }, cancellationToken);
        await _bybitRestClient.SwitchIsolatedMarginAsync(new BybitSwitchIsolatedMarginRequest
        {
            Category = settings.Category,
            Symbol = settings.Symbol,
            TradeMode = 1,
            BuyLeverage = leverage,
            SellLeverage = leverage
        }, cancellationToken);
        await _bybitRestClient.SetLeverageAsync(new BybitSetLeverageRequest
        {
            Category = settings.Category,
            Symbol = settings.Symbol,
            BuyLeverage = leverage,
            SellLeverage = leverage
        }, cancellationToken);
    }

    private static FuturesStrategyDecision BuildStrategyDecision(
        FuturesBotSettings settings,
        IReadOnlyList<Candle> candles,
        FuturesPositionSnapshot position,
        decimal currentPrice,
        BybitInstrumentInfo instrument)
    {
        if (settings.StrategyType == FuturesStrategyType.Pause ||
            candles.Count < 2 ||
            currentPrice <= 0m)
        {
            return FuturesStrategyDecision.Empty("Futures strategy paused or market data unavailable.");
        }

        if (settings.StrategyType == FuturesStrategyType.ReduceOnly)
        {
            return position.Size > 0m
                ? new FuturesStrategyDecision { TradeIntents = [CloseLong(settings, position, currentPrice, instrument, "reduce-only")] }
                : FuturesStrategyDecision.Empty("No futures position to reduce.");
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var lookback = ordered.TakeLast(Math.Min(60, ordered.Length)).ToArray();
        var first = lookback[0].Open > 0m ? lookback[0].Open : lookback[0].Close;
        var movePercent = first > 0m ? (currentPrice - first) / first * 100m : 0m;
        var resistance = lookback.Take(Math.Max(1, lookback.Length - 1)).Max(candle => candle.High);
        var support = lookback.Min(candle => candle.Low);
        var range = decimal.Max(instrument.TickSize, resistance - support);

        if (position.Size > 0m)
        {
            var stopPrice = position.EntryPrice * (1m - settings.StopLossPercent / 100m);
            var takeProfitPrice = position.EntryPrice * (1m + settings.TakeProfitPercent / 100m);
            if (currentPrice <= stopPrice || currentPrice >= takeProfitPrice || movePercent < -1m)
            {
                return new FuturesStrategyDecision { TradeIntents = [CloseLong(settings, position, currentPrice, instrument, "exit-signal")] };
            }

            return FuturesStrategyDecision.Empty("Existing futures long remains open.");
        }

        var shouldOpen = settings.StrategyType switch
        {
            FuturesStrategyType.TrendFollow => movePercent >= 0.8m,
            FuturesStrategyType.Breakout => currentPrice >= resistance,
            FuturesStrategyType.GridLongOnly => currentPrice <= support + (range * 0.35m),
            _ => false
        };
        if (!shouldOpen)
        {
            return FuturesStrategyDecision.Empty("No futures long entry signal.");
        }

        var entryIntent = OpenLong(settings, currentPrice, instrument);
        return entryIntent.Quantity > 0m
            ? new FuturesStrategyDecision { TradeIntents = [entryIntent] }
            : FuturesStrategyDecision.Empty("Futures entry quantity is below instrument precision.");
    }

    private static FuturesTradeIntent OpenLong(FuturesBotSettings settings, decimal currentPrice, BybitInstrumentInfo instrument)
    {
        var price = instrument.RoundPrice(currentPrice);
        var notional = settings.MaxNotionalUsdt * 0.25m;
        var quantity = instrument.RoundQuantity(notional / price);
        var stopLossPrice = instrument.RoundPrice(price * (1m - settings.StopLossPercent / 100m));
        var takeProfitPrice = instrument.RoundPrice(price * (1m + settings.TakeProfitPercent / 100m));
        return new FuturesTradeIntent
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            Action = FuturesTradeAction.OpenLong,
            Price = price,
            Quantity = quantity,
            Leverage = settings.Leverage,
            StopLossPrice = stopLossPrice,
            TakeProfitPrice = takeProfitPrice,
            LiquidationPrice = EstimateLongLiquidationPrice(price, settings.Leverage),
            PositionIdx = 0,
            OrderLinkId = CreateFuturesOrderLinkId(FuturesTradeAction.OpenLong),
            Reason = "futures-long-entry"
        };
    }

    private static FuturesTradeIntent CloseLong(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        decimal currentPrice,
        BybitInstrumentInfo instrument,
        string reason)
    {
        return new FuturesTradeIntent
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            Action = FuturesTradeAction.CloseLong,
            Price = instrument.RoundPrice(currentPrice),
            Quantity = instrument.RoundQuantity(position.Size),
            Leverage = settings.Leverage,
            PositionIdx = 0,
            OrderLinkId = CreateFuturesOrderLinkId(FuturesTradeAction.CloseLong),
            Reason = reason
        };
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

    private async Task<FuturesPositionSnapshot> SyncLivePositionAsync(
        FuturesBotSettings settings,
        decimal currentPrice,
        BotState state,
        CancellationToken cancellationToken)
    {
        var bybitPosition = await _bybitRestClient.GetPositionAsync(settings.Category, settings.Symbol, cancellationToken);
        var position = bybitPosition is null
            ? new FuturesPositionSnapshot { Symbol = settings.Symbol, Category = settings.Category, MarkPrice = currentPrice, Leverage = settings.Leverage }
            : MapBybitPosition(settings, bybitPosition, currentPrice);
        ValidateMvpPosition(position);
        ApplyPositionToState(state, position);
        await _repository.SaveBotStateAsync(state, cancellationToken);
        return position;
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

    private static FuturesPositionSnapshot MapBybitPosition(
        FuturesBotSettings settings,
        BybitPositionSnapshot position,
        decimal fallbackMarkPrice) => new()
    {
        Symbol = settings.Symbol,
        Category = settings.Category,
        Side = position.Size > 0m ? position.Side : "None",
        Size = position.Size,
        EntryPrice = position.AveragePrice,
        MarkPrice = position.MarkPrice > 0m ? position.MarkPrice : fallbackMarkPrice,
        LiquidationPrice = position.LiquidationPrice,
        PositionValueUsdt = position.PositionValue,
        MarginUsedUsdt = position.PositionInitialMargin,
        Leverage = position.Leverage > 0m ? position.Leverage : settings.Leverage,
        UnrealizedPnl = position.UnrealizedPnl,
        RealizedPnl = position.RealizedPnl,
        PositionIdx = position.PositionIdx,
        UpdatedAt = position.UpdatedAt
    };

    private static void ApplyPositionToState(BotState state, FuturesPositionSnapshot position)
    {
        var previousTotalRealizedPnl = state.TotalRealizedPnl;
        state.PositionSide = position.Side;
        state.BaseAssetQuantity = position.Size;
        state.AverageEntryPrice = position.EntryPrice;
        state.EntryPrice = position.EntryPrice;
        state.LastObservedPrice = position.MarkPrice;
        state.MarkPrice = position.MarkPrice;
        state.LiquidationPrice = position.LiquidationPrice;
        state.UnrealizedPnl = position.UnrealizedPnl;
        state.TotalRealizedPnl = position.RealizedPnl;
        state.DailyRealizedPnl += position.RealizedPnl - previousTotalRealizedPnl;
        state.PositionIdx = position.PositionIdx;
        state.Leverage = position.Leverage;
        state.MarginMode = "Isolated";
        state.ReduceOnly = false;
        state.IsInitialized = true;
        state.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void ValidateMvpSettings(FuturesBotSettings settings)
    {
        if (!string.Equals(settings.Category, "linear", StringComparison.OrdinalIgnoreCase) ||
            settings.MarginMode != FuturesMarginMode.Isolated ||
            settings.PositionMode != FuturesPositionMode.OneWay ||
            settings.Direction != FuturesDirection.LongOnly)
        {
            throw new InvalidOperationException("Futures worker MVP supports only linear, isolated, one-way, long-only profiles.");
        }
    }

    private static void ValidateMvpPosition(FuturesPositionSnapshot position)
    {
        if (position.PositionIdx != 0)
        {
            throw new InvalidOperationException("Futures worker MVP requires live positionIdx=0.");
        }

        if (position.Size > 0m && string.Equals(position.Side, "Sell", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Futures worker MVP does not manage short positions.");
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

    private static decimal EstimateLongLiquidationPrice(decimal entryPrice, decimal leverage) =>
        leverage > 0m ? decimal.Max(0m, entryPrice * (1m - (1m / leverage))) : 0m;

    private static string CreateFuturesOrderLinkId(FuturesTradeAction action)
    {
        var prefix = action == FuturesTradeAction.OpenLong ? "flo" : "flc";
        return $"{prefix}{DateTimeOffset.UtcNow:yyMMddHHmmss}{Guid.NewGuid():N}"[..35];
    }

    private static string FormatDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
