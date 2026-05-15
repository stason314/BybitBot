namespace BybitGridBot.Domain;

public sealed class Signal
{
    public SignalType Type { get; init; } = SignalType.Hold;

    public TradeSide? Direction { get; init; }

    public decimal Strength { get; init; }

    public decimal Confidence { get; init; }

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
