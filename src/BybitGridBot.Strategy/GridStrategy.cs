using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class GridStrategy
{
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
}
