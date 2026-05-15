using BybitGridBot.Domain;
using BybitGridBot.Risk;

namespace BybitGridBot.Tests;

public sealed class FuturesRiskManagerTests
{
    [Fact]
    public void Evaluate_BlocksIncreasingPositionAfterDailyLossLimit()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(dailyRealizedPnl: -25m));

        Assert.False(decision.IsAllowed);
        Assert.Contains("MAX_DAILY_LOSS_USDT", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BlocksDailyLossByEquityPercent()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            riskOptions: Options(maxDailyLossUsdt: 0m, maxDailyLossEquityPercent: 1m),
            accountEquityUsdt: 1000m,
            dailyRealizedPnl: -11m));

        Assert.False(decision.IsAllowed);
        Assert.Contains("MAX_DAILY_LOSS_USDT", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BlocksMaxDrawdownByEquityPercent()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            riskOptions: Options(maxDrawdownEquityPercent: 5m),
            accountEquityUsdt: 1000m,
            currentDrawdownPercent: 5.1m));

        Assert.False(decision.IsAllowed);
        Assert.Contains("FUTURES_MAX_DRAWDOWN_EQUITY_PERCENT", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BlocksEmergencyPauseForIncreasingPosition()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            riskOptions: Options(emergencyPause: true)));

        Assert.False(decision.IsAllowed);
        Assert.Equal(RiskSuggestedAction.PauseBot, decision.SuggestedAction);
    }

    [Fact]
    public void Evaluate_BlocksNewPositionWhenMaxOpenPositionsReached()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            riskOptions: Options(maxOpenPositions: 1),
            openPositionCount: 1));

        Assert.False(decision.IsAllowed);
        Assert.Contains("FUTURES_MAX_OPEN_POSITIONS", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BlocksNotionalAboveLimit()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            riskOptions: Options(maxNotionalUsdt: 400m),
            intent: Intent(price: 50000m, quantity: 0.01m)));

        Assert.False(decision.IsAllowed);
        Assert.Contains("FUTURES_MAX_NOTIONAL_USDT", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BlocksMarginAboveLimit()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            riskOptions: Options(maxMarginUsdt: 100m),
            intent: Intent(price: 50000m, quantity: 0.01m, leverage: 2m)));

        Assert.False(decision.IsAllowed);
        Assert.Contains("FUTURES_MAX_MARGIN_USDT", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BlocksLeverageAboveLimit()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            riskOptions: Options(maxLeverage: 2m),
            intent: Intent(leverage: 3m)));

        Assert.False(decision.IsAllowed);
        Assert.Contains("FUTURES_MAX_LEVERAGE", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BlocksMissingStopLossWhenRequired()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            intent: Intent(stopLossPrice: null)));

        Assert.False(decision.IsAllowed);
        Assert.Contains("STOP_LOSS_REQUIRED", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BlocksLiquidationBufferTooSmall()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            riskOptions: Options(minLiquidationBufferPercent: 10m),
            intent: Intent(liquidationPrice: 49000m)));

        Assert.False(decision.IsAllowed);
        Assert.Contains("FUTURES_MIN_LIQUIDATION_BUFFER_PERCENT", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_BlocksFundingCostAboveLimit()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            riskOptions: Options(maxFundingCostUsdt: 0.5m),
            intent: Intent(expectedFundingCostUsdt: 0.6m)));

        Assert.False(decision.IsAllowed);
        Assert.Contains("FUTURES_MAX_FUNDING_COST_USDT", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AllowsReduceOnlyCloseDuringDailyLossLimit()
    {
        var decision = new FuturesRiskManager().Evaluate(Context(
            dailyRealizedPnl: -25m,
            position: new FuturesPositionSnapshot
            {
                Symbol = "BTCUSDT",
                Category = "linear",
                Side = "Buy",
                Size = 0.01m,
                EntryPrice = 50000m,
                MarkPrice = 50000m,
                PositionValueUsdt = 500m,
                MarginUsedUsdt = 250m,
                Leverage = 2m
            },
            intent: Intent(action: FuturesTradeAction.CloseLong, quantity: 0.01m, stopLossPrice: null)));

        Assert.True(decision.IsAllowed);
    }

    private static FuturesRiskEvaluationContext Context(
        FuturesRiskOptions? riskOptions = null,
        FuturesTradeIntent? intent = null,
        FuturesPositionSnapshot? position = null,
        decimal dailyRealizedPnl = 0m,
        decimal totalRealizedPnl = 0m,
        decimal accountEquityUsdt = 1000m,
        decimal currentDrawdownUsdt = 0m,
        decimal currentDrawdownPercent = 0m,
        int openPositionCount = 0) => new()
    {
        RiskOptions = riskOptions ?? Options(),
        Intent = intent ?? Intent(),
        Position = position ?? new FuturesPositionSnapshot
        {
            Symbol = "BTCUSDT",
            Category = "linear",
            Side = "None",
            MarkPrice = 50000m,
            Leverage = 2m
        },
        MarkPrice = 50000m,
        AvailableMarginUsdt = 1000m,
        DailyRealizedPnl = dailyRealizedPnl,
        TotalRealizedPnl = totalRealizedPnl,
        AccountEquityUsdt = accountEquityUsdt,
        CurrentDrawdownUsdt = currentDrawdownUsdt,
        CurrentDrawdownPercent = currentDrawdownPercent,
        OpenPositionCount = openPositionCount
    };

    private static FuturesTradeIntent Intent(
        FuturesTradeAction action = FuturesTradeAction.OpenLong,
        decimal price = 50000m,
        decimal quantity = 0.005m,
        decimal leverage = 2m,
        decimal? stopLossPrice = 49000m,
        decimal? liquidationPrice = 35000m,
        decimal expectedFundingCostUsdt = 0m) => new()
    {
        Symbol = "BTCUSDT",
        Category = "linear",
        Action = action,
        Price = price,
        Quantity = quantity,
        Leverage = leverage,
        StopLossPrice = stopLossPrice,
        LiquidationPrice = liquidationPrice,
        ExpectedFundingCostUsdt = expectedFundingCostUsdt
    };

    private static FuturesRiskOptions Options(
        decimal maxNotionalUsdt = 1000m,
        decimal maxMarginUsdt = 500m,
        decimal maxLeverage = 2m,
        decimal minLiquidationBufferPercent = 15m,
        decimal maxFundingCostUsdt = 1m,
        decimal maxDailyLossUsdt = 20m,
        decimal maxDailyLossEquityPercent = 0m,
        decimal maxDrawdownEquityPercent = 0m,
        int maxOpenPositions = 1,
        bool emergencyPause = false) => new()
    {
        MaxNotionalUsdt = maxNotionalUsdt,
        MaxMarginUsdt = maxMarginUsdt,
        MaxLeverage = maxLeverage,
        MinLiquidationBufferPercent = minLiquidationBufferPercent,
        MaxFundingCostUsdt = maxFundingCostUsdt,
        MaxDailyLossUsdt = maxDailyLossUsdt,
        MaxDailyLossEquityPercent = maxDailyLossEquityPercent,
        MaxDrawdownEquityPercent = maxDrawdownEquityPercent,
        MaxOpenPositions = maxOpenPositions,
        EmergencyPause = emergencyPause,
        StopLossRequired = true
    };
}
