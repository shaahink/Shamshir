namespace TradingEngine.Domain;

public interface IStrategy
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<Timeframe> RequiredTimeframes { get; }
    int RequiredBarCount { get; }

    TradeIntent? Evaluate(MarketContext context);
    void OnTradeResult(TradeResult result);
    void Reset();
}
