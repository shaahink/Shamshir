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
    ConstraintSet? Constraints { get; }
    void InitializeDrawdownIfNeeded(decimal initialBalance, string drawdownType = "Fixed");
    void UpdateEquityLevels(decimal rawEquity);
    void OnDailyReset(decimal currentEquity);
    void OnWeeklyReset(decimal currentEquity);
    void OnMonthlyReset(decimal currentEquity);
    void RegisterPosition(Guid positionId, string strategyId, decimal openRiskAmount);
    void DeregisterPosition(Guid positionId);
    void EnterProtectionMode(string reason, ProtectionCause cause);
    void ExitProtectionMode();
    bool ValidateBudgetEntry(decimal newRiskAmount, EquitySnapshot equity, decimal perTradeRiskAmount);

    /// <summary>
    /// The prop-firm compliance verdict for a candidate signal, or null if not blocked. Exposed so the
    /// kernel order gate (iter-35 AF2) can fold this impure, service-dependent check into the pure
    /// <c>PreTradeGate</c> without re-implementing the compliance service. Mirrors the COMPLIANCE_BLOCK
    /// branch in <c>Validate</c>.
    /// </summary>
    string? CheckComplianceBlock(TradeIntent intent, RiskProfile profile);
}
