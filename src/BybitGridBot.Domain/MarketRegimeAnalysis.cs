namespace BybitGridBot.Domain;

public sealed class MarketRegimeAnalysis
{
    public MarketRegimeType Regime { get; init; } = MarketRegimeType.Range;

    public decimal Confidence { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public decimal Adx { get; init; }

    public decimal MovePercent { get; init; }

    public decimal RangePercent { get; init; }

    public decimal VolumeRatio { get; init; }

    public decimal? Support { get; init; }

    public decimal? Resistance { get; init; }
}
