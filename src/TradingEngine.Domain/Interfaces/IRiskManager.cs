namespace TradingEngine.Domain;

public interface IRiskManager
{
    decimal InitialBalance { get; }
    DrawdownState Drawdown { get; }
    decimal CalculateLotSize(TradeIntent intent, EquitySnapshot equity, RiskProfile profile, decimal currentMid);
    IReadOnlyList<RiskViolation> Validate(TradeIntent intent, EquitySnapshot equity, RiskProfile profile, decimal currentMid);
    IReadOnlyList<RiskViolation> ValidateOrder(TradeIntent intent, EquitySnapshot equity, RiskProfile profile, decimal currentMid,
        SymbolInfo symbolInfo, decimal slPips, decimal pipValuePerLot, decimal lots,
        IReadOnlyList<ProjectedPosition> openPositions, out decimal downsizedLots);
    ExtendedRiskState CurrentState { get; }
    PropFirmRuleSet? ActiveRuleSet { get; }
    void InitializeDrawdownIfNeeded(decimal initialBalance, string drawdownType = "Fixed");
    void UpdateEquityLevels(decimal rawEquity);
    void OnDailyReset(decimal currentEquity);
    void OnWeeklyReset(decimal currentEquity);
    void OnMonthlyReset(decimal currentEquity);
    void RegisterPosition(Guid positionId, string strategyId, decimal openRiskAmount);
    void DeregisterPosition(Guid positionId);
    void EnterProtectionMode(string reason, ProtectionCause cause);
    bool ValidateBudgetEntry(decimal newRiskAmount, EquitySnapshot equity, decimal perTradeRiskAmount);
}
