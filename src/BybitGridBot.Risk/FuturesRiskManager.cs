using BybitGridBot.Domain;

namespace BybitGridBot.Risk;

public sealed class FuturesRiskManager
{
    public RiskDecision Evaluate(FuturesRiskEvaluationContext context)
    {
        if (context.Intent.IsPositionIncreasing &&
            context.DailyRealizedPnl <= -context.MaxDailyLossUsdt)
        {
            return Block("MAX_DAILY_LOSS_USDT limit reached. Increasing futures position is blocked.", RiskSeverity.Critical, RiskSuggestedAction.PauseBot);
        }

        if (context.Intent.Leverage <= 0m)
        {
            return Block("Futures leverage must be positive.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        if (context.Intent.Leverage > context.RiskOptions.MaxLeverage)
        {
            return Block("FUTURES_MAX_LEVERAGE limit reached.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        if (context.Intent.IsPositionIncreasing && context.RiskOptions.StopLossRequired && context.Intent.StopLossPrice is null)
        {
            return Block("STOP_LOSS_REQUIRED is enabled for futures.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        if (context.Intent.ExpectedFundingCostUsdt > context.RiskOptions.MaxFundingCostUsdt)
        {
            return Block("FUTURES_MAX_FUNDING_COST_USDT limit reached.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
        }

        if (context.Intent.IsPositionIncreasing)
        {
            var projectedNotional = context.Position.PositionValueUsdt + context.Intent.NotionalUsdt;
            if (projectedNotional > context.RiskOptions.MaxNotionalUsdt)
            {
                return Block("FUTURES_MAX_NOTIONAL_USDT limit reached.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
            }

            var projectedMargin = context.Position.MarginUsedUsdt + context.Intent.InitialMarginUsdt;
            if (projectedMargin > context.RiskOptions.MaxMarginUsdt)
            {
                return Block("FUTURES_MAX_MARGIN_USDT limit reached.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
            }

            if (projectedMargin > context.AvailableMarginUsdt)
            {
                return Block("Insufficient available futures margin.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
            }

            var liquidationBuffer = ResolveLiquidationBufferPercent(context);
            if (liquidationBuffer < context.RiskOptions.MinLiquidationBufferPercent)
            {
                return Block("FUTURES_MIN_LIQUIDATION_BUFFER_PERCENT would be violated.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
            }
        }

        if (context.Intent.IsReduceOnly)
        {
            if (context.Position.Size <= 0m)
            {
                return Block("Reduce-only futures order requires an open position.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
            }

            if (context.Intent.Quantity > context.Position.Size)
            {
                return Block("Reduce-only futures quantity exceeds current position size.", RiskSeverity.Warning, RiskSuggestedAction.BlockNewOrders);
            }
        }

        return new RiskDecision
        {
            IsAllowed = true,
            Reason = "Futures risk checks passed.",
            Severity = RiskSeverity.Info,
            SuggestedAction = RiskSuggestedAction.Allow
        };
    }

    private static decimal ResolveLiquidationBufferPercent(FuturesRiskEvaluationContext context)
    {
        var liquidationPrice = context.Intent.LiquidationPrice ?? context.Position.LiquidationPrice;
        var referencePrice = context.MarkPrice > 0m ? context.MarkPrice : context.Intent.Price;
        if (liquidationPrice <= 0m || referencePrice <= 0m)
        {
            return 100m;
        }

        return Math.Abs(referencePrice - liquidationPrice) / referencePrice * 100m;
    }

    private static RiskDecision Block(string reason, RiskSeverity severity, RiskSuggestedAction action) => new()
    {
        IsAllowed = false,
        Reason = reason,
        Severity = severity,
        SuggestedAction = action
    };
}

public sealed class FuturesRiskEvaluationContext
{
    public FuturesRiskOptions RiskOptions { get; init; } = new();

    public FuturesTradeIntent Intent { get; init; } = new();

    public FuturesPositionSnapshot Position { get; init; } = new();

    public decimal MarkPrice { get; init; }

    public decimal AvailableMarginUsdt { get; init; }

    public decimal DailyRealizedPnl { get; init; }

    public decimal MaxDailyLossUsdt { get; init; } = 20m;
}
