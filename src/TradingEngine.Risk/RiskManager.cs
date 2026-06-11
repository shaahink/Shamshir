using TradingEngine.Risk.Compliance;
using TradingEngine.Risk.Sizing;

namespace TradingEngine.Risk;

public sealed class RiskManager(
    DrawdownTracker drawdownTracker,
    ISymbolInfoRegistry symbolRegistry,
    Func<string, string, decimal> getCrossRate,
    INewsFilter newsFilter,
    SessionFilter sessionFilter,
    IEngineClock clock,
    ICurrencyExposureTracker currencyExposure) : IRiskManager
{
    private readonly ICurrencyExposureTracker _currencyExposure = currencyExposure;

    public decimal InitialBalance => drawdownTracker.InitialAccountBalance;

    public ExtendedRiskState CurrentState { get; private set; } = new()
    {
        TradingAllowed = true, InProtectionMode = false, ProtectionReason = null,
        DailyDrawdownUsed = 0, WeeklyDrawdownUsed = 0, MonthlyDrawdownUsed = 0,
        MaxDrawdownUsed = 0, DailyDrawdownLimit = 0, MaxDrawdownLimit = 0, ProtectionUntilUtc = null,
    };

    private PropFirmRuleSet? _activeRuleSet;
    private IPropFirmComplianceService? _complianceService;
    private SizeModifierPipeline? _sizePipeline;
    private ProtectionCause _protectionCause = ProtectionCause.None;
    private bool _forceClosePending;
    private readonly Dictionary<Guid, (string StrategyId, decimal Risk)> _openPositionRisk = new();

    public void SetActiveRuleSet(PropFirmRuleSet ruleSet)
    {
        _activeRuleSet = ruleSet;
        drawdownTracker.DailyDdBaseMode = ruleSet.DailyDdBase;
    }

    public void SetComplianceService(IPropFirmComplianceService svc)
    {
        _complianceService = svc;
    }

    public void SetSizePipeline(SizeModifierPipeline pipeline)
    {
        _sizePipeline = pipeline;
    }

    public void EnterProtectionMode(string reason, ProtectionCause cause)
    {
        _protectionCause = cause;
        CurrentState = CurrentState with { InProtectionMode = true, ProtectionReason = reason, TradingAllowed = false };

        if (cause == ProtectionCause.MaxDrawdown && _activeRuleSet?.ForceCloseOnBreach == true)
            _forceClosePending = true;
    }

    public bool ConsumeForceClosePending() => Interlocked.Exchange(ref _forceClosePending, false);

    public void RegisterPosition(Guid positionId, string strategyId, decimal openRiskAmount)
        => _openPositionRisk[positionId] = (strategyId, openRiskAmount);

    public void DeregisterPosition(Guid positionId) => _openPositionRisk.Remove(positionId);

    public IReadOnlyList<RiskViolation> Validate(TradeIntent intent, EquitySnapshot equity, RiskProfile profile)
    {
        var violations = new List<RiskViolation>();

        if (CurrentState.InProtectionMode)
            violations.Add(new("PROTECTION_MODE_ACTIVE", "Trading suspended: protection mode"));

        if (_activeRuleSet != null)
        {
            if (equity.CurrentDailyDrawdown >= (decimal)_activeRuleSet.MaxDailyLossPercent)
                violations.Add(new("DAILY_DD_LIMIT", "Daily drawdown limit reached"));
            if (equity.CurrentMaxDrawdown >= (decimal)_activeRuleSet.MaxTotalLossPercent)
                violations.Add(new("MAX_DD_LIMIT", "Maximum drawdown limit reached"));
        }

        if (_openPositionRisk.Count >= profile.MaxConcurrentPositions)
            violations.Add(new("MAX_POSITIONS", $"Max concurrent positions ({profile.MaxConcurrentPositions}) reached"));

        var openForStrategy = _openPositionRisk.Values.Count(v => v.StrategyId == intent.StrategyId);
        if (openForStrategy >= profile.MaxConcurrentPositions)
            violations.Add(new("STRATEGY_MAX_POSITIONS", $"Strategy {intent.StrategyId}: max {profile.MaxConcurrentPositions} positions reached"));

        var symbolInfo = symbolRegistry.Get(intent.Symbol);
        var totalOpenRisk = _openPositionRisk.Values.Sum(v => v.Risk);

        if (equity.Equity <= 0)
        {
            violations.Add(new("MAX_EXPOSURE", "Max total exposure exceeded"));
        }
        else
        {
            var entryPrice = intent.LimitPrice ?? intent.StopLoss;
            var slPips = PipCalculator.Distance(entryPrice, intent.StopLoss, symbolInfo);
            var pipValue = PipCalculator.PipValuePerLot(symbolInfo, entryPrice.Value, getCrossRate);
            var newPositionRisk = (decimal)slPips.Value * pipValue * 1.0m;

            if ((totalOpenRisk + newPositionRisk) / equity.Equity > (decimal)profile.MaxExposurePercent)
                violations.Add(new("MAX_EXPOSURE", "Max total exposure exceeded"));
        }

        if (_activeRuleSet?.AllowTradesDuringNews == false && newsFilter.IsNewsWindowActive(intent.Symbol, clock.UtcNow))
            violations.Add(new("NEWS_WINDOW", "High-impact news window is active"));

        if (sessionFilter.IsWeekend(clock.UtcNow) && _activeRuleSet?.AllowWeekendHolding == false)
            violations.Add(new("WEEKEND_RESTRICTION", "Weekend close approaching — no new positions"));

        if (_complianceService is not null)
        {
            var compResult = _complianceService.ValidateSignal(intent, CurrentState, profile);
            if (compResult.Severity == ComplianceSeverity.Block)
            {
                foreach (var v in compResult.Violations)
                    violations.Add(new("COMPLIANCE_BLOCK", v));
            }
        }

        return violations;
    }

    public decimal CalculateLotSize(TradeIntent intent, EquitySnapshot equity, RiskProfile profile, decimal currentMid)
    {
        var symbolInfo = symbolRegistry.Get(intent.Symbol);
        var entryPrice = intent.LimitPrice ?? new Price(currentMid);
        var slDistance = PipCalculator.Distance(entryPrice, intent.StopLoss, symbolInfo);
        var pipValue = PipCalculator.PipValuePerLot(symbolInfo, entryPrice.Value, getCrossRate);

        var drawdownScale = _sizePipeline is not null
            ? (decimal)_sizePipeline.ComputeCombinedScale(new SizeModifierContext
            {
                Equity = equity,
                Profile = profile,
                Intent = intent,
            })
            : (decimal)DrawdownScaler.ComputeScaleFactor(
                equity.CurrentMaxDrawdown, (decimal)profile.MaxTotalDrawdownPercent,
                profile.DrawdownScaleThreshold, profile.DrawdownScaleFloor);

        return PositionSizer.Calculate(
            equity.Equity, RiskPercent.Parse(profile.RiskPerTradePercent),
            slDistance, pipValue, drawdownScale,
            (decimal)symbolInfo.MaxLots, symbolInfo.MinLots, symbolInfo.LotStep);
    }

    public void UpdateEquityLevels(decimal rawEquity)
    {
        drawdownTracker.OnEquityUpdate(rawEquity);
        CurrentState = CurrentState with
        {
            DailyDrawdownUsed = drawdownTracker.CurrentDailyDrawdown,
            WeeklyDrawdownUsed = drawdownTracker.CurrentWeeklyDrawdown,
            MonthlyDrawdownUsed = drawdownTracker.CurrentMonthlyDrawdown,
            MaxDrawdownUsed = drawdownTracker.CurrentMaxDrawdown,
            DrawdownVelocity = drawdownTracker.DrawdownVelocity,
            IsDrawdownAccelerating = drawdownTracker.IsAccelerating,
        };
    }

    public void OnDailyReset(decimal currentEquity)
    {
        drawdownTracker.OnDailyReset(currentEquity);
        if (CurrentState.InProtectionMode && _protectionCause == ProtectionCause.DailyDrawdown)
        {
            _protectionCause = ProtectionCause.None;
            CurrentState = CurrentState with { InProtectionMode = false, ProtectionReason = null, TradingAllowed = true };
        }
    }

    public void OnWeeklyReset(decimal currentEquity)
    {
        drawdownTracker.OnWeeklyReset(currentEquity);
        _complianceService?.OnWeeklyReset(clock.UtcNow, currentEquity);
    }

    public void OnMonthlyReset(decimal currentEquity)
    {
        drawdownTracker.OnMonthlyReset(currentEquity);
        _complianceService?.OnMonthlyReset(clock.UtcNow, currentEquity);
    }
}
