namespace TradingEngine.Domain;

public interface IRiskManager
{
    decimal CalculateLotSize(TradeIntent intent, EquitySnapshot equity, RiskProfile profile);
    IReadOnlyList<RiskViolation> Validate(TradeIntent intent, EquitySnapshot equity);
    RiskState CurrentState { get; }
    void OnEquityUpdate(EquitySnapshot snapshot);
}
