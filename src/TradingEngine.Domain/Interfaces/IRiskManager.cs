namespace TradingEngine.Domain;

public interface IRiskManager
{
    decimal CalculateLotSize(TradeIntent intent, EquitySnapshot equity, RiskProfile profile);
    IReadOnlyList<RiskViolation> Validate(TradeIntent intent, EquitySnapshot equity, RiskProfile profile);
    RiskState CurrentState { get; }
    void OnEquityUpdate(EquitySnapshot snapshot);
    void OnDailyReset(decimal currentEquity);
    void RegisterPosition(Guid positionId, string strategyId, decimal openRiskAmount);
    void DeregisterPosition(Guid positionId);
}
