namespace TradingEngine.Domain;

public interface IPropFirmComplianceService
{
    ComplianceResult ValidateSignal(TradeIntent intent, ExtendedRiskState state, RiskProfile profile);
    ComplianceResult ValidateAtBarOpen(ExtendedRiskState state, DateTime utcNow);
    PassProbabilityEstimate EstimatePassProbability(PassProbabilityInput input);
    void OnDailyReset(DateTime utcNow, decimal equity);
    void OnWeeklyReset(DateTime utcNow, decimal equity);
    void OnMonthlyReset(DateTime utcNow, decimal equity);
    ComplianceSummary GetSummary();
}
