using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class TrendFollowingStrategy : ITradingStrategy
{
    public TradingStrategyType Type => TradingStrategyType.TrendFollowing;

    public string DisplayName => "Trend Following";

    public StrategyScore Score(MarketRegime regime, GridOptions options)
    {
        var isAllowed = regime == MarketRegime.Uptrend;
        return new StrategyScore
        {
            StrategyType = StrategyType.TrendFollowing,
            Score = isAllowed ? 78m : 25m,
            Confidence = isAllowed ? 0.72m : 0.25m,
            RequiredCapitalPercent = options.TrendCapitalPercent,
            IsAllowed = isAllowed,
            Reason = isAllowed ? "Uptrend regime supports trend-following long exposure." : "Trend-following is disabled outside uptrend."
        };
    }
}
