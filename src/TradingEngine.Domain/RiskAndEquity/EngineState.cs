namespace TradingEngine.Domain;

/// <summary>
/// Kernel state — the authoritative, replayable engine state.
///
/// iter-35 (A2) note: historically ONLY the <see cref="Positions"/> slice was wired (via
/// PositionTracker + EngineReducer); <see cref="Governor"/>/<see cref="Drawdown"/> were frozen at
/// Empty while RiskManager owned the real state imperatively. iter-35 finishes the kernel so the
/// reducer is the single authority for ALL slices and the imperative copies are deleted (Kill-List).
/// <see cref="Protection"/> is the new authoritative protection slice (was RiskManager.CurrentState +
/// _protectionCause). See docs/iterations/iter-35/PLAN.md + SKELETON-HANDOVER.md.
/// </summary>
public sealed record EngineState(
    IReadOnlyDictionary<Guid, PositionState> Positions,
    GovernorState Governor,
    DrawdownState Drawdown,
    int OpenPositionCount,
    ProtectionState Protection = null!,
    AccountView Account = null!)
{
    // Defaulted + coalesced (mirrors DrawdownState.VelocityWindow) so existing positional constructions
    // stay valid during migration. TODO(deepseek): once RiskManager's imperative protection/account
    // state is deleted, make these required positional parameters.
    public ProtectionState Protection { get; init; } = Protection ?? ProtectionState.None;
    public AccountView Account { get; init; } = Account ?? AccountView.Flat;

    public static EngineState Empty => new(
        new Dictionary<Guid, PositionState>(),
        new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
        new DrawdownState(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "Fixed"),
        0,
        ProtectionState.None,
        AccountView.Flat);
}
