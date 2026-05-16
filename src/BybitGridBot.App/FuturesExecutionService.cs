using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public sealed class FuturesExecutionService
{
    private readonly AppOptions _appOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly FuturesOptions _futuresOptions;
    private readonly FuturesMainnetChecklistOptions _mainnetChecklistOptions;
    private readonly FuturesPaperSimulator _paperSimulator;
    private readonly IGridRepository _repository;

    public FuturesExecutionService(
        IOptions<AppOptions> appOptions,
        IOptions<FuturesOptions> futuresOptions,
        IOptions<FuturesMainnetChecklistOptions> mainnetChecklistOptions,
        IBybitRestClient bybitRestClient,
        FuturesPaperSimulator paperSimulator,
        IGridRepository repository)
    {
        _appOptions = appOptions.Value;
        _futuresOptions = futuresOptions.Value;
        _mainnetChecklistOptions = mainnetChecklistOptions.Value;
        _bybitRestClient = bybitRestClient;
        _paperSimulator = paperSimulator;
        _repository = repository;
    }

    public async Task<FuturesExecutionResult> ExecuteAsync(
        FuturesExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateTradingMode();
        ValidateInstrumentRules(request.Intent, request.Instrument);
        await ValidateTestnetChecklistAsync(request, cancellationToken);
        var bybitRequest = CreateBybitRequest(request.Settings, request.Intent);
        var now = DateTimeOffset.UtcNow;
        var order = new GridOrder
        {
            OrderLinkId = request.Intent.OrderLinkId,
            Symbol = request.Settings.Symbol,
            Category = request.Settings.Category,
            Side = Enum.Parse<TradeSide>(bybitRequest.Side, true),
            Price = request.Intent.Price,
            Quantity = request.Intent.Quantity,
            Status = _appOptions.TradingMode == TradingMode.Paper ? OrderStatus.Filled : OrderStatus.New,
            TradingMode = _appOptions.TradingMode,
            PositionSide = ResolvePositionSide(request.Intent.Action),
            ReduceOnly = bybitRequest.ReduceOnly == true,
            PositionIdx = bybitRequest.PositionIdx ?? 0,
            Leverage = request.Intent.Leverage,
            MarginMode = request.Settings.MarginMode.ToString(),
            EntryPrice = request.Position.EntryPrice,
            MarkPrice = request.MarkPrice,
            LiquidationPrice = request.Intent.LiquidationPrice ?? request.Position.LiquidationPrice,
            CreatedAt = now,
            UpdatedAt = now,
            FilledAt = _appOptions.TradingMode == TradingMode.Paper ? now : null
        };

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            var simulation = _paperSimulator.Simulate(new FuturesPaperSimulationRequest
            {
                Position = request.Position,
                Intent = request.Intent,
                MarkPrice = request.MarkPrice,
                FeeRatePercent = request.FeeRatePercent,
                FundingCostUsdt = request.Intent.ExpectedFundingCostUsdt
            });

            order.FilledQuantity = request.Intent.Quantity;
            order.AverageFillPrice = request.Intent.Price;
            order.FeePaid = simulation.FeePaid;
            order.RealizedPnl = simulation.Position.RealizedPnl - request.Position.RealizedPnl;
            order.EntryPrice = simulation.Position.EntryPrice;
            order.MarkPrice = simulation.Position.MarkPrice;
            order.LiquidationPrice = simulation.Position.LiquidationPrice;
            order.UnrealizedPnl = simulation.Position.UnrealizedPnl;
            await _repository.UpsertOrderAsync(order, cancellationToken);
            await _repository.UpsertFuturesOrderAsync(ToFuturesOrder(request, order), cancellationToken);
            await _repository.UpsertFuturesPositionAsync(simulation.Position, _appOptions.TradingMode, cancellationToken);
            await _repository.AddFuturesFillAsync(new FuturesFillRecord
            {
                OrderLinkId = request.Intent.OrderLinkId,
                Symbol = request.Settings.Symbol,
                Action = request.Intent.Action,
                Side = order.Side,
                ExecType = "Paper",
                Quantity = request.Intent.Quantity,
                Price = request.Intent.Price,
                Fee = simulation.FeePaid,
                RealizedPnl = order.RealizedPnl,
                Funding = simulation.FundingPaid,
                CreatedAt = now
            }, cancellationToken);

            return new FuturesExecutionResult
            {
                Order = order,
                Position = simulation.Position,
                IsPaper = true,
                IsLiquidated = simulation.IsLiquidated,
                Message = simulation.Reason
            };
        }

        var ack = await _bybitRestClient.CreateOrderAsync(bybitRequest, cancellationToken);
        order.BybitOrderId = ack.OrderId;
        await _repository.UpsertOrderAsync(order, cancellationToken);
        await _repository.UpsertFuturesOrderAsync(ToFuturesOrder(request, order), cancellationToken);

        return new FuturesExecutionResult
        {
            Order = order,
            Position = request.Position,
            IsPaper = false,
            Message = _appOptions.TradingMode == TradingMode.Mainnet
                ? "Futures mainnet order submitted."
                : "Futures testnet order submitted."
        };
    }

    public BybitCreateOrderRequest CreateBybitRequest(FuturesBotSettings settings, FuturesTradeIntent intent)
    {
        ValidateMvpExecution(settings, intent, _futuresOptions.MvpMaxLeverage, _appOptions.TradingMode);
        var request = FuturesOrderRequestFactory.Create(intent);
        if (intent.IsReduceOnly && request.ReduceOnly != true)
        {
            throw new InvalidOperationException("Futures close intents must be sent with reduceOnly=true.");
        }

        if (intent.IsPositionIncreasing && request.ReduceOnly == true)
        {
            throw new InvalidOperationException("Futures open intents must not be reduce-only.");
        }

        if (request.PositionIdx != 0)
        {
            throw new InvalidOperationException("Futures MVP requires positionIdx=0 for one-way mode.");
        }

        if (intent.Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort &&
            string.IsNullOrWhiteSpace(request.StopLoss))
        {
            throw new InvalidOperationException("Futures open intents must attach stopLoss to order create.");
        }

        return request;
    }

    private static void ValidateMvpExecution(
        FuturesBotSettings settings,
        FuturesTradeIntent intent,
        decimal maxLeverage,
        TradingMode tradingMode)
    {
        if (!string.Equals(settings.Category, "linear", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(intent.Category, "linear", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Futures MVP supports only category=linear.");
        }

        if (settings.MarginMode != FuturesMarginMode.Isolated)
        {
            throw new InvalidOperationException("Futures MVP supports only isolated margin.");
        }

        if (settings.PositionMode != FuturesPositionMode.OneWay)
        {
            throw new InvalidOperationException("Futures MVP supports only one-way mode.");
        }

        if (settings.Direction != FuturesDirection.LongOnly && tradingMode != TradingMode.Paper)
        {
            throw new InvalidOperationException("Futures short direction is enabled only in paper mode.");
        }

        if (intent.PositionIdx != 0)
        {
            throw new InvalidOperationException("Futures MVP requires positionIdx=0.");
        }

        if (intent.Action is FuturesTradeAction.OpenShort or FuturesTradeAction.CloseShort &&
            tradingMode != TradingMode.Paper)
        {
            throw new InvalidOperationException("Futures short actions are enabled only in paper mode.");
        }

        if (settings.Leverage > maxLeverage || intent.Leverage > maxLeverage)
        {
            throw new InvalidOperationException("Futures leverage exceeds MVP cap.");
        }
    }

    private static void ValidateInstrumentRules(FuturesTradeIntent intent, FuturesInstrumentRules instrument)
    {
        if (instrument.TickSize <= 0m)
        {
            throw new InvalidOperationException("Futures instrument tick size is invalid.");
        }

        if (instrument.QtyStep <= 0m && instrument.BasePrecision <= 0m)
        {
            throw new InvalidOperationException("Futures instrument quantity step is invalid.");
        }

        if (intent.Quantity <= 0m)
        {
            throw new InvalidOperationException("Futures order quantity must be positive.");
        }

        if (intent.IsPositionIncreasing && instrument.MinOrderQty > 0m && intent.Quantity < instrument.MinOrderQty)
        {
            throw new InvalidOperationException("Futures order quantity is below instrument min qty.");
        }

        if (intent.IsPositionIncreasing && instrument.MinOrderAmount > 0m && intent.NotionalUsdt < instrument.MinOrderAmount)
        {
            throw new InvalidOperationException("Futures order notional is below instrument min notional.");
        }
    }

    private void ValidateTradingMode()
    {
        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            return;
        }

        if (_appOptions.TradingMode == TradingMode.Mainnet && !_futuresOptions.MainnetEnabled)
        {
            throw new InvalidOperationException("Futures mainnet is blocked. Set FUTURES_MAINNET_ENABLED=true only after the mainnet checklist is complete.");
        }

        if (_appOptions.TradingMode == TradingMode.Mainnet)
        {
            var missing = _mainnetChecklistOptions.MissingItems();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException($"Futures mainnet checklist is incomplete: {string.Join(", ", missing)}.");
            }
        }

        if (_appOptions.TradingMode == TradingMode.Testnet && !_futuresOptions.TestnetEnabled)
        {
            throw new InvalidOperationException("Futures execution is paper-only until FUTURES_TESTNET_ENABLED=true.");
        }
    }

    private async Task ValidateTestnetChecklistAsync(FuturesExecutionRequest request, CancellationToken cancellationToken)
    {
        if (_appOptions.TradingMode == TradingMode.Paper ||
            request.Intent.Action != FuturesTradeAction.OpenLong ||
            _futuresOptions.MinSizeOrderCount <= 0)
        {
            return;
        }

        var previousOpenLongOrders = (await _repository.GetFuturesOrdersAsync(request.Settings.Symbol, cancellationToken))
            .Count(order => order.Action == FuturesTradeAction.OpenLong);
        if (previousOpenLongOrders >= _futuresOptions.MinSizeOrderCount)
        {
            return;
        }

        var minQuantity = CalculateMinimumOrderQuantity(request.Intent.Price, request.Instrument);
        if (request.Intent.Quantity != minQuantity)
        {
            throw new InvalidOperationException("First futures testnet orders must use instrument minimum size.");
        }
    }

    private static decimal CalculateMinimumOrderQuantity(decimal price, FuturesInstrumentRules instrument)
    {
        var minQuantity = instrument.MinOrderQty;
        if (price > 0m && instrument.MinOrderAmount > 0m)
        {
            minQuantity = decimal.Max(minQuantity, instrument.MinOrderAmount / price);
        }

        var step = instrument.QtyStep > 0m ? instrument.QtyStep : instrument.BasePrecision;
        return step > 0m ? Math.Ceiling(minQuantity / step) * step : minQuantity;
    }

    private static string ResolvePositionSide(FuturesTradeAction action) =>
        action switch
        {
            FuturesTradeAction.OpenLong or FuturesTradeAction.CloseLong or FuturesTradeAction.ReduceOnlyClose => "Long",
            FuturesTradeAction.OpenShort or FuturesTradeAction.CloseShort => "Short",
            _ => "None"
        };

    private static FuturesOrderRecord ToFuturesOrder(FuturesExecutionRequest request, GridOrder order) => new()
    {
        OrderLinkId = order.OrderLinkId,
        BybitOrderId = order.BybitOrderId,
        Symbol = order.Symbol,
        Category = order.Category,
        Action = request.Intent.Action,
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
        Leverage = order.Leverage,
        MarginMode = order.MarginMode ?? request.Settings.MarginMode.ToString(),
        StopLossPrice = request.Intent.StopLossPrice ?? 0m,
        TakeProfitPrice = request.Intent.TakeProfitPrice ?? 0m,
        RealizedPnl = order.RealizedPnl,
        CreatedAt = order.CreatedAt,
        UpdatedAt = order.UpdatedAt,
        FilledAt = order.FilledAt
    };
}

public sealed class FuturesExecutionRequest
{
    public FuturesBotSettings Settings { get; init; } = new();

    public FuturesTradeIntent Intent { get; init; } = new();

    public FuturesPositionSnapshot Position { get; init; } = new();

    public decimal MarkPrice { get; init; }

    public FuturesInstrumentRules Instrument { get; init; } = new();

    public decimal FeeRatePercent { get; init; } = 0.06m;
}

public sealed class FuturesExecutionResult
{
    public GridOrder Order { get; init; } = new();

    public FuturesPositionSnapshot Position { get; init; } = new();

    public bool IsPaper { get; init; }

    public bool IsLiquidated { get; init; }

    public string Message { get; init; } = string.Empty;
}
