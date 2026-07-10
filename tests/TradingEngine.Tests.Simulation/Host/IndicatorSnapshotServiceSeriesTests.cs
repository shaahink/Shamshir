using TradingEngine.Host;
using TradingEngine.Infrastructure.Indicators;

namespace TradingEngine.Tests.Simulation.Host;

/// <summary>
/// P2.1 gate: IndicatorSnapshotService keeps a capped ring buffer (last 64 values, latest last) per sig
/// key, so strategies can read short lookback history instead of caching a private field that silently
/// desyncs if a bar is ever skipped or replayed out of order.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class IndicatorSnapshotServiceSeriesTests
{
    private sealed class AtrOnlyStrategy : IStrategy
    {
        public string Id => "atr-only";
        public string DisplayName => "ATR Only (test only)";
        public Timeframe EntryTimeframe => Timeframe.H1;
        public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1];
        public int RequiredBarCount => 1;
        public IReadOnlyList<IndicatorRequest> RequiredIndicators => [new("ATR_TEST", IndicatorType.Atr, 3, Timeframe: Timeframe.H1)];
        public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
        public IStrategyConfig Config => throw new NotSupportedException();
        public StrategyStats Stats => new(0, 0, 0, 0);
        public TradeIntent? Evaluate(MarketContext context) => null;
        public void OnTradeResult(TradeResult result) { }
        public void Reset() { }
    }

    private static List<Bar> MakeBars(Symbol symbol, int count)
    {
        var bars = new List<Bar>();
        var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        decimal price = 1.1000m;
        for (var i = 0; i < count; i++)
        {
            bars.Add(new Bar(symbol, Timeframe.H1, time, price, price + 0.0010m, price - 0.0010m, price + 0.0002m, 100));
            price += 0.0002m;
            time = time.AddHours(1);
        }
        return bars;
    }

    [Fact]
    public async Task GetSeries_AccumulatesAcrossRecomputes_LatestLast()
    {
        var symbol = Symbol.Parse("EURUSD");
        var strategy = new AtrOnlyStrategy();
        var snapshot = new IndicatorSnapshotService(new SkenderIndicatorService(), [strategy]);
        var sigKey = IndicatorCache.BuildKey(symbol, strategy.RequiredIndicators[0]);

        var bars = MakeBars(symbol, 20);
        var byTf = snapshot.Bars.GetOrAdd(symbol, _ => new());
        var list = byTf.GetOrAdd(Timeframe.H1, _ => new());

        var readingsAfterEachBar = new List<double>();
        foreach (var bar in bars)
        {
            list.Add(bar);
            await snapshot.RecomputeIndicatorsAsync(symbol, Timeframe.H1, CancellationToken.None);
            readingsAfterEachBar.Add(snapshot.IndicatorValues[sigKey]);
        }

        var series = snapshot.GetSeries(sigKey);
        series.Should().HaveCount(20);
        series[^1].Should().Be(readingsAfterEachBar[^1], "the series' last entry must be the most recent reading");
        series[0].Should().Be(readingsAfterEachBar[0], "the series' first entry must be the earliest reading (oldest first)");
    }

    [Fact]
    public async Task GetSeries_CapsAt64Entries()
    {
        var symbol = Symbol.Parse("EURUSD");
        var strategy = new AtrOnlyStrategy();
        var snapshot = new IndicatorSnapshotService(new SkenderIndicatorService(), [strategy]);
        var sigKey = IndicatorCache.BuildKey(symbol, strategy.RequiredIndicators[0]);

        var bars = MakeBars(symbol, 70);
        var byTf = snapshot.Bars.GetOrAdd(symbol, _ => new());
        var list = byTf.GetOrAdd(Timeframe.H1, _ => new());

        foreach (var bar in bars)
        {
            list.Add(bar);
            await snapshot.RecomputeIndicatorsAsync(symbol, Timeframe.H1, CancellationToken.None);
        }

        snapshot.GetSeries(sigKey).Should().HaveCount(64,
            "the ring buffer must cap at 64 entries even after 70 recomputes");
    }

    [Fact]
    public void GetSeries_UnknownKey_ReturnsEmpty()
    {
        var snapshot = new IndicatorSnapshotService(new SkenderIndicatorService(), []);
        snapshot.GetSeries("NEVER_COMPUTED").Should().BeEmpty();
    }

    [Fact]
    public async Task BuildStrategyIndicatorSeries_MirrorsValuesShape_KeyedByRequestKey()
    {
        var symbol = Symbol.Parse("EURUSD");
        var strategy = new AtrOnlyStrategy();
        var snapshot = new IndicatorSnapshotService(new SkenderIndicatorService(), [strategy]);

        var bars = MakeBars(symbol, 10);
        var byTf = snapshot.Bars.GetOrAdd(symbol, _ => new());
        var list = byTf.GetOrAdd(Timeframe.H1, _ => new());
        foreach (var bar in bars)
        {
            list.Add(bar);
            await snapshot.RecomputeIndicatorsAsync(symbol, Timeframe.H1, CancellationToken.None);
        }

        var sigKey = IndicatorCache.BuildKey(symbol, strategy.RequiredIndicators[0]);
        var series = snapshot.BuildStrategyIndicatorSeries(symbol, strategy);
        series.Should().ContainKey("ATR_TEST");
        series["ATR_TEST"].Should().HaveCount(10);
        series["ATR_TEST"][^1].Should().Be(snapshot.IndicatorValues[sigKey]);
    }
}
