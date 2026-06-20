namespace TradingEngine.Services;

/// <summary>The gate's decision for an accepted signal: the submitted order id, the sized lots, the
/// computed risk amount, and the resolved profile. Part of the <see cref="IOrderGate"/> contract (kept in
/// src with the interface; the imperative implementations live in the test-support oracle, iter-36 K4).</summary>
public sealed record OrderContext(Guid OrderId, decimal Lots, decimal RiskAmount, RiskProfile Profile);

/// <summary>
/// The pre-trade order gate seam (iter-35 AF2 cutover). Abstracts "given a signal, decide + size +
/// submit (or reject)". The imperative implementations — <c>OrderDispatcher</c> (legacy gate) and
/// <c>KernelOrderGate</c> — are no longer in the production path (the kernel's <c>PreTradeGate</c> is the
/// single authority, iter-36 K4); they survive only as the golden regression oracle in the test-support
/// assembly. This interface stays in src as the contract the oracle's <c>TradingLoop</c> still depends on.
/// </summary>
public interface IOrderGate
{
    Task<OrderContext?> DispatchAsync(
        TradeIntent intent, EquitySnapshot equity, decimal currentMid,
        IBrokerAdapter broker, IReadOnlyList<ProjectedPosition> openPositions, CancellationToken ct);
}
