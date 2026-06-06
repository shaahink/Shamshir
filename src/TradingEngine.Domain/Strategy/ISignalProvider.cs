namespace TradingEngine.Domain;

public interface ISignalProvider
{
    string SignalId { get; }
    IReadOnlyList<IndicatorRequest> RequiredIndicators { get; }
    int RequiredBarCount { get; }
    (TradeDirection Direction, string Reason)? Evaluate(MarketContext context);
}
