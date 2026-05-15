namespace BybitGridBot.App;

public static class FuturesStateKeys
{
    public static string ForSymbol(string symbol) => $"futures:{symbol.Trim().ToUpperInvariant()}";
}
