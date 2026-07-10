using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Strategies.EmaAlignment;

namespace TradingEngine.Tests.Unit.StrategyTests;

/// <summary>
/// P2.3/D5 gate: ema-alignment was a STATE condition (fast&gt;slow AND price&gt;fast) that fires every bar of
/// any trend — the comment even said "crossover" while the code tested a condition. Rewritten to a real
/// edge: a crossover within the lookback window, then the FIRST pullback touch of the fast EMA since that
/// crossover. Fully derived from bars + MarketContext.IndicatorSeries (P2.1) — no private state needed.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class EmaAlignmentStrategyTests
{
    private static ISymbolInfoRegistry Registry()
    {
        var reg = new SymbolInfoRegistry();
        reg.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return reg;
    }

    private static EmaAlignmentStrategy MakeStrategy() =>
        new(new EmaAlignmentConfig("ema-alignment", "EMA Alignment", true, "standard", new()),
            Registry(), NullLogger<EmaAlignmentStrategy>.Instance);

    // Builds `padding` flat bars, then a 20-entry window: bars 0-9 bearish state (fast<slow), bar 10 is the
    // bullish cross (fast jumps above slow), bars 11-18 continue up with NO pullback touch (price well
    // above the fast EMA), bar 19 (the "current"/last bar) pulls back to touch the fast EMA and closes
    // back above it — the genuine first-touch entry.
    private static (List<Bar> Bars, List<double> Fast, List<double> Slow) BuildCrossoverThenPullbackFixture(int padding, bool includeEarlierTouch = false)
    {
        var bars = new List<Bar>();
        var fast = new List<double>();
        var slow = new List<double>();
        var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        void Add(decimal low, decimal high, decimal close, double f, double s)
        {
            bars.Add(new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, time, close, high, low, close, 100));
            fast.Add(f); slow.Add(s);
            time = time.AddHours(1);
        }

        for (var i = 0; i < padding; i++) Add(0.9000m, 0.9100m, 0.9050m, 0.90, 0.92); // neutral padding

        for (var i = 0; i < 10; i++) Add(0.9800m, 0.9900m, 0.9850m, 1.00, 1.02); // bearish state (fast<slow)

        Add(1.0250m, 1.0350m, 1.0300m, 1.03, 1.02); // bar 10: bullish cross (fast now > slow)

        for (var i = 0; i < 7; i++) // bars 11-17: uptrend continues, price stays clear of the fast EMA
        {
            var f = 1.04 + i * 0.01;
            Add((decimal)f + 0.04m, (decimal)f + 0.06m, (decimal)f + 0.05m, f, 1.02);
        }

        if (includeEarlierTouch)
        {
            // bar 18: an EARLIER pullback touch already happened — the later "current" touch is not the first.
            Add(1.108m, 1.112m, 1.115m, 1.11, 1.02);
        }
        else
        {
            Add(1.150m, 1.170m, 1.160m, 1.11, 1.02); // bar 18: still no touch
        }

        Add(1.100m, 1.130m, 1.125m, 1.12, 1.02); // bar 19 (current): Low(1.100) <= fast(1.12) <= High(1.130) => touch; close(1.125) > fast(1.12)

        return (bars, fast, slow);
    }

    private static MarketContext MakeContext(List<Bar> bars, List<double> fast, List<double> slow)
    {
        var values = new Dictionary<string, double> { ["EMA_20"] = fast[^1], ["EMA_50"] = slow[^1], ["ATR_14"] = 0.0010 };
        var series = new Dictionary<string, IReadOnlyList<double>> { ["EMA_20"] = fast, ["EMA_50"] = slow };
        var tick = new Tick(Symbol.Parse("EURUSD"), (decimal)bars[^1].Close, (decimal)bars[^1].Close, bars[^1].OpenTimeUtc);
        return new MarketContext(Symbol.Parse("EURUSD"), tick,
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            values, bars[^1].OpenTimeUtc, series);
    }

    [Fact]
    public void Evaluate_CrossoverThenFirstPullbackTouch_FiresLong()
    {
        var strategy = MakeStrategy();
        var (bars, fast, slow) = BuildCrossoverThenPullbackFixture(padding: 60);
        bars.Count.Should().BeGreaterThanOrEqualTo(strategy.RequiredBarCount);

        var intent = strategy.Evaluate(MakeContext(bars, fast, slow));

        intent.Should().NotBeNull("a bullish crossover happened within the lookback window and this bar is the first pullback touch of the fast EMA");
        intent!.Direction.Should().Be(TradeDirection.Long);
    }

    [Fact]
    public void Evaluate_SecondTouchAfterCrossover_DoesNotFire()
    {
        var strategy = MakeStrategy();
        var (bars, fast, slow) = BuildCrossoverThenPullbackFixture(padding: 60, includeEarlierTouch: true);

        strategy.Evaluate(MakeContext(bars, fast, slow)).Should().BeNull(
            "an earlier bar already touched the fast EMA since the crossover — this is a SECOND touch, not the first, and must not fire");
    }

    [Fact]
    public void Evaluate_NoCrossoverInWindow_ConditionAloneDoesNotFire()
    {
        // fast > slow for the ENTIRE window (no crossover event, just the old "condition" being true) —
        // must NOT fire, proving this is no longer a state-based condition strategy.
        var strategy = MakeStrategy();
        var bars = new List<Bar>();
        var fast = new List<double>();
        var slow = new List<double>();
        var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < strategy.RequiredBarCount + 5; i++)
        {
            bars.Add(new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, time, 1.05m, 1.20m, 1.00m, 1.10m, 100));
            fast.Add(1.10); // fast > slow throughout, never crosses within the window
            slow.Add(1.02);
            time = time.AddHours(1);
        }

        strategy.Evaluate(MakeContext(bars, fast, slow)).Should().BeNull(
            "fast>slow held for the whole window with no crossover event — the old condition-only logic would fire here, the edge rewrite must not");
    }

    [Fact]
    public void Evaluate_InsufficientBars_ReturnsNull()
    {
        var strategy = MakeStrategy();
        var bars = new List<Bar> { new(Symbol.Parse("EURUSD"), Timeframe.H1, DateTime.UtcNow, 1.1m, 1.1m, 1.1m, 1.1m, 100) };
        strategy.Evaluate(MakeContext(bars, [1.0], [1.0])).Should().BeNull();
    }
}
