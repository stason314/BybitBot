using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class GridStrategy : IGridTradingStrategy
{
    public TradingStrategyType Type => TradingStrategyType.Grid;

    public string DisplayName => "Grid";

    public IReadOnlyList<GridLevel> BuildGrid(GridOptions options)
    {
        if (options.LowerPrice >= options.UpperPrice)
        {
            throw new InvalidOperationException("GRID_LOWER_PRICE must be lower than GRID_UPPER_PRICE.");
        }

        if (options.Step <= 0m)
        {
            throw new InvalidOperationException("GRID_STEP must be positive.");
        }

        var levels = new List<GridLevel>();
        var price = options.LowerPrice;
        var index = 0;

        while (price <= options.UpperPrice + (options.Step / 10m))
        {
            levels.Add(new GridLevel(index, decimal.Round(price, 8, MidpointRounding.ToZero)));
            price += options.Step;
            index++;
        }

        return levels;
    }

    public IReadOnlyList<GridLevel> BuildGrid(GridOptions options, decimal atr, decimal referencePrice)
    {
        if (!options.GridDynamicStepEnabled || atr <= 0m || referencePrice <= 0m)
        {
            return BuildGrid(options);
        }

        var dynamicStep = CalculateDynamicStep(options, atr, referencePrice);
        return BuildGrid(options, dynamicStep);
    }

    public decimal CalculateDynamicStep(GridOptions options, decimal atr, decimal referencePrice)
    {
        if (atr <= 0m || referencePrice <= 0m)
        {
            return options.Step;
        }

        var atrStep = atr * options.GridStepAtrMultiplier;
        var minStep = referencePrice * options.GridMinStepPercent / 100m;
        var maxStep = referencePrice * options.GridMaxStepPercent / 100m;
        return decimal.Round(Clamp(atrStep, minStep, maxStep), 8, MidpointRounding.AwayFromZero);
    }

    public IReadOnlyList<GridLevel> GetBuyLevels(IReadOnlyList<GridLevel> levels, decimal currentPrice) =>
        levels.Where(level => level.Price < currentPrice).ToArray();

    public IReadOnlyList<GridLevel> GetSellLevels(IReadOnlyList<GridLevel> levels, decimal currentPrice) =>
        levels.Where(level => level.Price > currentPrice).ToArray();

    public GridLevel? GetNextUpperLevel(IReadOnlyList<GridLevel> levels, decimal price) =>
        levels.Where(level => level.Price > price).OrderBy(level => level.Price).FirstOrDefault();

    public GridLevel? GetNextLowerLevel(IReadOnlyList<GridLevel> levels, decimal price) =>
        levels.Where(level => level.Price < price).OrderByDescending(level => level.Price).FirstOrDefault();

    public bool IsWithinTradingRange(GridOptions options, decimal price) =>
        price >= options.LowerPrice && price <= options.UpperPrice;

    public bool IsBelowStop(GridOptions options, decimal price) => price < options.StopLowerPrice;

    public bool IsAboveStop(GridOptions options, decimal price) => price > options.StopUpperPrice;

    public bool CanCreateGridIntents(
        GridOptions options,
        MarketPhaseResult marketPhase,
        decimal currentPrice,
        bool bigRedGuardActive)
    {
        if (bigRedGuardActive ||
            marketPhase.Phase is MarketPhase.Dump or MarketPhase.HighVolatility or MarketPhase.BreakoutDown or MarketPhase.Unknown)
        {
            return false;
        }

        if (marketPhase.Phase == MarketPhase.BreakoutUp && (options.GridHandoffToBreakout || options.EnableBreakoutHandoff))
        {
            return false;
        }

        return marketPhase.Phase == MarketPhase.RangeBound &&
               IsWithinTradingRange(options, currentPrice) &&
               !IsBelowStop(options, currentPrice) &&
               !IsAboveStop(options, currentPrice);
    }

    public IReadOnlyList<TradeIntent> BuildRebalanceIntents(
        GridOptions options,
        IReadOnlyList<GridLevel> levels,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders)
    {
        if (!IsWithinTradingRange(options, currentPrice) ||
            IsBelowStop(options, currentPrice) ||
            IsAboveStop(options, currentPrice))
        {
            return [];
        }

        var intents = new List<TradeIntent>();
        var lower = GetNextLowerLevel(levels, currentPrice);
        var upper = GetNextUpperLevel(levels, currentPrice);

        if (lower is not null && !HasActiveOrder(activeOrders, TradeSide.Buy, lower.Price))
        {
            intents.Add(BuildIntent(options, TradeSide.Buy, lower.Price, "Grid buy below current price."));
        }

        if (upper is not null && !HasActiveOrder(activeOrders, TradeSide.Sell, upper.Price))
        {
            intents.Add(BuildIntent(options, TradeSide.Sell, upper.Price, "Grid sell above current price."));
        }

        return intents;
    }

    public IReadOnlyList<TradeIntent> BuildRebalanceIntents(
        GridOptions options,
        IReadOnlyList<GridLevel> levels,
        decimal currentPrice,
        IReadOnlyCollection<GridOrder> activeOrders,
        MarketPhaseResult marketPhase,
        bool bigRedGuardActive)
    {
        return CanCreateGridIntents(options, marketPhase, currentPrice, bigRedGuardActive)
            ? BuildRebalanceIntents(options, levels, currentPrice, activeOrders)
            : [];
    }

    private static bool HasActiveOrder(IReadOnlyCollection<GridOrder> activeOrders, TradeSide side, decimal price)
    {
        return activeOrders.Any(order => order.IsActive && order.Side == side && order.Price == price);
    }

    private static TradeIntent BuildIntent(GridOptions options, TradeSide side, decimal price, string reason)
    {
        return new TradeIntent
        {
            StrategyType = StrategyType.Grid,
            Symbol = options.Symbol,
            Side = side,
            OrderType = OrderType.Limit,
            Price = price,
            Quantity = decimal.Round(options.OrderSizeUsdt / price, 8, MidpointRounding.ToZero),
            Reason = reason,
            Confidence = 0.7m,
            ExpectedRisk = options.Step,
            ExpectedReward = options.Step
        };
    }

    private IReadOnlyList<GridLevel> BuildGrid(GridOptions options, decimal withStep)
    {
        return BuildGrid(new GridOptions
        {
            Symbol = options.Symbol,
            Category = options.Category,
            LowerPrice = options.LowerPrice,
            UpperPrice = options.UpperPrice,
            Step = withStep,
            OrderSizeUsdt = options.OrderSizeUsdt,
            StopLowerPrice = options.StopLowerPrice,
            StopUpperPrice = options.StopUpperPrice
        });
    }

    private static decimal Clamp(decimal value, decimal min, decimal max)
    {
        return value < min ? min : value > max ? max : value;
    }
}
