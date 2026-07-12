namespace TradingEngine.Domain;

public enum OrderState
{
    Created,
    Submitted,
    Accepted,

    /// <summary>
    /// F40: the venue accepted an entry order and is RESTING it — no fill yet. cTrader reports this for
    /// every limit/stop entry that does not fill on submit. The enum had no such member, so the adapter's
    /// <c>Enum.Parse</c> threw and abandoned the whole <c>bar_result</c> batch it appeared in (taking any
    /// sibling fills and the account update with it). Carries no fill price — consumers must not treat it
    /// as an execution.
    /// </summary>
    Pending,

    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected
}
