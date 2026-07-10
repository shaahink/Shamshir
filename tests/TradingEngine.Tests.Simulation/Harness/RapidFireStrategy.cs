namespace TradingEngine.Tests.Simulation.Harness;

/// <summary>
/// Fires every bar after warm-up (no skipping). Used to create overlapping
/// positions for the portfolio worst-case projection test.
/// </summary>
public sealed class RapidFireStrategy : IStrategy
{
    private int _barCount;

    public string Id => "rapid-fire";
    public string DisplayName => "Rapid Fire (test only)";
    public Timeframe EntryTimeframe => Config.EntryTimeframe;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [Config.EntryTimeframe];
    public int RequiredBarCount => 1;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators => [];
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public IStrategyConfig Config => new RapidFireConfig();
    public StrategyStats Stats => new(0, 0, 0, 0);

    public TradeIntent? Evaluate(MarketContext context)
    {
        _barCount++;
        if (_barCount <= 5) return null;

        var close = context.LatestTick.Bid;
        return new TradeIntent(
            context.Symbol,
            TradeDirection.Long,
            OrderType.Market,
            null,
            new Price(close - 0.0050m),
            new Price(close + 0.0100m),
            Id, "standard", "rapid-fire", context.EngineTimeUtc);
    }

    public void OnTradeResult(TradeResult result) { }
    public void Reset() { _barCount = 0; }
}

internal sealed record RapidFireConfig : IStrategyConfig
{
    public string Id => "rapid-fire";
    public string DisplayName => "Rapid Fire";
    public bool Enabled => true;
    public string RiskProfileId => "standard";
    public Timeframe EntryTimeframe { get; init; } = Timeframe.H1;
    public string? Symbol { get; init; }
    public IReadOnlyList<Timeframe> RequiredTimeframes { get; init; } = [];
    public RegimeFilterOptions RegimeFilter => new();
    public OrderEntryOptions OrderEntry => new() { Method = OrderEntryMethod.Market };
    public PositionManagementOptions PositionManagement => new();
    public ReentryOptions Reentry => new();
}
