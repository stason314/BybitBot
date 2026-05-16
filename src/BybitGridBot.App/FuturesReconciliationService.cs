using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public sealed class FuturesReconciliationService
{
    private readonly AppOptions _appOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly FuturesProtectionService _protectionService;
    private readonly ILogger<FuturesReconciliationService> _logger;
    private readonly IGridRepository _repository;

    public FuturesReconciliationService(
        IOptions<AppOptions> appOptions,
        IBybitRestClient bybitRestClient,
        FuturesProtectionService protectionService,
        IGridRepository repository,
        ILogger<FuturesReconciliationService> logger)
    {
        _appOptions = appOptions.Value;
        _bybitRestClient = bybitRestClient;
        _protectionService = protectionService;
        _repository = repository;
        _logger = logger;
    }

    public async Task<FuturesReconciliationResult> ReconcileAsync(
        FuturesBotSettings settings,
        BotState state,
        decimal fallbackMarkPrice,
        CancellationToken cancellationToken)
    {
        var remoteOpenOrders = await _bybitRestClient.GetOpenOrdersAsync(settings.Category, settings.Symbol, cancellationToken);
        var remoteHistory = await _bybitRestClient.GetOrderHistoryAsync(settings.Category, settings.Symbol, null, cancellationToken);
        var remoteSnapshots = remoteOpenOrders
            .Concat(remoteHistory)
            .Where(snapshot => FuturesOrderLinkIds.IsManaged(snapshot.OrderLinkId))
            .GroupBy(snapshot => snapshot.OrderLinkId)
            .Select(group => group.OrderByDescending(snapshot => snapshot.UpdatedAt).First())
            .ToArray();

        var localOrders = (await _repository.GetOrdersAsync(settings.Symbol, cancellationToken))
            .Where(order =>
                string.Equals(order.Category, settings.Category, StringComparison.OrdinalIgnoreCase) &&
                FuturesOrderLinkIds.IsManaged(order.OrderLinkId))
            .ToDictionary(order => order.OrderLinkId, StringComparer.OrdinalIgnoreCase);

        var syncedOrders = 0;
        foreach (var snapshot in remoteSnapshots)
        {
            var order = MapSnapshot(settings, snapshot, localOrders.GetValueOrDefault(snapshot.OrderLinkId));
            await _repository.UpsertOrderAsync(order, cancellationToken);
            await _repository.UpsertFuturesOrderAsync(MapFuturesOrder(settings, snapshot, order), cancellationToken);
            syncedOrders++;
        }

        var knownRemoteOrderLinks = remoteSnapshots
            .Select(snapshot => snapshot.OrderLinkId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fixedHangingOrders = 0;
        foreach (var order in localOrders.Values.Where(order => order.IsActive && !knownRemoteOrderLinks.Contains(order.OrderLinkId)))
        {
            var specificHistory = await _bybitRestClient.GetOrderHistoryAsync(
                settings.Category,
                settings.Symbol,
                order.OrderLinkId,
                cancellationToken);
            var snapshot = specificHistory
                .Where(item => string.Equals(item.OrderLinkId, order.OrderLinkId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault();
            if (snapshot is not null)
            {
                var syncedOrder = MapSnapshot(settings, snapshot, order);
                await _repository.UpsertOrderAsync(syncedOrder, cancellationToken);
                await _repository.UpsertFuturesOrderAsync(MapFuturesOrder(settings, snapshot, syncedOrder), cancellationToken);
                syncedOrders++;
                continue;
            }

            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.UpsertOrderAsync(order, cancellationToken);
            await _repository.UpsertFuturesOrderAsync(MapFuturesOrder(settings, order), cancellationToken);
            fixedHangingOrders++;
            _logger.LogWarning(
                "Marked hanging futures order as cancelled locally. Symbol: {Symbol}, OrderLinkId: {OrderLinkId}",
                settings.Symbol,
                order.OrderLinkId);
        }

        var syncedFills = await SyncExecutionsAsync(settings, remoteSnapshots, localOrders, cancellationToken);
        var bybitPosition = await _bybitRestClient.GetPositionAsync(settings.Category, settings.Symbol, cancellationToken);
        var position = bybitPosition is null
            ? new FuturesPositionSnapshot
            {
                Symbol = settings.Symbol,
                Category = settings.Category,
                MarkPrice = fallbackMarkPrice,
                Leverage = settings.Leverage
            }
            : MapPosition(settings, bybitPosition, fallbackMarkPrice);
        ValidateMvpPosition(position);
        ApplyPositionToState(state, position);
        await _repository.SaveBotStateAsync(state, cancellationToken);
        await _repository.UpsertFuturesPositionAsync(position, _appOptions.TradingMode, cancellationToken);
        if (position.Size > 0m)
        {
            await _protectionService.EnsureProtectiveStopAsync(settings, position, cancellationToken);
        }

        return new FuturesReconciliationResult
        {
            Position = position,
            RemoteOpenOrderCount = remoteOpenOrders.Count,
            RemoteHistoryOrderCount = remoteHistory.Count,
            SyncedOrderCount = syncedOrders,
            SyncedFillCount = syncedFills,
            FixedHangingOrderCount = fixedHangingOrders
        };
    }

    private async Task<int> SyncExecutionsAsync(
        FuturesBotSettings settings,
        IReadOnlyCollection<BybitOrderSnapshot> remoteSnapshots,
        IReadOnlyDictionary<string, GridOrder> localOrders,
        CancellationToken cancellationToken)
    {
        var executions = await _bybitRestClient.GetExecutionsAsync(
            settings.Category,
            settings.Symbol,
            null,
            "Trade",
            cancellationToken);
        var remoteReduceOnlyByLinkId = remoteSnapshots
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.OrderLinkId))
            .ToDictionary(snapshot => snapshot.OrderLinkId, snapshot => snapshot.ReduceOnly, StringComparer.OrdinalIgnoreCase);
        var syncedFills = 0;

        foreach (var execution in executions.Where(execution => FuturesOrderLinkIds.IsManaged(execution.OrderLinkId)))
        {
            if (await _repository.FuturesFillExistsAsync(execution.ExecId, cancellationToken))
            {
                continue;
            }

            var reduceOnly = remoteReduceOnlyByLinkId.GetValueOrDefault(execution.OrderLinkId) ||
                (localOrders.TryGetValue(execution.OrderLinkId, out var localOrder) && localOrder.ReduceOnly);
            await _repository.AddFuturesFillAsync(MapFill(execution, reduceOnly), cancellationToken);
            syncedFills++;
        }

        if (syncedFills > 0)
        {
            _logger.LogInformation(
                "Synced {FillCount} futures execution fills for {Symbol}.",
                syncedFills,
                settings.Symbol);
        }

        return syncedFills;
    }

    private GridOrder MapSnapshot(
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
        Action = ResolveAction(order.Side, order.ReduceOnly, order.PositionSide),
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

    private static FuturesOrderRecord MapFuturesOrder(FuturesBotSettings settings, GridOrder order) => new()
    {
        OrderLinkId = order.OrderLinkId,
        BybitOrderId = order.BybitOrderId,
        Symbol = order.Symbol,
        Category = order.Category,
        Action = ResolveAction(order.Side, order.ReduceOnly, order.PositionSide),
        Side = order.Side,
        Price = order.Price,
        Quantity = order.Quantity,
        FilledQuantity = order.FilledQuantity,
        AverageFillPrice = order.AverageFillPrice,
        FeePaid = order.FeePaid,
        Status = order.Status,
        TradingMode = order.TradingMode,
        PositionSide = order.PositionSide ?? "Long",
        ReduceOnly = order.ReduceOnly,
        PositionIdx = order.PositionIdx,
        Leverage = order.Leverage == 0m ? settings.Leverage : order.Leverage,
        MarginMode = order.MarginMode ?? settings.MarginMode.ToString(),
        RealizedPnl = order.RealizedPnl,
        CreatedAt = order.CreatedAt,
        UpdatedAt = order.UpdatedAt,
        FilledAt = order.FilledAt
    };

    private static FuturesTradeAction ResolveAction(TradeSide side, bool reduceOnly, string? positionSide = null)
    {
        if (IsShort(positionSide))
        {
            return reduceOnly ? FuturesTradeAction.CloseShort : FuturesTradeAction.OpenShort;
        }

        if (IsLong(positionSide))
        {
            return reduceOnly ? FuturesTradeAction.CloseLong : FuturesTradeAction.OpenLong;
        }

        return (side, reduceOnly) switch
        {
            (TradeSide.Buy, false) => FuturesTradeAction.OpenLong,
            (TradeSide.Sell, false) => FuturesTradeAction.OpenShort,
            (TradeSide.Buy, true) => FuturesTradeAction.CloseShort,
            _ => FuturesTradeAction.CloseLong
        };
    }

    private static FuturesFillRecord MapFill(BybitExecutionSnapshot execution, bool reduceOnly)
    {
        var side = ParseSide(execution.Side);
        return new FuturesFillRecord
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
        };
    }

    private static FuturesPositionSnapshot MapPosition(
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

    public static void ApplyPositionToState(
        BotState state,
        FuturesPositionSnapshot position,
        bool updatePaperEquity = false)
    {
        var previousTotalRealizedPnl = state.TotalRealizedPnl;
        var realizedDelta = position.RealizedPnl - previousTotalRealizedPnl;
        state.PositionSide = position.Side;
        state.BaseAssetQuantity = position.Size;
        state.AverageEntryPrice = position.EntryPrice;
        state.EntryPrice = position.EntryPrice;
        state.LastObservedPrice = position.MarkPrice;
        state.MarkPrice = position.MarkPrice;
        state.LiquidationPrice = position.LiquidationPrice;
        state.UnrealizedPnl = position.UnrealizedPnl;
        state.TotalRealizedPnl = position.RealizedPnl;
        state.DailyRealizedPnl += realizedDelta;
        state.PositionIdx = position.PositionIdx;
        state.Leverage = position.Leverage;
        state.MarginMode = "Isolated";
        state.ReduceOnly = false;
        state.IsInitialized = true;
        if (updatePaperEquity)
        {
            state.QuoteAssetBalance += realizedDelta;
            var currentEquity = state.QuoteAssetBalance + state.UnrealizedPnl;
            state.PeakEquityUsdt = state.PeakEquityUsdt <= 0m
                ? currentEquity
                : decimal.Max(state.PeakEquityUsdt, currentEquity);
            state.CurrentDrawdownUsdt = decimal.Max(0m, state.PeakEquityUsdt - currentEquity);
            state.CurrentDrawdownPercent = state.PeakEquityUsdt > 0m
                ? state.CurrentDrawdownUsdt / state.PeakEquityUsdt * 100m
                : 0m;
        }

        state.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void ValidateMvpPosition(FuturesPositionSnapshot position)
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

    private static TradeSide ParseSide(string side) =>
        Enum.TryParse<TradeSide>(side, true, out var parsed) ? parsed : TradeSide.Buy;

    private static bool IsLong(string? side) =>
        string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Long", StringComparison.OrdinalIgnoreCase);

    private static bool IsShort(string? side) =>
        string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Short", StringComparison.OrdinalIgnoreCase);

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

}

public sealed class FuturesReconciliationResult
{
    public FuturesPositionSnapshot Position { get; init; } = new();

    public int RemoteOpenOrderCount { get; init; }

    public int RemoteHistoryOrderCount { get; init; }

    public int SyncedOrderCount { get; init; }

    public int SyncedFillCount { get; init; }

    public int FixedHangingOrderCount { get; init; }
}
