namespace TradingEngine.Domain;

public record RiskState(
    bool TradingAllowed,
    bool InProtectionMode,
    string? ProtectionReason,
    decimal DailyDrawdownUsed,
    decimal MaxDrawdownUsed,
    decimal DailyDrawdownLimit,
    decimal MaxDrawdownLimit,
    DateTime? ProtectionUntilUtc);
