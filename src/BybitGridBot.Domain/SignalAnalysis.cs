namespace BybitGridBot.Domain;

public sealed class SignalAnalysis
{
    public SignalType Signal { get; init; } = SignalType.Hold;

    public decimal Confidence { get; init; }

    public required string Reason { get; init; }

    public decimal EmaFast { get; init; }

    public decimal EmaSlow { get; init; }

    public decimal Rsi { get; init; }

    public decimal BollingerPosition { get; init; }

    public decimal VolumeRatio { get; init; }

    public decimal TrendStrength { get; init; }
}
