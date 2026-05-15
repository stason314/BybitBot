using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public interface ITradingStrategy
{
    TradingStrategyType Type { get; }

    string DisplayName { get; }
}

public interface IGridTradingStrategy : ITradingStrategy
{
    IReadOnlyList<GridLevel> BuildGrid(GridOptions options);

    IReadOnlyList<GridLevel> GetBuyLevels(IReadOnlyList<GridLevel> levels, decimal currentPrice);

    IReadOnlyList<GridLevel> GetSellLevels(IReadOnlyList<GridLevel> levels, decimal currentPrice);

    GridLevel? GetNextUpperLevel(IReadOnlyList<GridLevel> levels, decimal price);

    GridLevel? GetNextLowerLevel(IReadOnlyList<GridLevel> levels, decimal price);

    bool IsWithinTradingRange(GridOptions options, decimal price);

    bool IsBelowStop(GridOptions options, decimal price);

    bool IsAboveStop(GridOptions options, decimal price);

    bool CanCreateGridIntents(
        GridOptions options,
        MarketPhaseResult marketPhase,
        decimal currentPrice,
        bool bigRedGuardActive,
        bool aggressiveModeActive = false);
}
