using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Storage;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public sealed class SpotExecutionSyncService
{
    private readonly AppOptions _appOptions;
    private readonly IGridRepository _repository;
    private readonly ILogger<SpotExecutionSyncService> _logger;

    public SpotExecutionSyncService(
        IOptions<AppOptions> appOptions,
        IGridRepository repository,
        ILogger<SpotExecutionSyncService> logger)
    {
        _appOptions = appOptions.Value;
        _repository = repository;
        _logger = logger;
    }

    public async Task<SpotExecutionApplyResult> ApplyExecutionAsync(
        BotState state,
        string category,
        BybitExecutionSnapshot execution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(execution.ExecId) ||
            string.IsNullOrWhiteSpace(execution.OrderLinkId) ||
            !OrderLinkIdFactory.IsManaged(execution.OrderLinkId))
        {
            return SpotExecutionApplyResult.Ignored;
        }

        if (await _repository.SpotExecutionExistsAsync(execution.ExecId, cancellationToken))
        {
            return SpotExecutionApplyResult.Duplicate;
        }

        var existing = await _repository.GetOrderByLinkIdAsync(execution.OrderLinkId, cancellationToken);
        var side = ParseSide(execution.Side);
        var executedAt = execution.ExecTime == DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : execution.ExecTime;
        var alreadyReflected = existing is not null &&
            existing.UpdatedAt >= executedAt &&
            existing.FilledQuantity >= execution.ExecQty;

        var order = existing ?? new GridOrder
        {
            OrderLinkId = execution.OrderLinkId,
            BybitOrderId = execution.OrderId,
            Symbol = execution.Symbol,
            Category = category,
            Side = side,
            Price = execution.ExecPrice,
            Quantity = execution.ExecQty,
            Status = OrderStatus.New,
            TradingMode = _appOptions.TradingMode,
            StrategySource = "Grid",
            CreatedAt = executedAt,
            UpdatedAt = executedAt
        };

        var pnlDelta = 0m;
        var becameFilled = false;
        if (!alreadyReflected)
        {
            pnlDelta = ApplyFillDelta(state, side, execution.ExecQty, execution.ExecPrice, execution.ExecFee);
            ApplyExecutionToOrder(order, execution, pnlDelta, executedAt);
            await _repository.SaveBotStateAsync(state, cancellationToken);
            await _repository.UpsertOrderAsync(order, cancellationToken);
            becameFilled = order.Status == OrderStatus.Filled;
        }

        var added = await _repository.AddSpotExecutionAsync(new SpotExecutionRecord
        {
            ExecId = execution.ExecId,
            OrderLinkId = execution.OrderLinkId,
            BybitOrderId = execution.OrderId,
            Symbol = execution.Symbol,
            Category = category,
            Side = side,
            ExecType = string.IsNullOrWhiteSpace(execution.ExecType) ? "Trade" : execution.ExecType,
            Quantity = execution.ExecQty,
            Price = execution.ExecPrice,
            Fee = execution.ExecFee,
            RealizedPnl = pnlDelta,
            IsApplied = !alreadyReflected,
            ExecutedAt = executedAt,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        if (!added)
        {
            return SpotExecutionApplyResult.Duplicate;
        }

        _logger.LogInformation(
            "Spot execution synced. Symbol: {Symbol}, OrderLinkId: {OrderLinkId}, ExecId: {ExecId}, Quantity: {Quantity}, Applied: {Applied}, PnL delta: {PnlDelta}",
            execution.Symbol,
            execution.OrderLinkId,
            execution.ExecId,
            execution.ExecQty,
            !alreadyReflected,
            pnlDelta);

        return new SpotExecutionApplyResult(true, !alreadyReflected, becameFilled, order, pnlDelta);
    }

    private void ApplyExecutionToOrder(
        GridOrder order,
        BybitExecutionSnapshot execution,
        decimal pnlDelta,
        DateTimeOffset executedAt)
    {
        var previousFilled = order.FilledQuantity;
        var newFilled = previousFilled + execution.ExecQty;
        var previousCost = previousFilled * order.AverageFillPrice;
        var newCost = previousCost + (execution.ExecQty * execution.ExecPrice);

        order.BybitOrderId = string.IsNullOrWhiteSpace(execution.OrderId) ? order.BybitOrderId : execution.OrderId;
        order.Price = order.Price <= 0m ? execution.ExecPrice : order.Price;
        order.Quantity = decimal.Max(order.Quantity, newFilled);
        order.FilledQuantity = newFilled;
        order.AverageFillPrice = newFilled > 0m ? newCost / newFilled : order.AverageFillPrice;
        order.FeePaid += execution.ExecFee;
        order.RealizedPnl += pnlDelta;
        order.Status = order.Quantity > 0m && newFilled >= order.Quantity ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
        order.UpdatedAt = executedAt;
        order.FilledAt = order.Status == OrderStatus.Filled ? executedAt : order.FilledAt;
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
            state.ProfitProtectionPeakPrice = decimal.Max(state.ProfitProtectionPeakPrice, fillPrice);

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
                state.ProfitProtectionPeakPrice = 0m;
                state.ProfitProtectionTrailingStopPrice = 0m;
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

    private static TradeSide ParseSide(string side) =>
        Enum.TryParse<TradeSide>(side, true, out var parsed) ? parsed : TradeSide.Buy;
}

public sealed record SpotExecutionApplyResult(
    bool IsKnown,
    bool IsApplied,
    bool BecameFilled,
    GridOrder? Order,
    decimal PnlDelta)
{
    public static SpotExecutionApplyResult Ignored { get; } = new(false, false, false, null, 0m);

    public static SpotExecutionApplyResult Duplicate { get; } = new(true, false, false, null, 0m);
}
