namespace TradingEngine.Domain;

public record PropFirmRuleSet(
    string Id,
    string DisplayName,
    string DrawdownType,
    double MaxDailyLossPercent,
    double MaxTotalLossPercent,
    double ProfitTargetPercent,
    int MinTradingDays,
    string EquityDefinition,
    string DailyResetTimeUtc,
    string DailyResetTimezone,
    bool AllowTradesDuringNews,
    string NewsImpactFilter,
    int NewsWindowMinutesBefore,
    int NewsWindowMinutesAfter,
    bool AllowWeekendHolding,
    string WeekendCloseUtc,
    string WeekendNoOpenUtc,
    string ProtectionResetPolicy,
    bool ForceCloseOnBreach,
    DailyDdBase DailyDdBase = DailyDdBase.InitialBalance)
{
    public double MaxWeeklyLossPercent { get; init; } = 0.04;
    public double MaxMonthlyLossPercent { get; init; } = 0.08;
    public bool RequireProfitTarget { get; init; } = true;
    public GracePeriodOptions GracePeriod { get; init; } = new();
    public ProtectionToggles Toggles { get; init; } = new();

    /// <summary>
    /// FTMO 1-Step "Best Day" consistency rule (live-verified 2026-07-27): the best single
    /// positive day must not exceed this share of total Positive Days' Profit for a pass to be
    /// granted; exceeding it is NOT a breach — it defers pass eligibility until diluted.
    /// Null = rule absent (all pre-2026 rule sets). Exactly-at-share passes (FTMO's own
    /// two-day 50/50 example is a legal pass).
    /// </summary>
    public double? BestDayMaxShare { get; init; }
}
