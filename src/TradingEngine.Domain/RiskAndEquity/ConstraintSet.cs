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
    bool ForceCloseOnBreach)
{
    /// <summary>
    /// Project from both config sources. PropFirmRuleSet limits take precedence over
    /// RiskProfile limits where they overlap (daily/max DD percents).
    /// </summary>
    /// <summary>
    /// Project from both config sources. Note: <see cref="RiskProfile"/> fields (RiskPerTradePercent,
    /// MaxDailyDrawdownPercent, MaxTotalDrawdownPercent, MaxExposurePercent) are stored as percentages
    /// (1.0 = 1%, 5.0 = 5%) and must be divided by 100. <see cref="PropFirmRuleSet"/> fields are
    /// stored as fractions (0.05 = 5%) and need no conversion.
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

            RiskPerTrade: (decimal)profile.RiskPerTradePercent / 100m,
            MaxConcurrentPositions: profile.MaxConcurrentPositions,
            MaxExposure: (decimal)profile.MaxExposurePercent / 100m,

            AllowTradesDuringNews: ruleSet.AllowTradesDuringNews,
            AllowWeekendHolding: ruleSet.AllowWeekendHolding,
            ForceCloseOnBreach: ruleSet.ForceCloseOnBreach);
    }
}
