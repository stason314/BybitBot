namespace BybitGridBot.Domain;

public sealed class MarketSnapshot
{
    public string Symbol { get; init; } = string.Empty;

    public decimal CurrentPrice { get; init; }

    public IReadOnlyList<Candle> Candles { get; init; } = [];

    public IReadOnlyList<Candle> BtcCandles { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
