namespace BybitGridBot.Domain;

public sealed class GridBotSettings
{
    public string Symbol { get; init; } = string.Empty;

    public string Category { get; init; } = "spot";

    public StrategySelectionMode StrategySelectionMode { get; init; } = StrategySelectionMode.Manual;

    public TradingStrategyType StrategyType { get; init; } = TradingStrategyType.Grid;

    public string StrategyConfigJson { get; init; } = "{}";

    public decimal LowerPrice { get; init; }

    public decimal UpperPrice { get; init; }

    public decimal Step { get; init; }

    public decimal OrderSizeUsdt { get; init; }

    public decimal StopLowerPrice { get; init; }

    public decimal StopUpperPrice { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
