namespace BybitGridBot.Domain;

public sealed record ExecutionTurnoverStats(
    decimal DailyTurnoverUsdt,
    decimal TotalTurnoverUsdt);
