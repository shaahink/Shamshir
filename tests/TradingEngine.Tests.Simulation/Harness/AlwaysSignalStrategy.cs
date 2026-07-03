namespace TradingEngine.Tests.Simulation.Harness;

public sealed class AlwaysSignalStrategy : IStrategy
{
    private int _barCount;
    private bool _positionOpen;

    public string Id => "always-signal";
    public string DisplayName => "Always Signal (test only)";
    public Timeframe EntryTimeframe => Timeframe.H1;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1];
    public int RequiredBarCount => 1;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators => [];
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public IStrategyConfig Config => new AlwaysSignalConfig();
    public StrategyStats Stats => new(0, 0, 0, 0);

    public TradeIntent? Evaluate(MarketContext context)
    {
        _barCount++;
        if (_barCount <= 5 || _positionOpen) return null;

        var close = context.LatestTick.Bid;

        _positionOpen = true;
        return new TradeIntent(
            context.Symbol,
            TradeDirection.Long,
            OrderType.Market,
            null,
            new Price(close - 0.0050m),
            new Price(close + 0.0100m),
            Id, "standard", "always-fire", context.EngineTimeUtc);
    }

    public void OnTradeResult(TradeResult result)
    {
        _positionOpen = false;
    }

    public void Reset() { _barCount = 0; _positionOpen = false; }
}

internal sealed record AlwaysSignalConfig : IStrategyConfig
{
    public string Id => "always-signal";
    public string DisplayName => "Always Signal";
    public bool Enabled => true;
    public IReadOnlyList<string> Symbols => ["EURUSD"];
    public string RiskProfileId => "standard";
    public Timeframe Timeframe => Timeframe.H1;
    public RegimeFilterOptions RegimeFilter => new();
    public OrderEntryOptions OrderEntry => new() { Method = OrderEntryMethod.Market };
    public PositionManagementOptions PositionManagement => new();
    public ReentryOptions Reentry => new();
}
