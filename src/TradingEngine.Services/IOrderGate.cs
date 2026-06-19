namespace TradingEngine.Services;

/// <summary>
/// The pre-trade order gate seam (iter-35 AF2 cutover). Abstracts "given a signal, decide + size +
/// submit (or reject)". Two implementations exist behind this during the strangler cutover:
///   • <see cref="OrderDispatcher"/> — the legacy gate (RiskManager.ValidateOrder + CalculateLotSize);
///   • <c>KernelOrderGate</c> (Host) — the kernel gate (PreTradeGate + KernelSizing), which becomes the
///     single authority once the golden + app verification pass, after which OrderDispatcher is deleted.
/// <see cref="TradingLoop"/> depends on this interface so the gate can be swapped via DI without
/// touching the loop, and so the golden harness can prove the two gates are behaviour-equivalent.
/// </summary>
public interface IOrderGate
{
    Task<OrderContext?> DispatchAsync(
        TradeIntent intent, EquitySnapshot equity, decimal currentMid,
        IBrokerAdapter broker, IReadOnlyList<ProjectedPosition> openPositions, CancellationToken ct);
}
