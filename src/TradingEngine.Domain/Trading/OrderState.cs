namespace TradingEngine.Domain;

public enum OrderState
{
    Created,
    Submitted,
    Accepted,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected
}
