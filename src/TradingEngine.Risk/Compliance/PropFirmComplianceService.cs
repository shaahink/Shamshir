namespace TradingEngine.Risk.Compliance;

public sealed class PropFirmComplianceService : IPropFirmComplianceService
{
    private readonly PropFirmRuleSet _ruleSet;
    private readonly DrawdownTracker _drawdownTracker;
    private readonly IEngineClock _clock;
    private readonly IPassProbabilityEstimator _estimator;
    private int _tradingDaysMet;
    private bool _tradeTakenToday;

    public PropFirmComplianceService(
        PropFirmRuleSet ruleSet,
        DrawdownTracker drawdownTracker,
        IEngineClock clock,
        IPassProbabilityEstimator estimator)
    {
        _ruleSet = ruleSet;
        _drawdownTracker = drawdownTracker;
        _clock = clock;
        _estimator = estimator;
    }

    public ComplianceResult ValidateSignal(TradeIntent intent, ExtendedRiskState state, RiskProfile profile)
    {
        return Validate(state);
    }

    public ComplianceResult ValidateAtBarOpen(ExtendedRiskState state, DateTime utcNow)
    {
        return Validate(state);
    }

    private ComplianceResult Validate(ExtendedRiskState state)
    {
        var violations = new List<string>();

        if (state.DailyDrawdownUsed >= (decimal)_ruleSet.MaxDailyLossPercent)
            violations.Add($"Daily DD limit exceeded ({state.DailyDrawdownUsed:P1} >= {_ruleSet.MaxDailyLossPercent:P1})");

        if (state.WeeklyDrawdownUsed >= (decimal)_ruleSet.MaxWeeklyLossPercent)
            violations.Add($"Weekly DD limit exceeded ({state.WeeklyDrawdownUsed:P1} >= {_ruleSet.MaxWeeklyLossPercent:P1})");

        if (state.MonthlyDrawdownUsed >= (decimal)_ruleSet.MaxMonthlyLossPercent)
            violations.Add($"Monthly DD limit exceeded ({state.MonthlyDrawdownUsed:P1} >= {_ruleSet.MaxMonthlyLossPercent:P1})");

        if (state.DailyDrawdownUsed >= (decimal)_ruleSet.MaxDailyLossPercent
            || state.WeeklyDrawdownUsed >= (decimal)_ruleSet.MaxWeeklyLossPercent
            || state.MonthlyDrawdownUsed >= (decimal)_ruleSet.MaxMonthlyLossPercent)
        {
            return new ComplianceResult(false, violations, ComplianceSeverity.Block);
        }

        if (state.IsDrawdownAccelerating)
        {
            violations.Add("Drawdown is accelerating");
            return new ComplianceResult(true, violations, ComplianceSeverity.Warning);
        }

        return new ComplianceResult(true, violations, ComplianceSeverity.None);
    }

    public PassProbabilityEstimate EstimatePassProbability(PassProbabilityInput input)
    {
        return _estimator.Estimate(input);
    }

    public void OnDailyReset(DateTime utcNow, decimal equity)
    {
        if (_tradeTakenToday)
        {
            _tradingDaysMet++;
            _tradeTakenToday = false;
        }
    }

    public void OnWeeklyReset(DateTime utcNow, decimal equity) { }
    public void OnMonthlyReset(DateTime utcNow, decimal equity) { }

    public ComplianceSummary GetSummary()
    {
        var targetEquity = _drawdownTracker.InitialAccountBalance * (1m + (decimal)_ruleSet.ProfitTargetPercent);
        var maxDD = _drawdownTracker.GetMaxDrawdownFloor((decimal)_ruleSet.MaxTotalLossPercent);
        var equity = _drawdownTracker.PeakEquity;

        return new ComplianceSummary
        {
            IsInChallenge = _ruleSet.RequireProfitTarget,
            CurrentEquity = equity,
            TargetEquity = targetEquity,
            MaxDrawdownFloor = maxDD,
            TradingDaysMet = _tradingDaysMet,
            TradingDaysRequired = _ruleSet.MinTradingDays,
            Status = equity >= targetEquity ? "OnTrack"
                : _drawdownTracker.CurrentMaxDrawdown > (decimal)(_ruleSet.MaxTotalLossPercent * 0.8) ? "AtRisk"
                : "OnTrack",
        };
    }

    internal void NotifyTradeTaken() => _tradeTakenToday = true;
}
