namespace TradingEngine.Domain;

public interface IRiskManager
{
    decimal InitialBalance { get; }
    decimal CalculateLotSize(TradeIntent intent, EquitySnapshot equity, RiskProfile profile, decimal currentMid);
    IReadOnlyList<RiskViolation> Validate(TradeIntent intent, EquitySnapshot equity, RiskProfile profile);
    ExtendedRiskState CurrentState { get; }
    void UpdateEquityLevels(decimal rawEquity);
    void OnDailyReset(decimal currentEquity);
    void OnWeeklyReset(decimal currentEquity);
    void OnMonthlyReset(decimal currentEquity);
    void RegisterPosition(Guid positionId, string strategyId, decimal openRiskAmount);
    void DeregisterPosition(Guid positionId);
    bool ConsumeForceClosePending();
}
