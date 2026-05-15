namespace BybitGridBot.Strategy;

public sealed class FuturesInstrumentRules
{
    public decimal TickSize { get; init; }

    public decimal QtyStep { get; init; }

    public decimal BasePrecision { get; init; }

    public decimal MinOrderQty { get; init; }

    public decimal MinOrderAmount { get; init; }

    public decimal RoundPrice(decimal price) => RoundDown(price, TickSize);

    public decimal RoundQuantity(decimal quantity)
    {
        var step = QtyStep > 0m ? QtyStep : BasePrecision;
        return RoundDown(quantity, step);
    }

    private static decimal RoundDown(decimal value, decimal step)
    {
        if (step <= 0m)
        {
            return value;
        }

        return Math.Floor(value / step) * step;
    }
}
