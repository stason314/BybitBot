using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public static class FuturesOrderLinkIds
{
    public static string Create(FuturesTradeAction action)
    {
        var prefix = action switch
        {
            FuturesTradeAction.OpenLong => "flo",
            FuturesTradeAction.CloseLong => "flc",
            FuturesTradeAction.OpenShort => "fso",
            FuturesTradeAction.CloseShort => "fsc",
            _ => "flc"
        };
        return $"{prefix}{DateTimeOffset.UtcNow:yyMMddHHmmss}{Guid.NewGuid():N}"[..35];
    }

    public static bool IsManaged(string orderLinkId) =>
        orderLinkId.StartsWith("flo", StringComparison.OrdinalIgnoreCase) ||
        orderLinkId.StartsWith("flc", StringComparison.OrdinalIgnoreCase) ||
        orderLinkId.StartsWith("fso", StringComparison.OrdinalIgnoreCase) ||
        orderLinkId.StartsWith("fsc", StringComparison.OrdinalIgnoreCase);
}
