namespace TradingEngine.Domain;

/// <summary>
/// Impure gate verdicts the pure kernel cannot compute itself — they depend on wall-clock / external
/// services (news calendar, session clock, prop-firm compliance, the legacy governor). The evaluator
/// stage (iter-36 K1) computes them — already folding in the rule-set conditions
/// (AllowTradesDuringNews / AllowWeekendHolding) — at sim-time and carries them on
/// <see cref="OrderProposed"/>, so the kernel gate (<c>PreTradeGate.Evaluate</c>) stays a deterministic
/// function of <c>(state, proposal, config, verdicts)</c> and a replay reproduces the same verdict
/// bit-for-bit (no date-dependence). A set flag here means "block". Default = nothing blocks.
///
/// Lives in Domain (not nested in PreTradeGate) precisely so the Domain-level <see cref="OrderProposed"/>
/// event can carry it — the evaluator freezes the impure verdict onto the event, the pure kernel reads it.
/// </summary>
public readonly record struct ExternalVerdicts(
    bool NewsActive = false,
    bool WeekendRestricted = false,
    string? ComplianceBlockReason = null,
    string? GovernorBlockReason = null);
