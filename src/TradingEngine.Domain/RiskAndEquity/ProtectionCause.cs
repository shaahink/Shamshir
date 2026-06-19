namespace TradingEngine.Domain;

// iter-35 (A2/H2): Weekly/Monthly added so weekly & monthly DD breaches — which today reach
// ConstraintSet but are never enforced — can enter protection with an attributable cause and clear on
// the right boundary (see ProtectionState.ClearsOn). Additive: existing callers are unaffected.
public enum ProtectionCause { None, DailyDrawdown, MaxDrawdown, WeeklyDrawdown, MonthlyDrawdown }
