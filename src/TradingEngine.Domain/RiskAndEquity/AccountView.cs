namespace TradingEngine.Domain;

/// <summary>
/// The time-varying account slice of the kernel state (iter-35 A2). Folded from each
/// <see cref="EquityObserved"/> event. Lives in <see cref="EngineState"/> (not <see cref="KernelConfig"/>)
/// because it changes every bar — the pre-trade gate and the journal <see cref="RiskSnapshot"/> read
/// equity/balance from here, so the kernel stays a pure function of <c>(state, event)</c>.
/// </summary>
public sealed record AccountView(decimal Balance, decimal Equity, decimal FloatingPnL)
{
    public static AccountView Flat => new(0m, 0m, 0m);
}
