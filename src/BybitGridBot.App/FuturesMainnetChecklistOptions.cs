using Microsoft.Extensions.Configuration;

namespace BybitGridBot.App;

public sealed class FuturesMainnetChecklistOptions
{
    [ConfigurationKeyName("FUTURES_MAINNET_CONFIRM_TESTNET_SOAK")]
    public bool ConfirmTestnetSoak { get; init; }

    [ConfigurationKeyName("FUTURES_MAINNET_CONFIRM_PROTECTIVE_STOPS")]
    public bool ConfirmProtectiveStops { get; init; }

    [ConfigurationKeyName("FUTURES_MAINNET_CONFIRM_RESTART_RECOVERY")]
    public bool ConfirmRestartRecovery { get; init; }

    [ConfigurationKeyName("FUTURES_MAINNET_CONFIRM_EMERGENCY_PAUSE")]
    public bool ConfirmEmergencyPause { get; init; }

    [ConfigurationKeyName("FUTURES_MAINNET_CONFIRM_TELEGRAM_ALERTS")]
    public bool ConfirmTelegramAlerts { get; init; }

    public IReadOnlyList<string> MissingItems()
    {
        var missing = new List<string>();
        if (!ConfirmTestnetSoak)
        {
            missing.Add("FUTURES_MAINNET_CONFIRM_TESTNET_SOAK");
        }

        if (!ConfirmProtectiveStops)
        {
            missing.Add("FUTURES_MAINNET_CONFIRM_PROTECTIVE_STOPS");
        }

        if (!ConfirmRestartRecovery)
        {
            missing.Add("FUTURES_MAINNET_CONFIRM_RESTART_RECOVERY");
        }

        if (!ConfirmEmergencyPause)
        {
            missing.Add("FUTURES_MAINNET_CONFIRM_EMERGENCY_PAUSE");
        }

        if (!ConfirmTelegramAlerts)
        {
            missing.Add("FUTURES_MAINNET_CONFIRM_TELEGRAM_ALERTS");
        }

        return missing;
    }
}
