using BybitGridBot.Domain;

namespace BybitGridBot.App;

internal static class OrderLinkIdFactory
{
    public static string Create(TradeSide side)
    {
        var prefix = side == TradeSide.Buy ? "b" : "s";
        return $"gb{prefix}{DateTimeOffset.UtcNow:yyMMddHHmmss}{Guid.NewGuid():N}"[..35];
    }

    public static bool IsManaged(string orderLinkId) =>
        orderLinkId.StartsWith("gb", StringComparison.OrdinalIgnoreCase);
}
