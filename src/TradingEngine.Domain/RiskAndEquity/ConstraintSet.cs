namespace TradingEngine.Domain;

/// <summary>
/// Resolved constraint values for the trading engine, projected from
/// <see cref="RiskProfile"/> and <see cref="PropFirmRuleSet"/> at startup.
/// All DD/loss limits are <see cref="decimal"/> — no (decimal)double casts on money math.
/// Consumed by the pre-trade gate, worst-case projection, and breach watchdog alike.
/// Config types (<see cref="RiskProfile"/>, <see cref="PropFirmRuleSet"/>) never reach
/// the engine logic directly.
/// </summary>
public sealed record ConstraintSet(
    string Id,

    // ── Loss limits (from PropFirmRuleSet, decimal) ──
    decimal MaxDailyLoss,
    decimal MaxTotalLoss,
    decimal MaxWeeklyLoss,
    decimal MaxMonthlyLoss,
    decimal ProfitTarget,

    // ── Drawdown config ──
    string DrawdownType,
    DailyDdBase DailyDdBase,

    // ── Trade controls (from RiskProfile) ──
    decimal RiskPerTrade,
    int MaxConcurrentPositions,
    decimal MaxExposure,

    // ── Session / news ──
    bool AllowTradesDuringNews,
    bool AllowWeekendHolding,
    bool ForceCloseOnBreach,

    // ── Protection toggles (iter-35 B1) ──
    bool DailyDdEnabled = true,
    bool MaxDdEnabled = true,
    bool WeeklyDdEnabled = false,
    bool MonthlyDdEnabled = false,
    bool ProfitTargetEnabled = true,
    bool ForceCloseOnBreachEnabled = true,
    bool NewsFilterEnabled = false,
    bool WeekendFilterEnabled = false,
    bool GovernorEnabled = true)
{
    /// <summary>
    /// Project from both config sources. PropFirmRuleSet limits take precedence over
    /// RiskProfile limits where they overlap (daily/max DD percents).
    /// </summary>
    /// <summary>
    /// Project from both config sources. <see cref="RiskProfile"/> fields and
    /// <see cref="PropFirmRuleSet"/> fields are normalized to decimal fractions
    /// (0.05 = 5%). Where they overlap, PropFirmRuleSet limits take precedence.
    /// </summary>
    public static ConstraintSet Resolve(RiskProfile profile, PropFirmRuleSet ruleSet)
    {
        return new ConstraintSet(
            Id: ruleSet.Id,

            MaxDailyLoss: (decimal)ruleSet.MaxDailyLossPercent,
            MaxTotalLoss: (decimal)ruleSet.MaxTotalLossPercent,
            MaxWeeklyLoss: (decimal)ruleSet.MaxWeeklyLossPercent,
            MaxMonthlyLoss: (decimal)ruleSet.MaxMonthlyLossPercent,
            ProfitTarget: (decimal)ruleSet.ProfitTargetPercent,

            DrawdownType: ruleSet.DrawdownType,
            DailyDdBase: ruleSet.DailyDdBase,

            RiskPerTrade: (decimal)profile.RiskPerTradePercent,
            MaxConcurrentPositions: profile.MaxConcurrentPositions,
            MaxExposure: (decimal)profile.MaxExposurePercent,

            AllowTradesDuringNews: ruleSet.AllowTradesDuringNews,
            AllowWeekendHolding: ruleSet.AllowWeekendHolding,
            ForceCloseOnBreach: ruleSet.ForceCloseOnBreach,

            DailyDdEnabled: ruleSet.Toggles.DailyDdEnabled,
            MaxDdEnabled: ruleSet.Toggles.MaxDdEnabled,
            WeeklyDdEnabled: ruleSet.Toggles.WeeklyDdEnabled,
            MonthlyDdEnabled: ruleSet.Toggles.MonthlyDdEnabled,
            ProfitTargetEnabled: ruleSet.Toggles.ProfitTargetEnabled,
            ForceCloseOnBreachEnabled: ruleSet.Toggles.ForceCloseOnBreachEnabled,
            NewsFilterEnabled: ruleSet.Toggles.NewsFilterEnabled,
            WeekendFilterEnabled: ruleSet.Toggles.WeekendFilterEnabled,
            GovernorEnabled: ruleSet.Toggles.GovernorEnabled);
    }
}
