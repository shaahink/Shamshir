using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingEngine.Engine;
using TradingEngine.Services;

namespace TradingEngine.Host;

/// <summary>
/// The kernel order gate (iter-35 AF2 cutover). Replaces <see cref="OrderDispatcher"/>: the pre-trade
/// decision + sizing now run through the pure kernel (<see cref="PreTradeGate"/> + <c>KernelSizing</c>),
/// where the C3/H1/H2/H3/M7/H5/H6/C14 risk bugs are already fixed and can't recur.
///
/// This impure adapter does only what the pure kernel cannot: it computes the service-dependent verdicts
/// (news window / weekend close / prop-firm compliance / legacy governor) exactly as
/// <c>RiskManager.Validate</c> did, builds the <see cref="EngineState"/> snapshot from the imperative
/// <see cref="IRiskManager"/> state, then delegates the whole decision to <see cref="PreTradeGate.Evaluate"/>.
/// Its journal records are byte-identical to <see cref="OrderDispatcher"/>'s so the golden snapshot is
/// preserved; the golden-harness equivalence test pins the behaviour.
/// </summary>
public sealed class KernelOrderGate(
    IRiskManager riskManager,
    IRiskProfileResolver riskProfileResolver,
    ISymbolInfoRegistry symbolRegistry,
    Func<string, string, decimal> crossRateProvider,
    IDecisionJournal decisionJournal,
    EngineRunContext runContext,
    SizingPolicyOptions sizingPolicy,
    INewsFilter newsFilter,
    SessionFilter sessionFilter,
    IEngineClock clock,
    ILogger<KernelOrderGate> logger,
    ITradingGovernor? governor = null) : IOrderGate
{
    public async Task<OrderContext?> DispatchAsync(
        TradeIntent intent, EquitySnapshot equity, decimal currentMid,
        IBrokerAdapter broker, IReadOnlyList<ProjectedPosition> openPositions, CancellationToken ct)
    {
        var profile = riskProfileResolver.Resolve(intent.RiskProfileId);
        var symbolInfo = symbolRegistry.Get(intent.Symbol);
        var entryPrice = intent.LimitPrice ?? new Price(currentMid);
        var slPips = (decimal)PipCalculator.Distance(entryPrice, intent.StopLoss, symbolInfo).Value;
        var pipValuePerLot = PipCalculator.PipValuePerLot(symbolInfo, entryPrice.Value, crossRateProvider);

        var constraints = riskManager.Constraints ?? ConstraintSet.Resolve(profile, riskManager.ActiveRuleSet ?? DefaultRuleSet());
        var state = BuildState(intent, equity, openPositions);
        var external = ComputeVerdicts(intent, equity, profile);

        var proposal = new OrderProposed(
            Guid.Empty, intent.Symbol, intent.Direction, intent.OrderType,
            intent.LimitPrice, intent.StopLoss, intent.TakeProfit, intent.StrategyId,
            entryPrice.Value, slPips, pipValuePerLot, equity.TimestampUtc);

        var gate = PreTradeGate.Evaluate(
            state, proposal, constraints, profile, sizingPolicy, symbolInfo, openPositions, external);

        if (!gate.Accepted)
        {
            logger.LogWarning("Blocked. Strategy={Strategy} Symbol={Symbol} Reason={Reason}",
                intent.StrategyId, intent.Symbol, gate.RejectReason);
            decisionJournal.Record(new DecisionRecord(
                runContext.RunId, equity.TimestampUtc, 0, intent.Symbol.Value, intent.StrategyId,
                null, "OrderRejected", gate.RejectReason, null,
                $"Risk validation failed: {gate.RejectReason}",
                JsonSerializer.Serialize(new { reason = gate.RejectReason })));
            return null;
        }

        var finalLots = gate.Lots;
        var riskAmount = gate.RiskAmount;

        logger.LogInformation("Order: Strategy={Strategy} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry:F5} SL={SL:F5}",
            intent.StrategyId, intent.Symbol, intent.Direction, finalLots, entryPrice.Value, intent.StopLoss.Value);

        var orderReq = new OrderRequest(intent, finalLots, intent.Symbol, intent.Direction, intent.OrderType, intent.LimitPrice);
        var orderId = await broker.SubmitOrderAsync(orderReq, ct);

        decisionJournal.Record(new DecisionRecord(
            runContext.RunId, equity.TimestampUtc, 0, intent.Symbol.Value, intent.StrategyId,
            null, "OrderSubmitted", null, null,
            $"Order accepted: lots={finalLots:F4} risk={riskAmount:F2}",
            JsonSerializer.Serialize(new { lots = finalLots, riskAmount, entryPrice = entryPrice.Value, sl = intent.StopLoss.Value })));

        return new OrderContext(orderId, finalLots, riskAmount, profile);
    }

    private EngineState BuildState(TradeIntent intent, EquitySnapshot equity, IReadOnlyList<ProjectedPosition> openPositions)
    {
        var dd = riskManager.Drawdown;
        var cs = riskManager.CurrentState;
        var protection = cs.InProtectionMode
            ? ProtectionState.None.Enter(ProtectionCause.MaxDrawdown, cs.ProtectionReason ?? "protection")
            : ProtectionState.None;

        // Only the COUNT matters to the gate: the per-strategy check uses the same MaxConcurrentPositions
        // limit as the total check, so it is dominated by it. Build that many placeholder positions, all
        // attributed to the proposal's strategy (⇒ per-strategy == total), with throwaway prices the gate
        // never reads.
        var positions = new Dictionary<Guid, PositionState>();
        foreach (var pp in openPositions)
        {
            var id = Guid.NewGuid();
            positions[id] = new PositionState(
                id, id, intent.Symbol, TradeDirection.Long, pp.Lots,
                new Price(0m), new Price(0m), null, clock.UtcNow, intent.StrategyId, PositionPhase.Open);
        }

        var governorState = new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial");
        var account = new AccountView(equity.Balance, equity.Equity, equity.FloatingPnL);
        return new EngineState(positions, governorState, dd, openPositions.Count, protection, account);
    }

    private ExternalVerdicts ComputeVerdicts(TradeIntent intent, EquitySnapshot equity, RiskProfile profile)
    {
        var ruleSet = riskManager.ActiveRuleSet;
        var newsActive = ruleSet?.AllowTradesDuringNews == false && newsFilter.IsNewsWindowActive(intent.Symbol, clock.UtcNow);
        var weekend = sessionFilter.IsWeekend(clock.UtcNow) && ruleSet?.AllowWeekendHolding == false;
        var compliance = riskManager.CheckComplianceBlock(intent, profile);

        string? governorReason = null;
        if (governor is not null)
        {
            var dayStart = riskManager.Drawdown.DailyStartEquity;
            var dayPnLFraction = dayStart > 0 ? (equity.Equity - dayStart) / dayStart : 0m;
            var ctx = new GovernorContext(dayPnLFraction, dayStart, equity.Equity, 0, ruleSet ?? DefaultRuleSet());
            var decision = governor.Evaluate(ctx);
            if (!decision.AllowNewTrades)
            {
                governorReason = decision.Reason;
            }
        }

        return new ExternalVerdicts(newsActive, weekend, compliance, governorReason);
    }

    private static PropFirmRuleSet DefaultRuleSet() => new(
        "none", "None", "Fixed", 0.05, 0.10, 0.10, 0,
        "BalancePlusFloating", "22:00:00", "UTC", false, "High", 0, 0,
        false, "21:00:00", "20:00:00", "NextTradingDay", false);
}
