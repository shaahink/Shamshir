namespace TradingEngine.Domain;

/// <summary>
/// The decision core (iter-35 A2): a pure function <c>(state, event) → (state', effects)</c>.
/// The <see cref="KernelDriver"/> calls this once per event. Implementations close over the
/// run-constant <see cref="KernelConfig"/> (so the gate/sizing/reset logic has the constraints it
/// needs) while remaining pure over time-varying inputs — no I/O, no wall-clock, no id minting.
/// </summary>
public interface IKernel
{
    EngineDecision Decide(EngineState state, EngineEvent evt);
}

/// <summary>
/// Run-constant configuration resolved ONCE at run start from the <see cref="ConfigSet"/>. Kept as a
/// closure constant (here), not as <see cref="EngineState"/> — only time-varying data (positions,
/// drawdown, equity, governor, protection) belongs in state. This split is what lets the kernel be a
/// pure function of <c>(state, event)</c> while still knowing the rules.
/// </summary>
public sealed record KernelConfig(
    ConstraintSet Constraints,
    RiskProfile Profile,
    SizingPolicyOptions Sizing,
    Func<Symbol, SymbolInfo> ResolveSymbol,
    Func<EngineState, IReadOnlyList<ProjectedPosition>> ProjectOpenPositions,
    int Seed)
{
    // ResolveSymbol / ProjectOpenPositions are deterministic lookups (config + state), kept as
    // delegates so the kernel doesn't depend on the symbol registry / pip-value services directly.
    // TODO(deepseek): ProjectOpenPositions currently has to recompute each open position's worst-case
    // (slPips * pipValue * lots). Cleaner long-term: track RiskAmount on PositionState at entry (the
    // gate already computes it → RegisterRisk) and sum it from state, removing the recompute + the
    // cross-rate dependency here. Also still TODO: prop-firm daily reset time/timezone (NEW-1) for the
    // reset boundary, and wiring the B1 enable-toggles into ConstraintSet.
}
