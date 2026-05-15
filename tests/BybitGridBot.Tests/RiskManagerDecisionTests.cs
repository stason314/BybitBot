using BybitGridBot.Domain;
using BybitGridBot.Risk;

namespace BybitGridBot.Tests;

public sealed class RiskManagerDecisionTests
{
    [Fact]
    public void EvaluateTradeIntent_BlocksWhenDailyLossExceeded()
    {
        var decision = new RiskManager().EvaluateTradeIntent(Context(state: new BotState { DailyRealizedPnl = -25m }));

        Assert.False(decision.IsAllowed);
        Assert.Contains("MAX_DAILY_LOSS_USDT", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateTradeIntent_BlocksWhenOpenOrdersExceeded()
    {
        var order = new GridOrder { OrderLinkId = "existing", Symbol = "TONUSDT", Category = "spot", Side = TradeSide.Buy, Price = 2m, Quantity = 5m, Status = OrderStatus.New, TradingMode = TradingMode.Paper, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };

        var decision = new RiskManager().EvaluateTradeIntent(Context(activeOrders: [order], riskOptions: new RiskOptions { MaxOpenOrders = 1 }));

        Assert.False(decision.IsAllowed);
        Assert.Contains("MAX_OPEN_ORDERS", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateTradeIntent_BlocksHighVolatilityWhenDisabled()
    {
        var decision = new RiskManager().EvaluateTradeIntent(Context(marketRegime: MarketRegime.HighVolatility));

        Assert.False(decision.IsAllowed);
        Assert.Contains("High volatility", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateTradeIntent_BlocksDuplicateOrderLinkId()
    {
        var order = new GridOrder { OrderLinkId = "dup", Symbol = "TONUSDT", Category = "spot", Side = TradeSide.Buy, Price = 2m, Quantity = 5m, Status = OrderStatus.New, TradingMode = TradingMode.Paper, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };

        var decision = new RiskManager().EvaluateTradeIntent(Context(intent: Intent(orderLinkId: "dup"), activeOrders: [order]));

        Assert.False(decision.IsAllowed);
        Assert.Contains("Duplicate", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateTradeIntent_BlocksOrderBelowMinSize()
    {
        var decision = new RiskManager().EvaluateTradeIntent(Context(intent: Intent(price: 2m, quantity: 1m)));

        Assert.False(decision.IsAllowed);
        Assert.Contains("MIN_ORDER_SIZE_USDT", decision.Reason, StringComparison.Ordinal);
    }

    private static RiskEvaluationContext Context(
        RiskOptions? riskOptions = null,
        BotState? state = null,
        IReadOnlyCollection<GridOrder>? activeOrders = null,
        TradeIntent? intent = null,
        MarketRegime marketRegime = MarketRegime.RangeBound) => new()
    {
        RiskOptions = riskOptions ?? new RiskOptions { MaxDailyLossUsdt = 20m, MaxOpenOrders = 10, MinOrderSizeUsdt = 5m, MaxPositionUsdt = 300m, MaxTotalExposurePercent = 80m, MinUsdtReservePercent = 20m },
        State = state ?? new BotState(),
        ActiveOrders = activeOrders ?? [],
        Intent = intent ?? Intent(),
        Position = new PositionSnapshot { Symbol = "TONUSDT", CurrentPrice = 2m, QuoteAssetBalance = 1000m },
        MarketRegime = marketRegime,
        TotalEquityUsdt = 1000m,
        AvailableUsdt = 900m,
        TotalExposureUsdt = 100m
    };

    private static TradeIntent Intent(string orderLinkId = "intent-1", decimal price = 2m, decimal quantity = 5m) => new()
    {
        StrategyType = StrategyType.Grid,
        Symbol = "TONUSDT",
        Side = TradeSide.Buy,
        Price = price,
        Quantity = quantity,
        OrderLinkId = orderLinkId
    };
}
