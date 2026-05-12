namespace BybitGridBot.Domain;

public enum OrderStatus
{
    New = 1,
    PartiallyFilled = 2,
    Filled = 3,
    Cancelled = 4,
    Rejected = 5
}
