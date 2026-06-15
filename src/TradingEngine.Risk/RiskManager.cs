using TradingEngine.Engine;
using TradingEngine.Risk.Compliance;
using TradingEngine.Risk.Sizing;

namespace TradingEngine.Risk;

public sealed class RiskManager(
    ISymbolInfoRegistry symbolRegistry,
    Func<string, string, decimal> getCrossRate,
    INewsFilter newsFilter,
    SessionFilter sessionFilter,
    IEngineClock clock,
    ICurrencyExposureTracker currencyExposure,
    ITradingGovernor? governor,
    SizingPolicyOptions sizingPolicy) : IRiskManager
{
    private readonly ICurrencyExposureTracker _currencyExposure = currencyExposure;
    public DrawdownState Drawdown { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "Fixed");

    public decimal InitialBalance => Drawdown.InitialAccountBalance;

    public void InitializeDrawdown(decimal initialBalance, string drawdownType = "Fixed")
    {
        Drawdown = DrawdownReducer.CreateInitial(initialBalance, drawdownType);
    }

    public void InitializeDrawdownIfNeeded(decimal initialBalance, string drawdownType = "Fixed")
    {
        if (!Drawdown.IsInitialized)
            InitializeDrawdown(initialBalance, drawdownType);
    }

    public ExtendedRiskState CurrentState { get; private set; } = new()
    {
        TradingAllowed = true, InProtectionMode = false, ProtectionReason = null,
        DailyDrawdownUsed = 0, WeeklyDrawdownUsed = 0, MonthlyDrawdownUsed = 0,
        MaxDrawdownUsed = 0, DailyDrawdownLimit = 0, MaxDrawdownLimit = 0, ProtectionUntilUtc = null,
    };

    public PropFirmRuleSet? ActiveRuleSet { get; private set; }
    public ConstraintSet? Constraints { get; private set; }
    private IPropFirmComplianceService? _complianceService;
    private SizeModifierPipeline? _sizePipeline;
    private ProtectionCause _protectionCause = ProtectionCause.None;
    private readonly Dictionary<Guid, (string StrategyId, decimal Risk)> _openPositionRisk = new();

    public void SetActiveRuleSet(PropFirmRuleSet ruleSet)
    {
        ActiveRuleSet = ruleSet;
        Drawdown = Drawdown with { DailyDdBaseMode = ruleSet.DailyDdBase.ToString() };
    }

    public void SetComplianceService(IPropFirmComplianceService svc)
    {
        _complianceService = svc;
    }

    public void SetSizePipeline(SizeModifierPipeline pipeline)
    {
        _sizePipeline = pipeline;
    }

    public void SetConstraints(ConstraintSet constraints)
    {
        Constraints = constraints;
    }

    public void EnterProtectionMode(string reason, ProtectionCause cause)
    {
        _protectionCause = cause;
        CurrentState = CurrentState with { InProtectionMode = true, ProtectionReason = reason, TradingAllowed = false };
    }

    public void RegisterPosition(Guid positionId, string strategyId, decimal openRiskAmount)
        => _openPositionRisk[positionId] = (strategyId, openRiskAmount);

    public void DeregisterPosition(Guid positionId) => _openPositionRisk.Remove(positionId);

    public IReadOnlyList<RiskViolation> Validate(TradeIntent intent, EquitySnapshot equity, RiskProfile profile, decimal currentMid)
    {
        var violations = new List<RiskViolation>();

        if (governor is not null)
        {
            var dayStart = Drawdown.DailyStartEquity;
            var dayPnLFraction = dayStart > 0 ? (equity.Equity - dayStart) / dayStart : 0m;
            var governorCtx = new GovernorContext(
                dayPnLFraction,
                dayStart,
                equity.Equity,
                0,
                ActiveRuleSet ?? new PropFirmRuleSet("none", "None", "Fixed", 0.05, 0.10, 0.10, 0,
                    "BalancePlusFloating", "22:00:00", "UTC", false, "High", 0, 0,
                    false, "21:00:00", "20:00:00", "NextTradingDay", false));
            var governorDecision = governor.Evaluate(governorCtx);
            if (!governorDecision.AllowNewTrades)
                violations.Add(new("GOVERNOR", governorDecision.Reason));
        }

        if (CurrentState.InProtectionMode)
            violations.Add(new("PROTECTION_MODE_ACTIVE", "Trading suspended: protection mode"));

        if (Constraints != null)
        {
            if (equity.CurrentDailyDrawdown >= Constraints.MaxDailyLoss)
                violations.Add(new("DAILY_DD_LIMIT", "Daily drawdown limit reached"));
            if (equity.CurrentMaxDrawdown >= Constraints.MaxTotalLoss)
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
            var newPositionRisk = equity.Equity * (Constraints?.RiskPerTrade ?? (decimal)profile.RiskPerTradePercent);

            if ((totalOpenRisk + newPositionRisk) / equity.Equity > (Constraints?.MaxExposure ?? (decimal)profile.MaxExposurePercent))
                violations.Add(new("MAX_EXPOSURE", "Max total exposure exceeded"));
        }

        if (ActiveRuleSet?.AllowTradesDuringNews == false && newsFilter.IsNewsWindowActive(intent.Symbol, clock.UtcNow))
            violations.Add(new("NEWS_WINDOW", "High-impact news window is active"));

        if (sessionFilter.IsWeekend(clock.UtcNow) && ActiveRuleSet?.AllowWeekendHolding == false)
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

    public IReadOnlyList<RiskViolation> ValidateOrder(
        TradeIntent intent, EquitySnapshot equity, RiskProfile profile, decimal currentMid,
        SymbolInfo symbolInfo, decimal slPips, decimal pipValuePerLot, decimal lots,
        IReadOnlyList<ProjectedPosition> openPositions, out decimal downsizedLots)
    {
        downsizedLots = lots;
        var violations = new List<RiskViolation>(Validate(intent, equity, profile, currentMid));
        if (violations.Count > 0) return violations;

        // --- Worst-case projection ---
        var candidateLoss = slPips * pipValuePerLot * lots;
        var openLosses = 0m;
        foreach (var p in openPositions)
            openLosses += p.SlPips * p.PipValuePerLot * p.Lots;

        var totalWorstCaseLoss = candidateLoss + openLosses;
        var projectedEquity = equity.Equity - totalWorstCaseLoss;

        // Daily floor must use the SAME base as the breach detector (DrawdownReducer.Apply /
        // breach watchdog), which keys off DailyDdBase. Using DailyStartEquity unconditionally
        // diverged from the watchdog whenever the day did not start at the initial balance:
        // for an InitialBalance-mode rule set with a below-initial day-start, the gate would wave
        // through an order whose worst case already breaches the (initial-balance) daily limit.
        var maxDailyLoss = Constraints?.MaxDailyLoss ?? (decimal)profile.MaxDailyDrawdownPercent;
        var dailyBaseEquity = (Constraints?.DailyDdBase ?? DailyDdBase.InitialBalance) == DailyDdBase.DailyStart
            ? equity.DailyStartEquity
            : Drawdown.InitialAccountBalance;
        var dailyFloor = dailyBaseEquity * (1m - maxDailyLoss);
        if (projectedEquity < dailyFloor)
        {
            violations.Add(new("WorstCaseDDWouldBreachDaily", "Worst-case projected equity breaches daily drawdown floor"));
            return violations;
        }

        var drawdownBase = Drawdown.DrawdownType == "Trailing" ? equity.Equity : equity.Balance;
        var maxFloor = drawdownBase * (1m - (Constraints?.MaxTotalLoss ?? (decimal)profile.MaxTotalDrawdownPercent));
        if (projectedEquity < maxFloor)
        {
            violations.Add(new("WorstCaseDDWouldBreachOverall", "Worst-case projected equity breaches max drawdown floor"));
            return violations;
        }

        // --- Budget validation with downsizing ---
        var riskAmount = slPips * pipValuePerLot * lots;
        var perTradeRiskAmount = equity.Equity * (Constraints?.RiskPerTrade ?? (decimal)profile.RiskPerTradePercent);

        if (!ValidateBudgetEntry(riskAmount, equity, perTradeRiskAmount))
        {
            while (lots > symbolInfo.MinLots)
            {
                lots = Math.Max(lots * 0.5m, symbolInfo.MinLots);
                lots = Math.Floor(lots / symbolInfo.LotStep) * symbolInfo.LotStep;
                if (lots < symbolInfo.MinLots) break;
                riskAmount = slPips * pipValuePerLot * lots;
                if (ValidateBudgetEntry(riskAmount, equity, perTradeRiskAmount))
                {
                    downsizedLots = lots;
                    break;
                }
            }
            if (lots < symbolInfo.MinLots || !ValidateBudgetEntry(riskAmount, equity, perTradeRiskAmount))
            {
                violations.Add(new("BudgetBlocked", $"Budget exceeded after downsizing: lots={lots:F4} risk={riskAmount:F2}"));
            }
            else
            {
                downsizedLots = lots;
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
                equity.CurrentMaxDrawdown, Constraints?.MaxTotalLoss ?? (decimal)profile.MaxTotalDrawdownPercent,
                profile.DrawdownScaleThreshold, profile.DrawdownScaleFloor);

        return PositionSizer.Calculate(
            equity.Equity, RiskPercent.Parse(profile.RiskPerTradePercent),
            slDistance, pipValue, drawdownScale,
            (decimal)symbolInfo.MaxLots, symbolInfo.MinLots, symbolInfo.LotStep);
    }

    public bool ValidateBudgetEntry(decimal newRiskAmount, EquitySnapshot equity, decimal perTradeRiskAmount)
    {
        var constraints = Constraints;
        if (constraints is null) return true;

        var totalOpenRisk = _openPositionRisk.Values.Sum(v => v.Risk);

        var dailyDdBase = constraints.DailyDdBase == DailyDdBase.DailyStart
            ? Drawdown.DailyStartEquity
            : Drawdown.InitialAccountBalance;

        if (dailyDdBase <= 0) return false;

        var dailyDdUsedFraction = constraints.MaxDailyLoss > 0
            ? CurrentState.DailyDrawdownUsed / constraints.MaxDailyLoss
            : 1m;

        var remainingDailyBudget = (1m - Math.Min(dailyDdUsedFraction, 1m)) * constraints.MaxDailyLoss * dailyDdBase;
        var budgetCap = remainingDailyBudget * (decimal)sizingPolicy.BudgetUseFraction;

        if (totalOpenRisk + newRiskAmount > budgetCap)
            return false;

        if (perTradeRiskAmount > 0 && (decimal)sizingPolicy.MaxPortfolioHeatRiskMultiples > 0)
        {
            var heatCap = perTradeRiskAmount * (decimal)sizingPolicy.MaxPortfolioHeatRiskMultiples;
            if (totalOpenRisk + newRiskAmount > heatCap)
                return false;
        }

        return true;
    }

    public void UpdateEquityLevels(decimal rawEquity)
    {
        Drawdown = DrawdownReducer.Apply(Drawdown, rawEquity);
        CurrentState = CurrentState with
        {
            DailyDrawdownUsed = Drawdown.CurrentDailyDrawdown,
            WeeklyDrawdownUsed = Drawdown.CurrentWeeklyDrawdown,
            MonthlyDrawdownUsed = Drawdown.CurrentMonthlyDrawdown,
            MaxDrawdownUsed = Drawdown.CurrentMaxDrawdown,
            DrawdownVelocity = Drawdown.DrawdownVelocity,
            IsDrawdownAccelerating = Drawdown.IsAccelerating,
        };
    }

    public void OnDailyReset(decimal currentEquity)
    {
        Drawdown = DrawdownReducer.ApplyDailyReset(Drawdown, currentEquity);
        if (CurrentState.InProtectionMode && _protectionCause == ProtectionCause.DailyDrawdown)
        {
            _protectionCause = ProtectionCause.None;
            CurrentState = CurrentState with { InProtectionMode = false, ProtectionReason = null, TradingAllowed = true };
        }
    }

    public void OnWeeklyReset(decimal currentEquity)
    {
        Drawdown = DrawdownReducer.ApplyWeeklyReset(Drawdown, currentEquity);
        _complianceService?.OnWeeklyReset(clock.UtcNow, currentEquity);
    }

    public void OnMonthlyReset(decimal currentEquity)
    {
        Drawdown = DrawdownReducer.ApplyMonthlyReset(Drawdown, currentEquity);
        _complianceService?.OnMonthlyReset(clock.UtcNow, currentEquity);
    }
}
