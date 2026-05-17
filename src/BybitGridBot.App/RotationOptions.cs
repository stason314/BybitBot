using System.ComponentModel.DataAnnotations;
using BybitGridBot.Domain;
using Microsoft.Extensions.Configuration;

namespace BybitGridBot.App;

public sealed class RotationOptions
{
    [ConfigurationKeyName("ROTATION_ENABLED")]
    public bool RotationEnabled { get; init; } = false;

    [ConfigurationKeyName("ACTIVE_PAIR_POOL_SIZE")]
    [Range(1, 100)]
    public int ActivePairPoolSize { get; init; } = 5;

    [ConfigurationKeyName("SCAN_INTERVAL_MINUTES")]
    [Range(1, 1440)]
    public int ScanIntervalMinutes { get; init; } = 15;

    [ConfigurationKeyName("MIN_PAIR_LIFETIME_MINUTES")]
    [Range(0, 10080)]
    public int MinPairLifetimeMinutes { get; init; } = 60;

    [ConfigurationKeyName("REPLACEMENT_SCORE_GAP")]
    [Range(typeof(decimal), "0", "100")]
    public decimal ReplacementScoreGap { get; init; } = 12m;

    [ConfigurationKeyName("ALLOW_REPLACE_ONLY_WHEN_FLAT")]
    public bool AllowReplaceOnlyWhenFlat { get; init; } = true;

    [ConfigurationKeyName("MAX_ACTIVE_POSITIONS")]
    [Range(0, 100)]
    public int MaxActivePositions { get; init; } = 5;

    [ConfigurationKeyName("ROTATION_MODE")]
    public RotationMode RotationMode { get; init; } = RotationMode.PaperOnly;
}
