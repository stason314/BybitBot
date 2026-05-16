using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class FuturesStrategyContext
{
    public FuturesBotSettings Settings { get; init; } = new();

    public IReadOnlyList<Candle> Candles { get; init; } = [];

    public FuturesPositionSnapshot Position { get; init; } = new();

    public IReadOnlyList<FuturesFillRecord> RecentFills { get; init; } = [];

    public decimal CurrentPrice { get; init; }

    public FuturesInstrumentRules Instrument { get; init; } = new();
}
