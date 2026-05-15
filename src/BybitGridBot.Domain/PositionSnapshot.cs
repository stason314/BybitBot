namespace BybitGridBot.Domain;

public sealed class PositionSnapshot
{
    public string Symbol { get; init; } = string.Empty;

    public decimal BaseAssetQuantity { get; init; }

    public decimal AverageEntryPrice { get; init; }

    public decimal QuoteAssetBalance { get; init; }

    public decimal CurrentPrice { get; init; }

    public decimal PositionValueUsdt => BaseAssetQuantity * CurrentPrice;
}
