namespace TradingEngine.Host;

/// <summary>
/// The venue-feedback bridge (iter-36 K2). Translates the venue's <see cref="ExecutionEvent"/> /
/// <see cref="AccountUpdate"/> back into kernel <see cref="EngineEvent"/>s so position lifecycle,
/// drawdown, protection and breach are all evolved by the reducer (EngineState as the single authority)
/// instead of by <c>PositionTracker</c> / <c>AccountProcessor</c>.
///
/// Pure translation — no I/O, no wall-clock. Sim-time + price come straight off the venue event. The
/// order id on the venue event already equals the kernel's order id (the K2 <c>ClientOrderId</c>
/// unification), so no id mapping is needed; the kernel finds the position by that id.
/// </summary>
public static class KernelFeedback
{
    /// <summary>
    /// One venue execution → one kernel event. A <see cref="OrderState.Filled"/> is an OrderFilled (the
    /// position lifecycle decides entry-fill vs close-fill by the position's current phase); a Cancelled
    /// (expired resting limit) and a Rejected map to their kernel counterparts. The <paramref name="symbol"/>
    /// is resolved by the caller from the kernel position the order belongs to (the venue event omits it).
    /// </summary>
    public static EngineEvent? FromExecution(ExecutionEvent e, Symbol symbol) => e.NewState switch
    {
        OrderState.PartiallyFilled =>
            new OrderPartiallyFilled(e.OrderId, symbol, e.FilledLots, e.FillPrice ?? new Price(0m), e.TimestampUtc),
        OrderState.Filled =>
            new OrderFilled(e.OrderId, symbol, e.FilledLots, e.FillPrice ?? new Price(0m), e.TimestampUtc),
        OrderState.Cancelled =>
            new OrderCancelled(e.OrderId, symbol, e.RejectionReason ?? "CANCELLED", e.TimestampUtc),
        OrderState.Rejected =>
            new OrderRejected(e.OrderId, symbol, e.RejectionReason ?? "REJECTED", e.TimestampUtc),
        _ => null,
    };

    /// <summary>
    /// A venue account snapshot → an <see cref="EquityObserved"/>, which the kernel folds into the
    /// authoritative drawdown + runs the breach watchdog on (force-close / protection, toggle-gated). A
    /// flat book reports Equity == Balance (not 0) — the C5 regression the K2 test guards.
    /// </summary>
    public static EquityObserved FromAccount(AccountUpdate a) =>
        new(a.Balance, a.Equity, a.FloatingPnL, a.TimestampUtc);
}
