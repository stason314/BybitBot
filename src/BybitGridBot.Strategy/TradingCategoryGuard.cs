namespace BybitGridBot.Strategy;

public static class TradingCategoryGuard
{
    public static void ValidateSpotWorkerCategory(string category, bool futuresEnabled)
    {
        if (string.Equals(category, "spot", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!futuresEnabled)
        {
            throw new InvalidOperationException("CATEGORY must be spot unless FUTURES_ENABLED=true.");
        }

        throw new InvalidOperationException("Non-spot categories must use the dedicated futures context. GridBotWorker supports spot only.");
    }
}
