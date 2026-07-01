namespace TradingEngine.Domain;

/// <summary>
/// Per-protection on/off toggles (iter-35 B1). Threaded into <see cref="ConstraintSet"/> so the
/// kernel gate and breach watchdog can skip disabled checks entirely — no need for separate
/// feature-flag plumbing in PreTradeGate or DecideEquity.
/// </summary>
public sealed record ProtectionToggles(
    bool DailyDdEnabled = true,
    bool MaxDdEnabled = true,
    bool WeeklyDdEnabled = false,
    bool MonthlyDdEnabled = false,
    bool ProfitTargetEnabled = true,
    bool ForceCloseOnBreachEnabled = true,
    bool NewsFilterEnabled = false,
    bool WeekendFilterEnabled = false,
    bool GovernorEnabled = true,
    bool ExposureEnabled = true,
    bool BudgetEnabled = true,
    bool MaxPositionsEnabled = true);
