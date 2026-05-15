namespace BybitGridBot.Domain;

public sealed class StrategyCooldownRecord
{
    public string Symbol { get; init; } = string.Empty;

    public string StrategyType { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public DateTimeOffset CooldownUntil { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
