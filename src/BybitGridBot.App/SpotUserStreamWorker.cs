using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public sealed class SpotUserStreamWorker : BackgroundService
{
    private readonly AppOptions _appOptions;
    private readonly GridOptions _defaultGridOptions;
    private readonly IBybitUserStreamClient _userStreamClient;
    private readonly IGridRepository _repository;
    private readonly SpotExecutionSyncService _spotExecutionSyncService;
    private readonly ILogger<SpotUserStreamWorker> _logger;

    public SpotUserStreamWorker(
        IOptions<AppOptions> appOptions,
        IOptions<GridOptions> gridOptions,
        IBybitUserStreamClient userStreamClient,
        IGridRepository repository,
        SpotExecutionSyncService spotExecutionSyncService,
        ILogger<SpotUserStreamWorker> logger)
    {
        _appOptions = appOptions.Value;
        _defaultGridOptions = gridOptions.Value;
        _userStreamClient = userStreamClient;
        _repository = repository;
        _spotExecutionSyncService = spotExecutionSyncService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_appOptions.SpotUserStreamEnabled)
        {
            _logger.LogInformation("Spot user stream disabled.");
            return;
        }

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            _logger.LogInformation("Spot user stream skipped in paper mode.");
            return;
        }

        if (!string.Equals(_defaultGridOptions.Category, "spot", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Spot user stream skipped because CATEGORY is {Category}.", _defaultGridOptions.Category);
            return;
        }

        _logger.LogInformation("Starting spot user stream. Mode: {TradingMode}", _appOptions.TradingMode);
        await _userStreamClient.RunAsync(HandleMessageAsync, stoppingToken);
    }

    private async Task HandleMessageAsync(BybitUserStreamMessage message, CancellationToken cancellationToken)
    {
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
        }
    }

    private async Task HandleOrderAsync(BybitOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!OrderLinkIdFactory.IsManaged(snapshot.OrderLinkId))
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

        _logger.LogInformation(
            "Spot order stream synced. Symbol: {Symbol}, OrderLinkId: {OrderLinkId}, Status: {Status}, Filled: {FilledQuantity}",
            order.Symbol,
            order.OrderLinkId,
            order.Status,
            order.FilledQuantity);
    }

    private async Task HandleExecutionAsync(BybitExecutionSnapshot execution, CancellationToken cancellationToken)
    {
        if (!OrderLinkIdFactory.IsManaged(execution.OrderLinkId))
        {
            return;
        }

        var settings = await ResolveSettingsAsync(execution.Symbol, cancellationToken);
        if (settings is null)
        {
            return;
        }

        var state = await EnsureStateAsync(settings, cancellationToken);
        await _spotExecutionSyncService.ApplyExecutionAsync(state, settings.Category, execution, cancellationToken);
    }

    private async Task<GridBotSettings?> ResolveSettingsAsync(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        var settings = await _repository.GetRuntimeSettingsAsync(symbol, cancellationToken);
        return settings is not null &&
            string.Equals(settings.Category, "spot", StringComparison.OrdinalIgnoreCase)
            ? settings
            : null;
    }

    private async Task<BotState> EnsureStateAsync(GridBotSettings settings, CancellationToken cancellationToken)
    {
        var state = await _repository.GetBotStateAsync(settings.Symbol, cancellationToken);
        if (state is not null)
        {
            ResetDailyPnlIfNeeded(state);
            return state;
        }

        state = new BotState
        {
            Symbol = settings.Symbol,
            TradingMode = _appOptions.TradingMode,
            AggressiveModeEnabled = _defaultGridOptions.AggressiveModeEnabled,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repository.SaveBotStateAsync(state, cancellationToken);
        return state;
    }

    private GridOrder MapOrder(GridBotSettings settings, BybitOrderSnapshot snapshot, GridOrder? existing)
    {
        var status = MapStatus(snapshot.OrderStatus);
        var updatedAt = snapshot.UpdatedAt == default ? DateTimeOffset.UtcNow : snapshot.UpdatedAt;
        return new GridOrder
        {
            OrderLinkId = snapshot.OrderLinkId,
            BybitOrderId = snapshot.OrderId,
            Symbol = snapshot.Symbol,
            Category = settings.Category,
            Side = ParseSide(snapshot.Side),
            Price = snapshot.Price == 0m ? existing?.Price ?? 0m : snapshot.Price,
            Quantity = snapshot.Quantity == 0m ? existing?.Quantity ?? 0m : snapshot.Quantity,
            FilledQuantity = decimal.Max(existing?.FilledQuantity ?? 0m, snapshot.CumExecQty),
            AverageFillPrice = snapshot.AveragePrice == 0m ? existing?.AverageFillPrice ?? 0m : snapshot.AveragePrice,
            FeePaid = decimal.Max(existing?.FeePaid ?? 0m, snapshot.FeePaid),
            Status = status,
            TradingMode = _appOptions.TradingMode,
            ParentOrderLinkId = existing?.ParentOrderLinkId,
            StrategySource = existing?.StrategySource ?? settings.StrategyType.ToString(),
            RealizedPnl = existing?.RealizedPnl ?? 0m,
            CreatedAt = snapshot.CreatedAt == default ? existing?.CreatedAt ?? updatedAt : snapshot.CreatedAt,
            UpdatedAt = updatedAt,
            FilledAt = status == OrderStatus.Filled ? updatedAt : existing?.FilledAt
        };
    }

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
