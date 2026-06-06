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
    DailyDdBase DailyDdBase = DailyDdBase.InitialBalance);
