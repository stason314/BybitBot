using BybitGridBot.App;
using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Risk;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BybitGridBot.Tests;

public sealed class FuturesIntegrationSafetyTests
{
    [Fact]
    public void FuturesCloseRequest_IsAlwaysReduceOnly()
    {
        var service = CreateExecutionService(TradingMode.Paper);

        var request = service.CreateBybitRequest(Settings(), Intent(FuturesTradeAction.CloseLong, stopLossPrice: null));

        Assert.Equal("Sell", request.Side);
        Assert.True(request.ReduceOnly.GetValueOrDefault());
        Assert.Equal(0, request.PositionIdx);
    }

    [Fact]
    public void FuturesOpenShortRequest_IsPaperOnlyAndSellSide()
    {
        var service = CreateExecutionService(TradingMode.Paper);

        var request = service.CreateBybitRequest(
            Settings(direction: FuturesDirection.ShortOnly),
            Intent(FuturesTradeAction.OpenShort, stopLossPrice: 110m));

        Assert.Equal("Sell", request.Side);
        Assert.False(request.ReduceOnly.GetValueOrDefault());
    }

    [Fact]
    public void FuturesOpenShortRequest_IsBlockedOutsidePaper()
    {
        var service = CreateExecutionService(TradingMode.Testnet);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.CreateBybitRequest(
                Settings(direction: FuturesDirection.ShortOnly),
                Intent(FuturesTradeAction.OpenShort, stopLossPrice: 110m)));

        Assert.Contains("paper mode", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DailyLossBlocksOpenLong_ButAllowsCloseLong()
    {
        var manager = new FuturesRiskManager();
        var open = manager.Evaluate(RiskContext(Intent(FuturesTradeAction.OpenLong), dailyRealizedPnl: -25m));
        var close = manager.Evaluate(RiskContext(
            Intent(FuturesTradeAction.CloseLong, stopLossPrice: null),
            positionSize: 0.01m,
            dailyRealizedPnl: -25m));

        Assert.False(open.IsAllowed);
        Assert.True(close.IsAllowed);
    }

    [Fact]
    public void PaperSimulator_LiquidatesLongPosition()
    {
        var simulator = new FuturesPaperSimulator(new FuturesAccounting());

        var result = simulator.Simulate(new FuturesPaperSimulationRequest
        {
            Position = new FuturesPositionSnapshot
            {
                Symbol = "BTCUSDT",
                Category = "linear",
                Side = "Buy",
                Size = 0.01m,
                EntryPrice = 100m,
                MarkPrice = 89m,
                LiquidationPrice = 90m,
                Leverage = 2m,
                PositionIdx = 0
            },
            MarkPrice = 89m
        });

        Assert.True(result.IsLiquidated);
        Assert.Equal(0m, result.Position.Size);
    }

    [Fact]
    public async Task Preflight_BlocksInvalidCategoryModeAndLeverage()
    {
        var service = await CreatePreflightServiceAsync(new FuturesOptions
        {
            Enabled = true,
            MvpMaxLeverage = 2m
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureTestnetReadyAsync(Settings(category: "spot"), Instrument(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureTestnetReadyAsync(Settings(positionMode: FuturesPositionMode.Hedge), Instrument(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnsureTestnetReadyAsync(Settings(leverage: 3m), Instrument(), CancellationToken.None));
    }

    [Fact]
    public async Task TestnetFirstOrdersMustUseMinimumSize()
    {
        var service = CreateExecutionService(
            TradingMode.Testnet,
            new FuturesOptions
            {
                TestnetEnabled = true,
                MvpMaxLeverage = 2m,
                MinSizeOrderCount = 3
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(new FuturesExecutionRequest
            {
                Settings = Settings(),
                Intent = Intent(FuturesTradeAction.OpenLong, quantity: 0.01m),
                Position = new FuturesPositionSnapshot { Symbol = "BTCUSDT", Category = "linear" },
                MarkPrice = 100m,
                Instrument = Instrument(minOrderQty: 0.001m, minOrderAmount: 0.1m)
            }, CancellationToken.None));

        Assert.Contains("minimum size", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconciliation_SyncsManagedExecutionsIdempotently()
    {
        var repository = await CreateRepositoryAsync();
        var orderLinkId = FuturesOrderLinkIds.Create(FuturesTradeAction.OpenLong);
        var bybitClient = new FakeBybitRestClient
        {
            OrderHistory =
            [
                new BybitOrderSnapshot
                {
                    OrderId = "order-1",
                    OrderLinkId = orderLinkId,
                    Symbol = "BTCUSDT",
                    Side = "Buy",
                    OrderStatus = "Filled",
                    Quantity = 0.001m,
                    CumExecQty = 0.001m,
                    AveragePrice = 100m,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ],
            Executions =
            [
                new BybitExecutionSnapshot
                {
                    ExecId = "exec-1",
                    OrderId = "order-1",
                    OrderLinkId = orderLinkId,
                    Symbol = "BTCUSDT",
                    Side = "Buy",
                    ExecType = "Trade",
                    ExecPrice = 100m,
                    ExecQty = 0.001m,
                    ExecFee = 0.00006m,
                    ExecTime = DateTimeOffset.UtcNow
                }
            ]
        };
        var service = new FuturesReconciliationService(
            Options.Create(new AppOptions { TradingMode = TradingMode.Testnet }),
            bybitClient,
            new FuturesProtectionService(
                Options.Create(new AppOptions { TradingMode = TradingMode.Testnet }),
                bybitClient,
                repository,
                NullLogger<FuturesProtectionService>.Instance),
            repository,
            NullLogger<FuturesReconciliationService>.Instance);
        var state = new BotState { Symbol = FuturesStateKeys.ForSymbol("BTCUSDT"), TradingMode = TradingMode.Testnet };

        var first = await service.ReconcileAsync(Settings(), state, 100m, CancellationToken.None);
        var second = await service.ReconcileAsync(Settings(), state, 100m, CancellationToken.None);

        Assert.Equal(1, first.SyncedFillCount);
        Assert.Equal(0, second.SyncedFillCount);
        Assert.True(await repository.FuturesFillExistsAsync("exec-1", CancellationToken.None));
    }


    private static FuturesRiskEvaluationContext RiskContext(
        FuturesTradeIntent intent,
        decimal positionSize = 0m,
        decimal dailyRealizedPnl = 0m) => new()
    {
        RiskOptions = new FuturesRiskOptions
        {
            MaxLeverage = 2m,
            MaxNotionalUsdt = 100m,
            MaxMarginUsdt = 50m,
            MinLiquidationBufferPercent = 10m,
            StopLossRequired = true
        },
        Intent = intent,
        Position = new FuturesPositionSnapshot
        {
            Symbol = "BTCUSDT",
            Category = "linear",
            Side = positionSize > 0m ? "Buy" : "None",
            Size = positionSize,
            PositionValueUsdt = positionSize * 100m,
            MarginUsedUsdt = positionSize * 50m,
            LiquidationPrice = 50m
        },
        MarkPrice = 100m,
        AvailableMarginUsdt = 100m,
        DailyRealizedPnl = dailyRealizedPnl
    };

    private static FuturesExecutionService CreateExecutionService(
        TradingMode tradingMode,
        FuturesOptions? futuresOptions = null)
    {
        var repository = CreateRepositoryAsync().GetAwaiter().GetResult();
        return new FuturesExecutionService(
            Options.Create(new AppOptions { TradingMode = tradingMode }),
            Options.Create(futuresOptions ?? new FuturesOptions { MvpMaxLeverage = 2m }),
            Options.Create(new FuturesMainnetChecklistOptions()),
            new FakeBybitRestClient(),
            new FuturesPaperSimulator(new FuturesAccounting()),
            repository);
    }

    private static async Task<FuturesPreflightService> CreatePreflightServiceAsync(FuturesOptions futuresOptions)
    {
        var repository = await CreateRepositoryAsync();
        return new FuturesPreflightService(
            Options.Create(futuresOptions),
            new FakeBybitRestClient(),
            repository,
            NullLogger<FuturesPreflightService>.Instance);
    }

    private static async Task<IGridRepository> CreateRepositoryAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bybit-futures-tests-{Guid.NewGuid():N}.db");
        var repository = new SqliteGridRepository(path, NullLogger<SqliteGridRepository>.Instance);
        await repository.InitializeAsync(CancellationToken.None);
        return repository;
    }

    private static FuturesBotSettings Settings(
        string category = "linear",
        FuturesPositionMode positionMode = FuturesPositionMode.OneWay,
        decimal leverage = 2m,
        FuturesDirection direction = FuturesDirection.LongOnly) => new()
    {
        Symbol = "BTCUSDT",
        Category = category,
        StrategyType = FuturesStrategyType.TrendFollow,
        Leverage = leverage,
        MarginMode = FuturesMarginMode.Isolated,
        PositionMode = positionMode,
        Direction = direction,
        MaxNotionalUsdt = 100m,
        MaxMarginUsdt = 50m,
        StopLossPercent = 1m,
        TakeProfitPercent = 2m,
        LiquidationBufferPercent = 15m
    };

    private static FuturesTradeIntent Intent(
        FuturesTradeAction action,
        decimal quantity = 0.001m,
        decimal? stopLossPrice = 90m) => new()
    {
        Symbol = "BTCUSDT",
        Category = "linear",
        Action = action,
        Price = 100m,
        Quantity = quantity,
        Leverage = 2m,
        StopLossPrice = stopLossPrice,
        TakeProfitPrice = action == FuturesTradeAction.OpenLong ? 110m : null,
        LiquidationPrice = 50m,
        PositionIdx = 0,
        OrderLinkId = $"test-{Guid.NewGuid():N}"[..20]
    };

    private static FuturesInstrumentRules Instrument(
        decimal minOrderQty = 0.001m,
        decimal minOrderAmount = 0.1m) => new()
    {
        TickSize = 0.1m,
        QtyStep = 0.001m,
        BasePrecision = 0.001m,
        MinOrderQty = minOrderQty,
        MinOrderAmount = minOrderAmount
    };

    private sealed class FakeBybitRestClient : IBybitRestClient
    {
        public IReadOnlyList<BybitOrderSnapshot> OpenOrders { get; init; } = [];

        public IReadOnlyList<BybitOrderSnapshot> OrderHistory { get; init; } = [];

        public IReadOnlyList<BybitExecutionSnapshot> Executions { get; init; } = [];

        public Task<BybitTicker> GetTickerAsync(string category, string symbol, CancellationToken cancellationToken) =>
            Task.FromResult(new BybitTicker(symbol, 100m, 99m, 101m));

        public Task<BybitWalletBalance> GetWalletBalanceAsync(CancellationToken cancellationToken, params string[] coins) =>
            Task.FromResult(new BybitWalletBalance());

        public Task<BybitFeeRate> GetFeeRateAsync(string category, string symbol, CancellationToken cancellationToken) =>
            Task.FromResult(new BybitFeeRate(symbol, 0.01m, 0.06m));

        public Task<BybitOrderAck> CreateOrderAsync(BybitCreateOrderRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new BybitOrderAck("fake-order", request.OrderLinkId));

        public Task<BybitOrderAck> CancelOrderAsync(string category, string symbol, string? orderId, string? orderLinkId, CancellationToken cancellationToken) =>
            Task.FromResult(new BybitOrderAck(orderId ?? "fake-order", orderLinkId ?? "fake-link"));

        public Task<IReadOnlyList<BybitOrderSnapshot>> GetOpenOrdersAsync(string category, string symbol, CancellationToken cancellationToken) =>
            Task.FromResult(OpenOrders);

        public Task<IReadOnlyList<BybitOrderSnapshot>> GetOrderHistoryAsync(string category, string symbol, string? orderLinkId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BybitOrderSnapshot>>(
                string.IsNullOrWhiteSpace(orderLinkId)
                    ? OrderHistory
                    : OrderHistory.Where(order => string.Equals(order.OrderLinkId, orderLinkId, StringComparison.OrdinalIgnoreCase)).ToArray());

        public Task<IReadOnlyList<BybitExecutionSnapshot>> GetExecutionsAsync(string category, string symbol, string? orderLinkId, string? execType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BybitExecutionSnapshot>>(
                Executions
                    .Where(execution => string.IsNullOrWhiteSpace(orderLinkId) || string.Equals(execution.OrderLinkId, orderLinkId, StringComparison.OrdinalIgnoreCase))
                    .Where(execution => string.IsNullOrWhiteSpace(execType) || string.Equals(execution.ExecType, execType, StringComparison.OrdinalIgnoreCase))
                    .ToArray());

        public Task<IReadOnlyList<Candle>> GetKlinesAsync(string category, string symbol, string interval, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Candle>>(Array.Empty<Candle>());

        public Task<BybitInstrumentInfo> GetInstrumentInfoAsync(string category, string symbol, CancellationToken cancellationToken) =>
            Task.FromResult(new BybitInstrumentInfo
            {
                Symbol = symbol,
                TickSize = 0.1m,
                QtyStep = 0.001m,
                BasePrecision = 0.001m,
                MinOrderQty = 0.001m,
                MinOrderAmount = 0.1m
            });

        public Task<BybitPositionSnapshot?> GetPositionAsync(string category, string symbol, CancellationToken cancellationToken) =>
            Task.FromResult<BybitPositionSnapshot?>(new BybitPositionSnapshot
            {
                Symbol = symbol,
                Side = "None",
                PositionIdx = 0,
                TradeMode = 1,
                Leverage = 2m
            });

        public Task SetLeverageAsync(BybitSetLeverageRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SwitchIsolatedMarginAsync(BybitSwitchIsolatedMarginRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SwitchPositionModeAsync(BybitSwitchPositionModeRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetTradingStopAsync(BybitSetTradingStopRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
