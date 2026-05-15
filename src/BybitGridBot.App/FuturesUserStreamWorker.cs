using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Notifications;
using BybitGridBot.Risk;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public sealed class FuturesUserStreamWorker : BackgroundService
{
    private readonly AppOptions _appOptions;
    private readonly IBybitUserStreamClient _userStreamClient;
    private readonly BybitUserStreamTelemetry _userStreamTelemetry;
    private readonly FuturesOptions _futuresOptions;
    private readonly FuturesProtectionService _protectionService;
    private readonly FuturesRiskOptions _riskOptions;
    private readonly ILogger<FuturesUserStreamWorker> _logger;
    private readonly ITelegramNotifier _notifier;
    private readonly IGridRepository _repository;

    public FuturesUserStreamWorker(
        IOptions<AppOptions> appOptions,
        IOptions<FuturesOptions> futuresOptions,
        IOptions<FuturesRiskOptions> riskOptions,
        FuturesProtectionService protectionService,
        IBybitUserStreamClient userStreamClient,
        BybitUserStreamTelemetry userStreamTelemetry,
        IGridRepository repository,
        ITelegramNotifier notifier,
        ILogger<FuturesUserStreamWorker> logger)
    {
        _appOptions = appOptions.Value;
        _futuresOptions = futuresOptions.Value;
        _riskOptions = riskOptions.Value;
        _protectionService = protectionService;
        _userStreamClient = userStreamClient;
        _userStreamTelemetry = userStreamTelemetry;
        _repository = repository;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_futuresOptions.Enabled || !_futuresOptions.UserStreamEnabled)
        {
            _logger.LogInformation("Futures user stream disabled.");
            return;
        }

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            _logger.LogInformation("Futures user stream skipped in paper mode.");
            return;
        }

        _logger.LogInformation("Starting futures user stream. Mode: {TradingMode}", _appOptions.TradingMode);
        await _userStreamClient.RunAsync(HandleMessageAsync, stoppingToken);
    }

    private async Task HandleMessageAsync(BybitUserStreamMessage message, CancellationToken cancellationToken)
    {
        _userStreamTelemetry.MarkHandled(message.Type, message.Topic);
        switch (message.Type)
        {
            case BybitUserStreamMessageType.Order:
                foreach (var order in message.Orders)
                {
                    await HandleOrderAsync(order, cancellationToken);
                }

                break;
            case BybitUserStreamMessageType.Execution:
                foreach (var execution in message.Executions)
                {
                    await HandleExecutionAsync(execution, cancellationToken);
                }

                break;
            case BybitUserStreamMessageType.Position:
                foreach (var position in message.Positions)
                {
                    await HandlePositionAsync(position, cancellationToken);
                }

                break;
        }
    }

    private async Task HandleOrderAsync(BybitOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!FuturesOrderLinkIds.IsManaged(snapshot.OrderLinkId))
        {
            return;
        }

        var settings = await ResolveSettingsAsync(snapshot.Symbol, cancellationToken);
        if (settings is null)
        {
            return;
        }

        var existing = await _repository.GetOrderByLinkIdAsync(snapshot.OrderLinkId, cancellationToken);
        var order = MapOrder(settings, snapshot, existing);
        await _repository.UpsertOrderAsync(order, cancellationToken);
        await _repository.UpsertFuturesOrderAsync(MapFuturesOrder(settings, snapshot, order), cancellationToken);
        _logger.LogInformation(
            "Futures order stream synced. Symbol: {Symbol}, OrderLinkId: {OrderLinkId}, Status: {Status}, Filled: {FilledQuantity}",
            order.Symbol,
            order.OrderLinkId,
            order.Status,
            order.FilledQuantity);
    }

    private async Task HandleExecutionAsync(BybitExecutionSnapshot execution, CancellationToken cancellationToken)
    {
        if (!FuturesOrderLinkIds.IsManaged(execution.OrderLinkId) ||
            await _repository.FuturesFillExistsAsync(execution.ExecId, cancellationToken))
        {
            return;
        }

        var settings = await ResolveSettingsAsync(execution.Symbol, cancellationToken);
        if (settings is null)
        {
            return;
        }

        var existing = await _repository.GetOrderByLinkIdAsync(execution.OrderLinkId, cancellationToken);
        var side = ParseSide(execution.Side);
        var reduceOnly = existing?.ReduceOnly == true || execution.OrderLinkId.StartsWith("flc", StringComparison.OrdinalIgnoreCase);
        await _repository.AddFuturesFillAsync(new FuturesFillRecord
        {
            ExecId = execution.ExecId,
            OrderLinkId = execution.OrderLinkId,
            Symbol = execution.Symbol,
            Action = ResolveAction(side, reduceOnly),
            Side = side,
            ExecType = string.IsNullOrWhiteSpace(execution.ExecType) ? "Trade" : execution.ExecType,
            Quantity = execution.ExecQty,
            Price = execution.ExecPrice,
            Fee = execution.ExecFee,
            RealizedPnl = execution.ExecPnl,
            Funding = 0m,
            CreatedAt = execution.ExecTime == DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : execution.ExecTime
        }, cancellationToken);

        _logger.LogInformation(
            "Futures execution stream synced. Symbol: {Symbol}, OrderLinkId: {OrderLinkId}, ExecId: {ExecId}, Quantity: {Quantity}",
            execution.Symbol,
            execution.OrderLinkId,
            execution.ExecId,
            execution.ExecQty);
    }

    private async Task HandlePositionAsync(BybitPositionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var settings = await ResolveSettingsAsync(snapshot.Symbol, cancellationToken);
        if (settings is null)
        {
            return;
        }

        var state = await EnsureStateAsync(settings, snapshot.MarkPrice, cancellationToken);
        var position = MapPosition(settings, snapshot);
        await ApplyPositionAsync(state, position, cancellationToken);
        try
        {
            FuturesReconciliationService.ValidateMvpPosition(position);
        }
        catch (InvalidOperationException exception)
        {
            await RecordRiskPauseAsync(settings.Symbol, state, exception.Message, cancellationToken);
            return;
        }

        if (position.Size > 0m)
        {
            await _protectionService.EnsureProtectiveStopAsync(settings, position, cancellationToken);
        }

        if (!string.Equals(snapshot.PositionStatus, "Normal", StringComparison.OrdinalIgnoreCase))
        {
            await RecordRiskPauseAsync(
                settings.Symbol,
                state,
                $"Bybit positionStatus={snapshot.PositionStatus}. Futures profile paused.",
                cancellationToken);
            return;
        }

        var buffer = ResolveLiquidationBufferPercent(position);
        if (position.Size > 0m && buffer < _riskOptions.MinLiquidationBufferPercent)
        {
            await RecordRiskPauseAsync(
                settings.Symbol,
                state,
                "FUTURES_MIN_LIQUIDATION_BUFFER_PERCENT breached by user stream position update.",
                cancellationToken);
        }
    }

    private async Task<FuturesBotSettings?> ResolveSettingsAsync(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        var settings = await _repository.GetFuturesSettingsAsync(symbol, cancellationToken);
        return settings?.Enabled == true ? settings : null;
    }

    private async Task<BotState> EnsureStateAsync(
        FuturesBotSettings settings,
        decimal markPrice,
        CancellationToken cancellationToken)
    {
        var stateKey = FuturesStateKeys.ForSymbol(settings.Symbol);
        var state = await _repository.GetBotStateAsync(stateKey, cancellationToken);
        if (state is not null)
        {
            ResetDailyPnlIfNeeded(state);
            return state;
        }

        state = new BotState
        {
            Symbol = stateKey,
            TradingMode = _appOptions.TradingMode,
            LastObservedPrice = markPrice,
            PositionSide = "None",
            Leverage = settings.Leverage,
            MarginMode = settings.MarginMode.ToString(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repository.SaveBotStateAsync(state, cancellationToken);
        return state;
    }

    private async Task ApplyPositionAsync(BotState state, FuturesPositionSnapshot position, CancellationToken cancellationToken)
    {
        FuturesReconciliationService.ApplyPositionToState(state, position);
        await _repository.SaveBotStateAsync(state, cancellationToken);
        await _repository.UpsertFuturesPositionAsync(position, _appOptions.TradingMode, cancellationToken);
    }

    private async Task RecordRiskPauseAsync(
        string symbol,
        BotState state,
        string reason,
        CancellationToken cancellationToken)
    {
        await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = symbol,
            Source = "UserStream",
            IsAllowed = false,
            Reason = reason,
            Severity = RiskSeverity.Critical.ToString(),
            SuggestedAction = RiskSuggestedAction.PauseBot.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        if (!state.IsPaused || !string.Equals(state.PauseReason, reason, StringComparison.Ordinal))
        {
            state.IsPaused = true;
            state.PauseReason = reason;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.SaveBotStateAsync(state, cancellationToken);
            await _notifier.NotifyAsync($"Futures profile paused.\nSymbol: `{symbol}`\nReason: `{reason}`", cancellationToken);
        }

        _logger.LogWarning("Futures profile paused from user stream. Symbol: {Symbol}. Reason: {Reason}", symbol, reason);
    }

    private GridOrder MapOrder(
        FuturesBotSettings settings,
        BybitOrderSnapshot snapshot,
        GridOrder? existing)
    {
        var status = MapStatus(snapshot.OrderStatus);
        var updatedAt = snapshot.UpdatedAt == default ? DateTimeOffset.UtcNow : snapshot.UpdatedAt;
        var reduceOnly = existing?.ReduceOnly ??
            snapshot.ReduceOnly ||
            snapshot.OrderLinkId.StartsWith("flc", StringComparison.OrdinalIgnoreCase);

        return new GridOrder
        {
            OrderLinkId = snapshot.OrderLinkId,
            BybitOrderId = snapshot.OrderId,
            Symbol = snapshot.Symbol,
            Category = settings.Category,
            Side = ParseSide(snapshot.Side),
            Price = snapshot.Price == 0m ? existing?.Price ?? 0m : snapshot.Price,
            Quantity = snapshot.Quantity == 0m ? existing?.Quantity ?? 0m : snapshot.Quantity,
            FilledQuantity = snapshot.CumExecQty,
            AverageFillPrice = snapshot.AveragePrice == 0m ? existing?.AverageFillPrice ?? 0m : snapshot.AveragePrice,
            FeePaid = snapshot.FeePaid,
            Status = status,
            TradingMode = _appOptions.TradingMode,
            PositionSide = existing?.PositionSide ?? "Long",
            ReduceOnly = reduceOnly,
            PositionIdx = snapshot.PositionIdx != 0 ? snapshot.PositionIdx : existing?.PositionIdx ?? 0,
            Leverage = existing?.Leverage ?? settings.Leverage,
            MarginMode = existing?.MarginMode ?? settings.MarginMode.ToString(),
            EntryPrice = existing?.EntryPrice ?? 0m,
            MarkPrice = existing?.MarkPrice ?? 0m,
            LiquidationPrice = existing?.LiquidationPrice ?? 0m,
            UnrealizedPnl = existing?.UnrealizedPnl ?? 0m,
            RealizedPnl = existing?.RealizedPnl ?? 0m,
            CreatedAt = snapshot.CreatedAt == default ? existing?.CreatedAt ?? updatedAt : snapshot.CreatedAt,
            UpdatedAt = updatedAt,
            FilledAt = status == OrderStatus.Filled ? updatedAt : existing?.FilledAt
        };
    }

    private static FuturesOrderRecord MapFuturesOrder(
        FuturesBotSettings settings,
        BybitOrderSnapshot snapshot,
        GridOrder order) => new()
    {
        OrderLinkId = order.OrderLinkId,
        BybitOrderId = order.BybitOrderId,
        Symbol = order.Symbol,
        Category = order.Category,
        Action = ResolveAction(order.Side, order.ReduceOnly || snapshot.ReduceOnly),
        Side = order.Side,
        Price = order.Price,
        Quantity = order.Quantity,
        FilledQuantity = order.FilledQuantity,
        AverageFillPrice = order.AverageFillPrice,
        FeePaid = order.FeePaid,
        Status = order.Status,
        TradingMode = order.TradingMode,
        PositionSide = order.PositionSide ?? "Long",
        ReduceOnly = order.ReduceOnly || snapshot.ReduceOnly,
        PositionIdx = snapshot.PositionIdx != 0 ? snapshot.PositionIdx : order.PositionIdx,
        Leverage = order.Leverage == 0m ? settings.Leverage : order.Leverage,
        MarginMode = order.MarginMode ?? settings.MarginMode.ToString(),
        RealizedPnl = order.RealizedPnl,
        CreatedAt = order.CreatedAt,
        UpdatedAt = order.UpdatedAt,
        FilledAt = order.FilledAt
    };

    private static FuturesPositionSnapshot MapPosition(FuturesBotSettings settings, BybitPositionSnapshot position) => new()
    {
        Symbol = settings.Symbol,
        Category = settings.Category,
        Side = position.Size > 0m ? position.Side : "None",
        Size = position.Size,
        EntryPrice = position.AveragePrice,
        MarkPrice = position.MarkPrice,
        LiquidationPrice = position.LiquidationPrice,
        PositionValueUsdt = position.PositionValue,
        MarginUsedUsdt = position.PositionInitialMargin,
        Leverage = position.Leverage > 0m ? position.Leverage : settings.Leverage,
        UnrealizedPnl = position.UnrealizedPnl,
        RealizedPnl = position.RealizedPnl,
        PositionIdx = position.PositionIdx,
        UpdatedAt = position.UpdatedAt
    };

    private static decimal ResolveLiquidationBufferPercent(FuturesPositionSnapshot position)
    {
        if (position.LiquidationPrice <= 0m || position.MarkPrice <= 0m)
        {
            return 100m;
        }

        return Math.Abs(position.MarkPrice - position.LiquidationPrice) / position.MarkPrice * 100m;
    }

    private static FuturesTradeAction ResolveAction(TradeSide side, bool reduceOnly) =>
        side == TradeSide.Buy && !reduceOnly ? FuturesTradeAction.OpenLong : FuturesTradeAction.CloseLong;

    private static TradeSide ParseSide(string side) =>
        Enum.TryParse<TradeSide>(side, true, out var parsed) ? parsed : TradeSide.Buy;

    private static OrderStatus MapStatus(string orderStatus) =>
        orderStatus switch
        {
            "New" => OrderStatus.New,
            "PartiallyFilled" => OrderStatus.PartiallyFilled,
            "Filled" => OrderStatus.Filled,
            "Cancelled" or "PartiallyFilledCanceled" or "Deactivated" => OrderStatus.Cancelled,
            "Rejected" => OrderStatus.Rejected,
            _ => OrderStatus.Rejected
        };

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
}
