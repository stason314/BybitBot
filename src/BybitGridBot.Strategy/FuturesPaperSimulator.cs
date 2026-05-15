using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class FuturesPaperSimulator
{
    private readonly FuturesAccounting _accounting;

    public FuturesPaperSimulator(FuturesAccounting accounting)
    {
        _accounting = accounting;
    }

    public FuturesPaperSimulationResult Simulate(FuturesPaperSimulationRequest request)
    {
        var position = request.Position;
        var feePaid = 0m;
        var fundingPaid = decimal.Max(0m, request.FundingCostUsdt);

        if (request.Intent is not null)
        {
            var orderFee = CalculateFee(request.Intent.NotionalUsdt, request.FeeRatePercent);
            feePaid += orderFee;
            position = _accounting.ApplyFill(position, new FuturesFill
            {
                Action = request.Intent.Action,
                Quantity = request.Intent.Quantity,
                Price = request.Intent.Price,
                MarkPrice = request.MarkPrice > 0m ? request.MarkPrice : request.Intent.Price,
                Fee = orderFee,
                Funding = -fundingPaid,
                Leverage = request.Intent.Leverage,
                LiquidationPrice = request.Intent.LiquidationPrice ?? 0m,
                PositionIdx = request.Intent.PositionIdx
            });
        }
        else
        {
            position = _accounting.MarkToMarket(position, request.MarkPrice, -fundingPaid);
        }

        if (!IsLiquidated(position, request.MarkPrice))
        {
            return new FuturesPaperSimulationResult
            {
                Position = position,
                FeePaid = feePaid,
                FundingPaid = fundingPaid,
                IsLiquidated = false,
                Reason = "Futures paper simulation completed."
            };
        }

        var liquidationPrice = position.LiquidationPrice;
        var liquidationFee = CalculateFee(position.Size * liquidationPrice, request.FeeRatePercent);
        feePaid += liquidationFee;
        var liquidatedPosition = _accounting.ApplyFill(position, new FuturesFill
        {
            Action = ResolveCloseAction(position),
            Quantity = position.Size,
            Price = liquidationPrice,
            MarkPrice = liquidationPrice,
            Fee = liquidationFee,
            Leverage = position.Leverage,
            PositionIdx = position.PositionIdx
        });

        return new FuturesPaperSimulationResult
        {
            Position = liquidatedPosition,
            FeePaid = feePaid,
            FundingPaid = fundingPaid,
            IsLiquidated = true,
            Reason = "Futures paper position liquidated."
        };
    }

    private static bool IsLiquidated(FuturesPositionSnapshot position, decimal markPrice)
    {
        if (position.Size <= 0m || position.LiquidationPrice <= 0m || markPrice <= 0m)
        {
            return false;
        }

        return IsLong(position.Side)
            ? markPrice <= position.LiquidationPrice
            : markPrice >= position.LiquidationPrice;
    }

    private static FuturesTradeAction ResolveCloseAction(FuturesPositionSnapshot position) =>
        IsLong(position.Side) ? FuturesTradeAction.CloseLong : FuturesTradeAction.CloseShort;

    private static bool IsLong(string side) =>
        string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Long", StringComparison.OrdinalIgnoreCase);

    private static decimal CalculateFee(decimal notional, decimal feeRatePercent) =>
        notional * feeRatePercent / 100m;
}

public sealed class FuturesPaperSimulationRequest
{
    public FuturesPositionSnapshot Position { get; init; } = new();

    public FuturesTradeIntent? Intent { get; init; }

    public decimal MarkPrice { get; init; }

    public decimal FeeRatePercent { get; init; } = 0.06m;

    public decimal FundingCostUsdt { get; init; }
}

public sealed class FuturesPaperSimulationResult
{
    public FuturesPositionSnapshot Position { get; init; } = new();

    public bool IsLiquidated { get; init; }

    public decimal FeePaid { get; init; }

    public decimal FundingPaid { get; init; }

    public string Reason { get; init; } = string.Empty;
}
