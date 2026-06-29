namespace TradingEngine.Domain;

public interface IStrategy
{
    string Id { get; }
    string DisplayName { get; }
    IStrategyConfig Config { get; }
    Timeframe EntryTimeframe { get; }
    IReadOnlyList<Timeframe> RequiredTimeframes { get; }
    int RequiredBarCount { get; }
    IReadOnlyList<IndicatorRequest> RequiredIndicators { get; }
    IReadOnlyList<IPositionBehavior> PositionBehaviors { get; }
    StrategyStats Stats { get; }

    TradeIntent? Evaluate(MarketContext context);
    void OnTradeResult(TradeResult result);
    void Reset();
}

public record StrategyStats(
    int ConsecutiveWins,
    int ConsecutiveLosses,
    double WinRateLast20,
    double AvgRLast20);

