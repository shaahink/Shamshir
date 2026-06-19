namespace TradingEngine.Domain;

/// <summary>
/// Authoritative protection slice of the kernel state (iter-35 A2).
///
/// Today this lives imperatively on <c>RiskManager.CurrentState.InProtectionMode</c> +
/// <c>RiskManager._protectionCause</c>, and the breach watchdog in <c>AccountProcessor</c> mutates it.
/// iter-35 moves it here so the reducer is the single authority and protection is part of the
/// replayable <see cref="EngineState"/>. The imperative copies are then deleted (see Kill-List in
/// docs/iterations/iter-35/PLAN.md).
///
/// This type fixes <b>C4</b> structurally: MaxDD-caused protection currently never exits because
/// <c>OnDailyReset</c> only clears <see cref="ProtectionCause.DailyDrawdown"/>. Here, <see cref="ClearsOn"/>
/// is the single place that owns "does protection from cause X clear on boundary Y", driven by
/// <see cref="ResetPolicy"/> from the active <c>PropFirmRuleSet.ProtectionResetPolicy</c>.
/// </summary>
public sealed record ProtectionState(
    bool InProtectionMode,
    ProtectionCause Cause,
    string? Reason,
    string ResetPolicy,
    DateTime? UntilUtc)
{
    /// <summary>Not in protection; default reset policy. Used as the kernel's initial protection slice.</summary>
    public static ProtectionState None => new(false, ProtectionCause.None, null, "NextTradingDay", null);

    public ProtectionState Enter(ProtectionCause cause, string reason, DateTime? untilUtc = null) =>
        this with { InProtectionMode = true, Cause = cause, Reason = reason, UntilUtc = untilUtc };

    public ProtectionState Clear() =>
        this with { InProtectionMode = false, Cause = ProtectionCause.None, Reason = null, UntilUtc = null };

    /// <summary>
    /// Whether protection from the current <see cref="Cause"/> should clear when the given period
    /// boundary rolls. Daily-DD protection clears on a day roll; weekly/monthly clear on their roll;
    /// MaxDD protection clears only per <see cref="ResetPolicy"/>.
    /// TODO(deepseek): complete the ResetPolicy matrix (C4). The known policy today is "NextTradingDay";
    /// confirm the full set from PropFirmRuleSet.ProtectionResetPolicy and the FTMO rule docs. This is
    /// the ONLY place allowed to decide protection-exit — do not re-add the logic to RiskManager.
    /// </summary>
    public bool ClearsOn(ProtectionBoundary boundary) => Cause switch
    {
        ProtectionCause.None => true,
        ProtectionCause.DailyDrawdown => boundary == ProtectionBoundary.Day,
        ProtectionCause.WeeklyDrawdown => boundary is ProtectionBoundary.Day or ProtectionBoundary.Week,
        ProtectionCause.MonthlyDrawdown => true,
        // MaxDD: policy-driven. "NextTradingDay" clears on the next day roll; other policies (e.g. never,
        // or only on account reset) must be honored here. TODO(deepseek): implement the full matrix.
        ProtectionCause.MaxDrawdown => ResetPolicy == "NextTradingDay" && boundary == ProtectionBoundary.Day,
        _ => false,
    };
}

public enum ProtectionBoundary { Day, Week, Month }
