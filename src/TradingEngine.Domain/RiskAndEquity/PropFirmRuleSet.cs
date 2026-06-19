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
}
