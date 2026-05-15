using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class ExpectedProfitFilter
{
    public ExpectedProfitDecision Evaluate(GridOptions options, TradeSide side, decimal expectedProfitPercent, string source)
    {
        var required = RequiredPercent(options);
        var allowed = expectedProfitPercent >= required;
        var reason = allowed
            ? $"{source} expected profit {expectedProfitPercent:F4}% is above required {required:F4}%."
            : $"{source} expected profit {expectedProfitPercent:F4}% is below fees/slippage/min threshold {required:F4}%.";

        return new ExpectedProfitDecision(allowed, expectedProfitPercent, required, side, source, reason);
    }

    public ExpectedProfitDecision EvaluateGrid(GridOptions options, decimal price, decimal step)
    {
        var stepPercent = price <= 0m ? 0m : step / price * 100m;
        return Evaluate(options, TradeSide.Buy, stepPercent, "Grid");
    }

    public static decimal RequiredPercent(GridOptions options)
    {
        return (options.FeePercent * 2m) + options.SlippagePercent + options.MinExpectedProfitPercent;
    }
}

public sealed record ExpectedProfitDecision(
    bool IsAllowed,
    decimal ExpectedProfitPercent,
    decimal RequiredProfitPercent,
    TradeSide Side,
    string Source,
    string Reason);
