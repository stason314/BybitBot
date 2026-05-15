using BybitGridBot.Domain;

namespace BybitGridBot.Strategy;

public sealed class CapitalAllocator
{
    public CapitalAllocation Allocate(
        GridOptions options,
        StrategyType strategyType,
        decimal requestedUsdt,
        decimal totalEquityUsdt,
        decimal availableUsdt,
        decimal totalExposureUsdt,
        decimal strategyExposureUsdt)
    {
        var reserve = totalEquityUsdt * options.MinUsdtReservePercent / 100m;
        var maxTotalExposure = totalEquityUsdt * options.MaxTotalExposurePercent / 100m;
        var strategyLimit = totalEquityUsdt * GetStrategyPercent(options, strategyType) / 100m;

        if (requestedUsdt <= 0m)
        {
            return Block(strategyType, requestedUsdt, strategyLimit, availableUsdt, reserve, "Requested capital must be positive.");
        }

        if (strategyExposureUsdt + requestedUsdt > strategyLimit)
        {
            return Block(strategyType, requestedUsdt, strategyLimit, availableUsdt, reserve, "Strategy allocation limit exceeded.");
        }

        if (totalExposureUsdt + requestedUsdt > maxTotalExposure)
        {
            return Block(strategyType, requestedUsdt, strategyLimit, availableUsdt, reserve, "MAX_TOTAL_EXPOSURE_PERCENT limit exceeded.");
        }

        if (availableUsdt - requestedUsdt < reserve)
        {
            return Block(strategyType, requestedUsdt, strategyLimit, availableUsdt, reserve, "MIN_USDT_RESERVE_PERCENT reserve would be violated.");
        }

        return new CapitalAllocation
        {
            StrategyType = strategyType,
            IsAllowed = true,
            RequestedUsdt = requestedUsdt,
            AllocatedUsdt = requestedUsdt,
            StrategyLimitUsdt = strategyLimit,
            AvailableUsdt = availableUsdt,
            ReservedUsdt = reserve,
            Reason = "Capital allocation allowed."
        };
    }

    private static decimal GetStrategyPercent(GridOptions options, StrategyType strategyType)
    {
        return strategyType switch
        {
            StrategyType.Grid => options.GridCapitalPercent,
            StrategyType.Dca => options.DcaCapitalPercent,
            StrategyType.Btd => options.BtdCapitalPercent,
            StrategyType.Breakout => options.BreakoutCapitalPercent,
            StrategyType.TrendFollowing => options.TrendCapitalPercent,
            StrategyType.Pause => 0m,
            _ => 0m
        };
    }

    private static CapitalAllocation Block(
        StrategyType strategyType,
        decimal requestedUsdt,
        decimal strategyLimit,
        decimal availableUsdt,
        decimal reserve,
        string reason) => new()
    {
        StrategyType = strategyType,
        IsAllowed = false,
        RequestedUsdt = requestedUsdt,
        StrategyLimitUsdt = strategyLimit,
        AvailableUsdt = availableUsdt,
        ReservedUsdt = reserve,
        Reason = reason
    };
}
