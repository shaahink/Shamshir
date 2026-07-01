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
    ///
    /// Policies:
    ///   • "NextTradingDay" — clears on the next day boundary (FTMO default)
    ///   • "Never"           — protection is permanent until manual reset
    ///   • "AccountReset"    — only clears on an explicit account-level reset
    ///   • Unknown/missing  → treated as "Never" for safety
    /// </summary>
    public bool ClearsOn(ProtectionBoundary boundary) => Cause switch
    {
        ProtectionCause.None => true,
        ProtectionCause.DailyDrawdown => ResetPolicy != "Never" && boundary == ProtectionBoundary.Day,
        ProtectionCause.WeeklyDrawdown => ResetPolicy != "Never" && boundary is ProtectionBoundary.Day or ProtectionBoundary.Week,
        ProtectionCause.MonthlyDrawdown => ResetPolicy != "Never" && boundary == ProtectionBoundary.Month,
        ProtectionCause.MaxDrawdown => ResetPolicy switch
        {
            "NextTradingDay" => boundary == ProtectionBoundary.Day,
            "Never" => false,
            "AccountReset" => false,
            _ => false,
        },
        _ => false,
    };
}

public enum ProtectionBoundary { Day, Week, Month }
