namespace BybitGridBot.Domain;

public sealed record Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal Turnover);
