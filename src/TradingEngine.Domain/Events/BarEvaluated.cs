namespace TradingEngine.Domain;

public sealed record BarEvaluated(
    string RunId,
    Symbol Symbol,
    Timeframe Timeframe,
    DateTime BarOpenTimeUtc,
    string StrategyId,
    IReadOnlyDictionary<string, double> IndicatorValues,
    bool SignalFired,
    TradeDirection? SignalDirection,
    string Reason,
    DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);
