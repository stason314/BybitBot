using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public static class FuturesOrderLinkIds
{
    public static string Create(FuturesTradeAction action)
    {
        var prefix = action == FuturesTradeAction.OpenLong ? "flo" : "flc";
        return $"{prefix}{DateTimeOffset.UtcNow:yyMMddHHmmss}{Guid.NewGuid():N}"[..35];
    }

    public static bool IsManaged(string orderLinkId) =>
        orderLinkId.StartsWith("flo", StringComparison.OrdinalIgnoreCase) ||
        orderLinkId.StartsWith("flc", StringComparison.OrdinalIgnoreCase);
}
