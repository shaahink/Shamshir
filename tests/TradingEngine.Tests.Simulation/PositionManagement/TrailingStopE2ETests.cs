using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.PositionManagement;

/// <summary>
/// End-to-end proof that breakeven/trailing is now actually wired into the per-bar loop (it used to be
/// fully implemented but never invoked). A single long is opened, price runs up, and the position's
/// stop must ratchet ABOVE entry — driven entirely through TradingLoop.UpdateTrailingStopsAsync.
/// </summary>
[Trait("Category", "Simulation")]
public sealed class TrailingStopE2ETests
{
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");

    [Fact]
    public async Task Rising_market_ratchets_the_stop_above_entry()
    {
        // Steadily rising bars (low = open = prior close, so a 6-pip trail never stops us out).
        var bars = new List<Bar>();
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var price = 1.10000m;
        for (var i = 0; i < 30; i++)
        {
            var open = price;
            var close = open + 0.0010m;
            bars.Add(new Bar(Eur, Timeframe.H1, t, open, close, open, close, 1000));
            price = close;
            t = t.AddHours(1);
        }

        await using var harness = await new EngineHarnessBuilder()
            .WithSymbol(Eur)
            .WithStrategy(new OneShotLongStrategy())
            .WithoutBreachWatchdog()
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        var open2 = harness.Tracker.OpenPositions.Values.SingleOrDefault(p => p.StrategyId == "trail-test");
        open2.Should().NotBeNull("the long should still be open (TP not hit, trail never crossed)");
        // Entry filled ~1.10100 with an initial stop at ~1.09900; after a 290-pip run the stop must be
        // well above entry — i.e. breakeven engaged AND trailing continued past it.
        open2!.CurrentStopLoss.Value.Should().BeGreaterThan(open2.EntryPrice.Value,
            "the stop must have trailed past breakeven as the market rose");
    }

    private sealed class OneShotLongStrategy : IStrategy
    {
        private bool _opened;
        public string Id => "trail-test";
        public string DisplayName => "Trail Test";
        public Timeframe EntryTimeframe => Timeframe.H1;
        public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1];
        public int RequiredBarCount => 1;
        public IReadOnlyList<IndicatorRequest> RequiredIndicators => [];
        public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
        public IStrategyConfig Config { get; } = new TrailConfig();
        public StrategyStats Stats => new(0, 0, 0, 0);

        public TradeIntent? Evaluate(MarketContext context)
        {
            if (_opened) return null;
            _opened = true;
            var px = context.LatestTick.Bid;
            return new TradeIntent(context.Symbol, TradeDirection.Long, OrderType.Market, null,
                new Price(px - 0.0020m), new Price(px + 0.0500m), Id, "standard", "open", context.EngineTimeUtc);
        }

        public void OnTradeResult(TradeResult result) { }
        public void Reset() => _opened = false;
    }

    private sealed record TrailConfig : IStrategyConfig
    {
        public string Id => "trail-test";
        public string DisplayName => "Trail Test";
        public bool Enabled => true;
        public IReadOnlyList<string> Symbols => ["EURUSD"];
        public string RiskProfileId => "standard";
        public Timeframe Timeframe => Timeframe.H1;
        public RegimeFilterOptions RegimeFilter => new();
        public OrderEntryOptions OrderEntry => new();
        public PositionManagementOptions PositionManagement => new()
        {
            Breakeven = new BreakevenOptions { Enabled = true, TriggerRMultiple = 1.0, OffsetPips = 1.0 },
            // iter-38 A1: trailing is now gated by its Enabled toggle (Method alone no longer activates it).
            // Custom keeps the stored 2.5 multiple (no auto-tune), so this E2E stays byte-identical.
            Trailing = new TrailingOptions { Enabled = true, Mode = AddOnMode.Custom, Method = "AtrMultiple", AtrMultiple = 2.5 },
        };
        public ReentryOptions Reentry => new();
    }
}
