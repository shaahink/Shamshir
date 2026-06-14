namespace TradingEngine.Domain;

public abstract record EngineEvent(DateTime OccurredAtUtc);

public record BarClosed(Symbol Symbol, Timeframe Timeframe, decimal Open, decimal High, decimal Low, decimal Close, DateTime BarOpenTimeUtc) : EngineEvent(BarOpenTimeUtc);

public record TickReceived(Symbol Symbol, decimal Bid, decimal Ask, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record OrderSubmitted(Guid OrderId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price? LimitPrice, string StrategyId, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record OrderFilled(Guid OrderId, Symbol Symbol, decimal FilledLots, Price FillPrice, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record OrderPartiallyFilled(Guid OrderId, Symbol Symbol, decimal FilledLots, Price FillPrice, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record OrderRejected(Guid OrderId, Symbol Symbol, string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

public record CloseRequested(Guid PositionId, string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);
