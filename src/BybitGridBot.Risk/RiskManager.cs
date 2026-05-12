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
}
