namespace TradingEngine.Tests.Simulation.Harness;

public sealed class RepeatingSignalStrategy : IStrategy
{
    private int _barCount;

    public string Id => "repeating-signal";
    public string DisplayName => "Repeating Signal (test only)";
    public Timeframe EntryTimeframe => Timeframe.H1;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1];
    public int RequiredBarCount => 1;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators => [];
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public IStrategyConfig Config => new RepeatingSignalConfig();
    public StrategyStats Stats => new(0, 0, 0, 0);

    public TradeIntent? Evaluate(MarketContext context)
    {
        _barCount++;
        if (_barCount <= 5 || _barCount % 3 == 0) return null;

        var close = context.LatestTick.Bid;
        return new TradeIntent(
            context.Symbol,
            TradeDirection.Long,
            OrderType.Market,
            null,
            new Price(close - 0.0050m),
            new Price(close + 0.0100m),
            Id, "standard", "repeating-fire", context.EngineTimeUtc);
    }

    public void OnTradeResult(TradeResult result) { }
    public void Reset() { _barCount = 0; }
}

internal sealed record RepeatingSignalConfig : IStrategyConfig
{
    public string Id => "repeating-signal";
    public string DisplayName => "Repeating Signal";
    public bool Enabled => true;
    public IReadOnlyList<string> Symbols => ["EURUSD"];
    public string RiskProfileId => "standard";
    public Timeframe Timeframe => Timeframe.H1;
    public RegimeFilterOptions RegimeFilter => new();
    public OrderEntryOptions OrderEntry => new();
    public PositionManagementOptions PositionManagement => new();
    public ReentryOptions Reentry => new();
}
