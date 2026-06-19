using TradingEngine.Domain;

namespace TradingEngine.Engine;

/// <summary>
/// Captures the authoritative <see cref="RiskSnapshot"/> from kernel state for the journal + live
/// monitor (iter-35 A3). Now that <see cref="EngineState"/> carries Account + Drawdown + Protection +
/// Governor, this is a pure projection — the canonical <c>captureRisk</c> for <see cref="KernelDriver"/>.
/// </summary>
public static class RiskSnapshots
{
    public static RiskSnapshot Capture(EngineState s) => new(
        Balance: s.Account.Balance,
        Equity: s.Account.Equity,
        FloatingPnL: s.Account.FloatingPnL,
        DailyDrawdown: s.Drawdown.CurrentDailyDrawdown,
        MaxDrawdown: s.Drawdown.CurrentMaxDrawdown,
        WeeklyDrawdown: s.Drawdown.CurrentWeeklyDrawdown,
        MonthlyDrawdown: s.Drawdown.CurrentMonthlyDrawdown,
        InProtectionMode: s.Protection.InProtectionMode,
        ProtectionCause: s.Protection.InProtectionMode ? s.Protection.Cause.ToString() : null,
        GovernorState: s.Governor.State.ToString(),
        OpenPositions: s.Positions.Count);
}
