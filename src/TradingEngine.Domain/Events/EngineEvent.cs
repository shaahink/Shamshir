namespace TradingEngine.Domain;

public abstract record EngineEvent(DateTime OccurredAtUtc);

public record BarClosed(Symbol Symbol, Timeframe Timeframe, decimal Open, decimal High, decimal Low, decimal Close, DateTime BarOpenTimeUtc) : EngineEvent(BarOpenTimeUtc);

public record BarIngested(string RunId, Bar Bar) : EngineEvent(Bar.OpenTimeUtc);

public record TickReceived(Symbol Symbol, decimal Bid, decimal Ask, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record OrderSubmitted(Guid OrderId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price? LimitPrice, string StrategyId, DateTime OccurredAtUtc, Price StopLoss = default, Price? TakeProfit = null) : EngineEvent(OccurredAtUtc);

public record OrderFilled(Guid OrderId, Symbol Symbol, decimal FilledLots, Price FillPrice, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record OrderPartiallyFilled(Guid OrderId, Symbol Symbol, decimal FilledLots, Price FillPrice, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record OrderRejected(Guid OrderId, Symbol Symbol, string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

/// <summary>A resting order (e.g. an expired limit entry) was cancelled by the venue before it ever
/// filled. Distinct from <see cref="OrderRejected"/> (refused at submit) and <see cref="OrderFilled"/>
/// (a real fill) so the lifecycle never mistakes a cancellation for a zero-lot fill.</summary>
public record OrderCancelled(Guid OrderId, Symbol Symbol, string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record CloseRequested(Guid PositionId, string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record EquityObserved(decimal Equity, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record DayRolled(DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record WeekRolled(DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record MonthRolled(DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);
