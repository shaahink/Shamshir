using TradingEngine.Domain;

namespace TradingEngine.Engine;

/// <summary>
/// The single, PURE pre-trade risk gate (iter-35 A2). Replaces — and lets us DELETE — the three
/// scattered, partly-divergent implementations: <c>OrderDispatcher</c>'s validate+size, the
/// <c>RiskManager.Validate/ValidateOrder/ValidateBudgetEntry/CalculateLotSize</c> family, and the now-deleted
/// <c>RiskGate.ProjectWorstCase</c>.
///
/// PURE: no I/O, no wall-clock, no Guid.NewGuid. All time-varying inputs come from
/// <paramref name="state"/> (equity/balance/drawdown/protection/governor/positions) or off the
/// evaluator-supplied <see cref="OrderProposed"/> (slPips, pipValuePerLot). Everything run-constant
/// (constraints, profile, sizing policy, symbol info) is passed in. ⇒ deterministic + replayable.
///
/// Bugs fixed here (cannot recur once this is the only gate):
///   C3/H1 — max-DD floor via <see cref="DrawdownState.GetMaxDrawdownFloor"/> (Trailing→PeakEquity,
///           Fixed→InitialAccountBalance). No more equity.Equity/equity.Balance reimplementation.
///   H3    — daily floor honors <see cref="ConstraintSet.DailyDdBase"/>.
///   H2    — weekly + monthly DD enforced.
///   NEW-3/C14 — SL distance validated; MaxSlPips &lt;= 0 means "no limit".
///   M7    — worst-case candidate loss includes round-trip commission.
///   H5/H6 — sizing via <see cref="KernelSizing"/> (AntiMartingale real; scale applied to fixed methods).
/// </summary>
public static class PreTradeGate
{
    public readonly record struct GateResult(bool Accepted, decimal Lots, decimal RiskAmount, string? RejectReason)
    {
        public static GateResult Reject(string reason) => new(false, 0m, 0m, reason);
        public static GateResult Accept(decimal lots, decimal riskAmount) => new(true, lots, riskAmount, null);
    }

    public static GateResult Evaluate(
        EngineState state,
        OrderProposed p,
        ConstraintSet c,
        RiskProfile profile,
        SizingPolicyOptions sizing,
        SymbolInfo symbol,
        IReadOnlyList<ProjectedPosition> openPositions,
        ExternalVerdicts external = default)
    {
        var equity = state.Account.Equity;

        // 1. Protection / governor — no new trades while suspended.
        if (state.Protection.InProtectionMode)
        {
            return GateResult.Reject("PROTECTION_MODE_ACTIVE");
        }

        // B1: governor toggle — skip governor check when disabled. Honors BOTH the external (legacy
        // ITradingGovernor) verdict the caller supplies AND the kernel GovernorState (the latter is the
        // end-state once TradingGovernorService is retired in AF6).
        if (c.GovernorEnabled)
        {
            if (external.GovernorBlockReason is { } gr)
            {
                return GateResult.Reject($"GOVERNOR:{gr}");
            }
            var g = state.Governor;
            if (g.State is GovernorTradingState.HardStop or GovernorTradingState.SoftStop
                    or GovernorTradingState.CoolingOff or GovernorTradingState.ProfitLocked
                || g.CoolingOffBarsRemaining > 0 || g.ProfitLockedToday)
            {
                return GateResult.Reject($"GOVERNOR:{g.State}");
            }
        }

        if (equity <= 0)
        {
            return GateResult.Reject("NO_EQUITY");
        }

        // 2. SL distance validity (NEW-3/C14). MaxSlPips <= 0 means "no limit".
        if (profile.MaxSlPips > 0 && p.SlPips > (decimal)profile.MaxSlPips)
        {
            return GateResult.Reject($"SL_TOO_WIDE:{p.SlPips:F1}>{profile.MaxSlPips:F1}");
        }

        // 3. Position-count limits.
        var openCount = state.Positions.Count;
        if (openCount >= profile.MaxConcurrentPositions)
        {
            return GateResult.Reject($"MAX_POSITIONS:{openCount}>={profile.MaxConcurrentPositions}");
        }

        var openForStrategy = state.Positions.Values.Count(x => x.StrategyId == p.StrategyId);
        if (openForStrategy >= profile.MaxConcurrentPositions)
        {
            return GateResult.Reject($"STRATEGY_MAX_POSITIONS:{p.StrategyId}");
        }

        // 4. Exposure (notional new risk vs equity).
        var totalOpenRisk = SumWorstCase(openPositions);
        var newPositionRiskNotional = equity * c.RiskPerTrade;
        if ((totalOpenRisk + newPositionRiskNotional) / equity > c.MaxExposure)
        {
            return GateResult.Reject("MAX_EXPOSURE");
        }

        // 4b. External (impure) gate verdicts — news window / weekend close / prop-firm compliance.
        // The caller already folded in the rule-set conditions (AllowTradesDuringNews/AllowWeekendHolding),
        // so a set flag here means "block". Mirrors the tail of RiskManager.Validate.
        if (external.NewsActive)
        {
            return GateResult.Reject("NEWS_WINDOW");
        }
        if (external.WeekendRestricted)
        {
            return GateResult.Reject("WEEKEND_RESTRICTION");
        }
        if (external.ComplianceBlockReason is { } complianceReason)
        {
            return GateResult.Reject($"COMPLIANCE_BLOCK:{complianceReason}");
        }

        // 5. Sizing (H5/H6).
        var drawdownScale = (decimal)KernelSizing.ComputeScaleFactor(
            state.Drawdown.CurrentMaxDrawdown, c.MaxTotalLoss, profile.DrawdownScaleThreshold, profile.DrawdownScaleFloor);
        var lots = KernelSizing.Calculate(
            equity, profile, p.SlPips, p.PipValuePerLot, drawdownScale, symbol.MaxLots, symbol.MinLots, symbol.LotStep);
        if (lots <= 0)
        {
            return GateResult.Reject("ZERO_LOTS");
        }

        // 6. Worst-case simultaneous-stop projection (C3/H1/H3/M7).
        // iter-strategy-system P5: gated behind toggle flags so per-run overrides can disable.
        if (c.DailyDdEnabled || c.MaxDdEnabled)
        {
            var candidateLoss = CandidateWorstCase(p, lots, symbol);
            var projectedEquity = equity - (candidateLoss + totalOpenRisk);

            if (c.DailyDdEnabled)
            {
                var dailyBaseEquity = c.DailyDdBase == DailyDdBase.DailyStart
                    ? state.Drawdown.DailyStartEquity
                    : state.Drawdown.InitialAccountBalance;
                var dailyFloor = dailyBaseEquity * (1m - c.MaxDailyLoss);
                if (projectedEquity < dailyFloor)
                {
                    return GateResult.Reject("WorstCaseDDWouldBreachDaily");
                }
            }

            if (c.MaxDdEnabled)
            {
                var maxFloor = state.Drawdown.GetMaxDrawdownFloor(c.MaxTotalLoss);
                if (projectedEquity < maxFloor)
                {
                    return GateResult.Reject("WorstCaseDDWouldBreachOverall");
                }
            }
        }

        // 7. Weekly / monthly DD (H2). B1: gated behind toggle flags.
        if (c.WeeklyDdEnabled && c.MaxWeeklyLoss > 0 && state.Drawdown.CurrentWeeklyDrawdown >= c.MaxWeeklyLoss)
        {
            return GateResult.Reject("WEEKLY_DD_LIMIT");
        }
        if (c.MonthlyDdEnabled && c.MaxMonthlyLoss > 0 && state.Drawdown.CurrentMonthlyDrawdown >= c.MaxMonthlyLoss)
        {
            return GateResult.Reject("MONTHLY_DD_LIMIT");
        }

        // 8. Budget validation with halving downsizing (port of RiskManager.ValidateBudgetEntry + loop).
        var riskAmount = p.SlPips * p.PipValuePerLot * lots;
        var perTradeRiskAmount = equity * c.RiskPerTrade;
        if (!BudgetOk(state, c, sizing, totalOpenRisk, riskAmount, perTradeRiskAmount))
        {
            while (lots > symbol.MinLots)
            {
                lots = Math.Max(lots * 0.5m, symbol.MinLots);
                lots = symbol.LotStep > 0 ? Math.Floor(lots / symbol.LotStep) * symbol.LotStep : lots;
                if (lots < symbol.MinLots)
                {
                    break;
                }
                riskAmount = p.SlPips * p.PipValuePerLot * lots;
                if (BudgetOk(state, c, sizing, totalOpenRisk, riskAmount, perTradeRiskAmount))
                {
                    break;
                }
            }
            if (lots < symbol.MinLots || !BudgetOk(state, c, sizing, totalOpenRisk, riskAmount, perTradeRiskAmount))
            {
                return GateResult.Reject($"BudgetBlocked:lots={lots:F4}");
            }
        }

        return GateResult.Accept(lots, riskAmount);
    }

    private static decimal SumWorstCase(IReadOnlyList<ProjectedPosition> open)
    {
        var sum = 0m;
        for (var i = 0; i < open.Count; i++)
        {
            sum += open[i].SlPips * open[i].PipValuePerLot * open[i].Lots;
        }
        return sum;
    }

    // M7: include round-trip commission in the candidate worst case. Swap is night-count dependent and
    // unknown at entry — TODO(deepseek): add an estimate if a max-hold is configured.
    private static decimal CandidateWorstCase(OrderProposed p, decimal lots, SymbolInfo symbol)
    {
        var slLoss = p.SlPips * p.PipValuePerLot * lots;
        var commission = symbol.CommissionPerLotPerSide * lots * 2m;
        return slLoss + commission;
    }

    private static bool BudgetOk(
        EngineState state, ConstraintSet c, SizingPolicyOptions sizing,
        decimal totalOpenRisk, decimal newRiskAmount, decimal perTradeRiskAmount)
    {
        var dailyDdBase = c.DailyDdBase == DailyDdBase.DailyStart
            ? state.Drawdown.DailyStartEquity
            : state.Drawdown.InitialAccountBalance;
        if (dailyDdBase <= 0)
        {
            return false;
        }

        var dailyDdUsedFraction = c.MaxDailyLoss > 0
            ? state.Drawdown.CurrentDailyDrawdown / c.MaxDailyLoss
            : 1m;
        var remainingDailyBudget = (1m - Math.Min(dailyDdUsedFraction, 1m)) * c.MaxDailyLoss * dailyDdBase;
        var budgetCap = remainingDailyBudget * (decimal)sizing.BudgetUseFraction;
        if (totalOpenRisk + newRiskAmount > budgetCap)
        {
            return false;
        }

        if (perTradeRiskAmount > 0 && sizing.MaxPortfolioHeatRiskMultiples > 0)
        {
            var heatCap = perTradeRiskAmount * (decimal)sizing.MaxPortfolioHeatRiskMultiples;
            if (totalOpenRisk + newRiskAmount > heatCap)
            {
                return false;
            }
        }

        return true;
    }
}
