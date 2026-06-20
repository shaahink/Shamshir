namespace TradingEngine.Domain;

public abstract record EngineEvent(DateTime OccurredAtUtc);

public record BarClosed(Symbol Symbol, Timeframe Timeframe, decimal Open, decimal High, decimal Low, decimal Close, DateTime BarOpenTimeUtc) : EngineEvent(BarOpenTimeUtc);

public record BarIngested(string RunId, Bar Bar) : EngineEvent(Bar.OpenTimeUtc);

public record TickReceived(Symbol Symbol, decimal Bid, decimal Ask, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

/// <summary>
/// A candidate order from the strategy/indicator evaluation layer, BEFORE the kernel's pre-trade gate
/// (iter-35 A2). The reducer runs <c>PreTradeGate</c> on this and either accepts → emits a
/// <see cref="SubmitOrder"/> effect + creates an Intended position (which the venue confirms via
/// <see cref="OrderFilled"/>), or rejects → emits a <see cref="RecordDecisionEvent"/> with the reason.
/// This is the seam that moves the gate (drawdown floors, exposure, weekly/monthly, SL validity,
/// sizing) INTO the kernel, replacing the scattered logic in OrderDispatcher + RiskManager. The
/// strategy + indicator evaluation that PRODUCES these proposals stays outside the kernel (a
/// deterministic evaluator stage, A5) — the kernel owns money/risk/position lifecycle only.
/// Note: lots are decided BY the gate (sizing), so they are not carried on the proposal.
/// </summary>
/// <param name="SlPips">Stop distance in pips, computed by the evaluator (market context lives there, not in the kernel).</param>
/// <param name="PipValuePerLot">Cross-rate-aware pip value per lot, computed by the evaluator. The gate needs both to size + project worst case purely.</param>
/// <param name="External">Impure gate verdicts (news/weekend/compliance/governor) the evaluator froze at sim-time so the pure kernel gate can apply them deterministically — no protection silently dropped (iter-36 K1). Default = nothing blocks.</param>
public record OrderProposed(Guid OrderId, Symbol Symbol, TradeDirection Direction, OrderType OrderType, Price? LimitPrice, Price StopLoss, Price? TakeProfit, string StrategyId, decimal SignalPriceMid, decimal SlPips, decimal PipValuePerLot, DateTime OccurredAtUtc, ExternalVerdicts External = default) : EngineEvent(OccurredAtUtc);

public record OrderSubmitted(Guid OrderId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price? LimitPrice, string StrategyId, DateTime OccurredAtUtc, Price StopLoss = default, Price? TakeProfit = null) : EngineEvent(OccurredAtUtc);

public record OrderFilled(Guid OrderId, Symbol Symbol, decimal FilledLots, Price FillPrice, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record OrderPartiallyFilled(Guid OrderId, Symbol Symbol, decimal FilledLots, Price FillPrice, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record OrderRejected(Guid OrderId, Symbol Symbol, string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

/// <summary>A resting order (e.g. an expired limit entry) was cancelled by the venue before it ever
/// filled. Distinct from <see cref="OrderRejected"/> (refused at submit) and <see cref="OrderFilled"/>
/// (a real fill) so the lifecycle never mistakes a cancellation for a zero-lot fill.</summary>
public record OrderCancelled(Guid OrderId, Symbol Symbol, string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record CloseRequested(Guid PositionId, string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

/// <summary>An account observation from the venue (was the imperative <c>AccountUpdate</c>). Carries the
/// full account so the kernel can fold an authoritative <see cref="AccountView"/> + run the breach
/// watchdog in the reducer/kernel instead of in AccountProcessor (iter-35 A2, fixes C5-class issues by
/// keeping equity truthful in one place).</summary>
public record EquityObserved(decimal Balance, decimal Equity, decimal FloatingPnL, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record DayRolled(DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record WeekRolled(DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record MonthRolled(DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);
