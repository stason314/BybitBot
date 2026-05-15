using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class FuturesAccounting
{
    public FuturesPositionSnapshot ApplyFill(FuturesPositionSnapshot position, FuturesFill fill)
    {
        if (fill.Quantity <= 0m)
        {
            return MarkToMarket(position, fill.MarkPrice, fill.Funding);
        }

        return fill.Action switch
        {
            FuturesTradeAction.OpenLong => OpenPosition(position, fill, "Buy"),
            FuturesTradeAction.OpenShort => OpenPosition(position, fill, "Sell"),
            FuturesTradeAction.CloseLong => ClosePosition(position, fill, "Buy"),
            FuturesTradeAction.CloseShort => ClosePosition(position, fill, "Sell"),
            FuturesTradeAction.ReduceOnlyClose => CloseReduceOnly(position, fill),
            _ => MarkToMarket(position, fill.MarkPrice, fill.Funding)
        };
    }

    public FuturesPositionSnapshot MarkToMarket(FuturesPositionSnapshot position, decimal markPrice, decimal funding = 0m)
    {
        var effectiveMarkPrice = markPrice > 0m ? markPrice : position.MarkPrice;
        var positionValue = position.Size * effectiveMarkPrice;
        var unrealizedPnl = CalculateUnrealizedPnl(position.Side, position.Size, position.EntryPrice, effectiveMarkPrice);
        var leverage = position.Leverage <= 0m ? 1m : position.Leverage;

        return new FuturesPositionSnapshot
        {
            Symbol = position.Symbol,
            Category = position.Category,
            Side = position.Size <= 0m ? "None" : position.Side,
            Size = position.Size,
            EntryPrice = position.EntryPrice,
            MarkPrice = effectiveMarkPrice,
            LiquidationPrice = position.LiquidationPrice,
            PositionValueUsdt = positionValue,
            MarginUsedUsdt = positionValue / leverage,
            Leverage = leverage,
            UnrealizedPnl = unrealizedPnl,
            RealizedPnl = position.RealizedPnl,
            Funding = position.Funding + funding,
            PositionIdx = position.PositionIdx,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private FuturesPositionSnapshot OpenPosition(FuturesPositionSnapshot position, FuturesFill fill, string side)
    {
        if (position.Size > 0m && !IsSameSide(position.Side, side))
        {
            throw new InvalidOperationException("Futures accounting cannot open the opposite side without closing the current position first.");
        }

        var existingCost = position.Size * position.EntryPrice;
        var addedCost = fill.Quantity * fill.Price;
        var newSize = position.Size + fill.Quantity;
        var entryPrice = newSize > 0m ? (existingCost + addedCost) / newSize : 0m;
        var markPrice = fill.MarkPrice > 0m ? fill.MarkPrice : fill.Price;
        var leverage = fill.Leverage > 0m ? fill.Leverage : decimal.Max(position.Leverage, 1m);
        var liquidationPrice = fill.LiquidationPrice > 0m
            ? fill.LiquidationPrice
            : EstimateLiquidationPrice(side, entryPrice, leverage);

        return MarkToMarket(
            new FuturesPositionSnapshot
            {
                Symbol = position.Symbol,
                Category = position.Category,
                Side = side,
                Size = newSize,
                EntryPrice = entryPrice,
                MarkPrice = markPrice,
                LiquidationPrice = liquidationPrice,
                Leverage = leverage,
                RealizedPnl = position.RealizedPnl - fill.Fee,
                Funding = position.Funding + fill.Funding,
                PositionIdx = fill.PositionIdx
            },
            markPrice);
    }

    private FuturesPositionSnapshot CloseReduceOnly(FuturesPositionSnapshot position, FuturesFill fill)
    {
        return IsLong(position.Side)
            ? ClosePosition(position, fill, "Buy")
            : ClosePosition(position, fill, "Sell");
    }

    private FuturesPositionSnapshot ClosePosition(FuturesPositionSnapshot position, FuturesFill fill, string expectedSide)
    {
        if (position.Size <= 0m || !IsSameSide(position.Side, expectedSide))
        {
            return MarkToMarket(position, fill.MarkPrice, fill.Funding);
        }

        var closedQuantity = decimal.Min(position.Size, fill.Quantity);
        var remainingQuantity = position.Size - closedQuantity;
        var closePnl = IsLong(expectedSide)
            ? (fill.Price - position.EntryPrice) * closedQuantity
            : (position.EntryPrice - fill.Price) * closedQuantity;
        var realizedPnl = position.RealizedPnl + closePnl - fill.Fee + fill.Funding;
        var markPrice = fill.MarkPrice > 0m ? fill.MarkPrice : fill.Price;
        var leverage = fill.Leverage > 0m ? fill.Leverage : decimal.Max(position.Leverage, 1m);

        return MarkToMarket(
            new FuturesPositionSnapshot
            {
                Symbol = position.Symbol,
                Category = position.Category,
                Side = remainingQuantity > 0m ? position.Side : "None",
                Size = remainingQuantity,
                EntryPrice = remainingQuantity > 0m ? position.EntryPrice : 0m,
                MarkPrice = markPrice,
                LiquidationPrice = remainingQuantity > 0m ? position.LiquidationPrice : 0m,
                Leverage = leverage,
                RealizedPnl = realizedPnl,
                Funding = position.Funding,
                PositionIdx = position.PositionIdx
            },
            markPrice);
    }

    private static decimal CalculateUnrealizedPnl(string side, decimal size, decimal entryPrice, decimal markPrice)
    {
        if (size <= 0m || entryPrice <= 0m || markPrice <= 0m)
        {
            return 0m;
        }

        return IsLong(side)
            ? (markPrice - entryPrice) * size
            : (entryPrice - markPrice) * size;
    }

    private static decimal EstimateLiquidationPrice(string side, decimal entryPrice, decimal leverage)
    {
        if (entryPrice <= 0m || leverage <= 0m)
        {
            return 0m;
        }

        var leverageMove = 1m / leverage;
        return IsLong(side)
            ? decimal.Max(0m, entryPrice * (1m - leverageMove))
            : entryPrice * (1m + leverageMove);
    }

    private static bool IsSameSide(string currentSide, string expectedSide) =>
        IsLong(currentSide) == IsLong(expectedSide);

    private static bool IsLong(string side) =>
        string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Long", StringComparison.OrdinalIgnoreCase);
}

public sealed class FuturesFill
{
    public FuturesTradeAction Action { get; init; }

    public decimal Quantity { get; init; }

    public decimal Price { get; init; }

    public decimal MarkPrice { get; init; }

    public decimal Fee { get; init; }

    public decimal Funding { get; init; }

    public decimal Leverage { get; init; }

    public decimal LiquidationPrice { get; init; }

    public int PositionIdx { get; init; }
}
