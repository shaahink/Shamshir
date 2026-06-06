namespace TradingEngine.Risk;

public sealed class RiskManager(
    DrawdownTracker drawdownTracker) : IRiskManager
{
    public RiskState CurrentState { get; private set; } = new(
        TradingAllowed: true,
        InProtectionMode: false,
        ProtectionReason: null,
        DailyDrawdownUsed: 0,
        MaxDrawdownUsed: 0,
        DailyDrawdownLimit: 0,
        MaxDrawdownLimit: 0,
        ProtectionUntilUtc: null);

    private PropFirmRuleSet? _activeRuleSet;

    public void EnterProtectionMode(string reason)
    {
        CurrentState = CurrentState with
        {
            InProtectionMode = true,
            ProtectionReason = reason,
            TradingAllowed = false,
        };
    }

    public void SetActiveRuleSet(PropFirmRuleSet ruleSet)
    {
        _activeRuleSet = ruleSet;
    }

    public IReadOnlyList<RiskViolation> Validate(TradeIntent intent, EquitySnapshot equity)
    {
        var violations = new List<RiskViolation>();

        if (CurrentState.InProtectionMode)
        {
            violations.Add(new("PROTECTION_MODE_ACTIVE", "Trading suspended: protection mode"));
        }

        if (_activeRuleSet != null)
        {
            if (equity.CurrentDailyDrawdown >= (decimal)_activeRuleSet.MaxDailyLossPercent)
            {
                violations.Add(new("DAILY_DD_LIMIT", "Daily drawdown limit reached"));
            }

            if (equity.CurrentMaxDrawdown >= (decimal)_activeRuleSet.MaxTotalLossPercent)
            {
                violations.Add(new("MAX_DD_LIMIT", "Maximum drawdown limit reached"));
            }
        }

        return violations;
    }

    public decimal CalculateLotSize(TradeIntent intent, EquitySnapshot equity, RiskProfile profile)
    {
        var drawdownScale = DrawdownScaler.ComputeScaleFactor(
            equity.CurrentMaxDrawdown,
            (decimal)profile.MaxTotalDrawdownPercent,
            profile.DrawdownScaleThreshold,
            profile.DrawdownScaleFloor);

        const decimal pipValue = 10m;
        var slPips = 20.0;

        return PositionSizer.Calculate(
            equity.Equity,
            RiskPercent.Parse(profile.RiskPerTradePercent),
            new Pips(slPips),
            pipValue,
            (decimal)drawdownScale,
            (decimal)profile.MaxConcurrentPositions,
            0.01m,
            0.01m);
    }

    public void OnEquityUpdate(EquitySnapshot snapshot)
    {
        drawdownTracker.OnEquityUpdate(snapshot.Equity);

        CurrentState = CurrentState with
        {
            DailyDrawdownUsed = snapshot.CurrentDailyDrawdown,
            MaxDrawdownUsed = snapshot.CurrentMaxDrawdown,
        };
    }

    public void OnDailyReset(decimal currentEquity)
    {
        drawdownTracker.OnDailyReset(currentEquity);

        if (CurrentState.InProtectionMode)
        {
            CurrentState = CurrentState with
            {
                InProtectionMode = false,
                ProtectionReason = null,
            };
        }
    }
}
