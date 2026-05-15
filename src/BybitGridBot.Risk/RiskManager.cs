using BybitGridBot.Domain;
using BybitGridBot.Strategy;

namespace BybitGridBot.Risk;

public sealed class RiskManager
{
    public IReadOnlyList<string> ValidateOrderPlacement(
        RiskOptions riskOptions,
        GridOptions gridOptions,
        BotState state,
        IReadOnlyCollection<GridOrder> activeOrders,
        decimal currentPrice,
        decimal orderSizeUsdt,
        decimal minOrderSizeUsdt,
        decimal availableUsdt)
    {
        var violations = new List<string>();

        if (activeOrders.Count >= riskOptions.MaxOpenOrders)
        {
            violations.Add("MAX_OPEN_ORDERS limit reached.");
        }

        if (state.DailyRealizedPnl <= -riskOptions.MaxDailyLossUsdt)
        {
            violations.Add("MAX_DAILY_LOSS_USDT limit reached.");
        }

        if (currentPrice < gridOptions.LowerPrice || currentPrice > gridOptions.UpperPrice)
        {
            violations.Add("Current price is outside the configured trading range.");
        }

        if (orderSizeUsdt < decimal.Max(riskOptions.MinOrderSizeUsdt, minOrderSizeUsdt))
        {
            violations.Add("Order size is below the minimum allowed notional.");
        }

        if (availableUsdt < orderSizeUsdt)
        {
            violations.Add("Insufficient USDT balance.");
        }

        var positionValue = (state.BaseAssetQuantity * currentPrice) + orderSizeUsdt;
        if (positionValue > riskOptions.MaxPositionUsdt)
        {
            violations.Add("MAX_POSITION_USDT limit reached.");
        }

        return violations;
    }

    public RiskDecision EvaluateTradeIntent(RiskEvaluationContext context)
    {
        if (context.State.DailyRealizedPnl <= -context.RiskOptions.MaxDailyLossUsdt)
        {
            return Block("MAX_DAILY_LOSS_USDT limit reached.", RiskSeverity.Critical, RiskSuggestedAction.PauseBot);
        }

        if (context.ActiveOrders.Count >= context.RiskOptions.MaxOpenOrders)
        {
            return Block("MAX_OPEN_ORDERS limit reached.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        if (context.MarketRegime == MarketRegime.HighVolatility &&
            !context.RiskOptions.AllowHighVolatilityTrading)
        {
            return Block("High volatility trading is disabled.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        if (!string.IsNullOrWhiteSpace(context.Intent.OrderLinkId) &&
            context.ActiveOrders.Any(order => string.Equals(order.OrderLinkId, context.Intent.OrderLinkId, StringComparison.OrdinalIgnoreCase)))
        {
            return Block("Duplicate orderLinkId.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        if (context.Intent.NotionalUsdt < context.RiskOptions.MinOrderSizeUsdt)
        {
            return Block("Order notional is below MIN_ORDER_SIZE_USDT.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        var newPositionValue = context.Position.PositionValueUsdt +
                               (context.Intent.Side == TradeSide.Buy ? context.Intent.NotionalUsdt : 0m);
        if (newPositionValue > context.RiskOptions.MaxPositionUsdt)
        {
            return Block("MAX_POSITION_USDT limit reached.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        var maxExposure = context.TotalEquityUsdt * context.RiskOptions.MaxTotalExposurePercent / 100m;
        if (context.TotalExposureUsdt + context.Intent.NotionalUsdt > maxExposure)
        {
            return Block("MAX_TOTAL_EXPOSURE_PERCENT limit reached.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        var reserve = context.TotalEquityUsdt * context.RiskOptions.MinUsdtReservePercent / 100m;
        if (context.Intent.Side == TradeSide.Buy &&
            context.AvailableUsdt - context.Intent.NotionalUsdt < reserve)
        {
            return Block("MIN_USDT_RESERVE_PERCENT would be violated.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        return new RiskDecision
        {
            IsAllowed = true,
            Reason = "Risk checks passed.",
            Severity = RiskSeverity.Info,
            SuggestedAction = RiskSuggestedAction.Allow
        };
    }

    private static RiskDecision Block(string reason, RiskSeverity severity, RiskSuggestedAction action) => new()
    {
        IsAllowed = false,
        Reason = reason,
        Severity = severity,
        SuggestedAction = action
    };
}

public sealed class RiskEvaluationContext
{
    public RiskOptions RiskOptions { get; init; } = new();

    public BotState State { get; init; } = new();

    public IReadOnlyCollection<GridOrder> ActiveOrders { get; init; } = [];

    public TradeIntent Intent { get; init; } = new();

    public PositionSnapshot Position { get; init; } = new();

    public MarketRegime MarketRegime { get; init; } = MarketRegime.Unknown;

    public decimal TotalEquityUsdt { get; init; }

    public decimal AvailableUsdt { get; init; }

    public decimal TotalExposureUsdt { get; init; }
}
