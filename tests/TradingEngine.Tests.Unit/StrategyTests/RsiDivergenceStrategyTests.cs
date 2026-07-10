using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Strategies.RsiDivergence;

namespace TradingEngine.Tests.Unit.StrategyTests;

/// <summary>
/// P2.2 gate: RsiDivergenceStrategy was a tautology (`rsiAtLowest = lowestIdx >= 0 ? rsi : rsi` — always
/// the current RSI, so "divergence" was never actually tested). Rewritten to pivot-based real divergence
/// using PivotFinder + MarketContext.IndicatorSeries (P2.1). Hand-constructed fixtures per PLAN.md's own
/// guidance ("construct bars arithmetically... do not fish real data for a divergence").
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class RsiDivergenceStrategyTests
{
    private static ISymbolInfoRegistry Registry()
    {
        var reg = new SymbolInfoRegistry();
        reg.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return reg;
    }

    private static RsiDivergenceStrategy MakeStrategy() =>
        new(new RsiDivergenceConfig(), Registry(), NullLogger<RsiDivergenceStrategy>.Instance);

    // Builds: `padding` neutral flat bars, then a "W" shape (pivot1 Low=6 @ index `padding+4`,
    // pivot2 Low=3 @ index `padding+10`, a genuine lower low), then a confirmation bar breaking above
    // pivot2's High. RSI series is padded flat at 50, with RSI=25 at pivot1 and RSI=35 at pivot2 — a
    // real bullish divergence: price makes a LOWER low, RSI makes a HIGHER low.
    private static (List<Bar> Bars, List<double> Rsi) BuildBullishDivergenceFixture(int padding)
    {
        var bars = new List<Bar>();
        var rsi = new List<double>();
        var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Bar Mk(decimal low, decimal high, decimal? closeOverride = null)
        {
            var close = closeOverride ?? (low + high) / 2;
            var b = new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, time, close, high, low, close, 100);
            time = time.AddHours(1);
            return b;
        }

        for (var i = 0; i < padding; i++) { bars.Add(Mk(20, 30)); rsi.Add(50); } // neutral padding

        bars.Add(Mk(10, 15)); rsi.Add(50);
        bars.Add(Mk(9, 14)); rsi.Add(45);
        bars.Add(Mk(8, 13)); rsi.Add(40);
        bars.Add(Mk(7, 12)); rsi.Add(30);
        bars.Add(Mk(6, 11)); rsi.Add(25);  // pivot1 (Low=6, RSI=25)
        bars.Add(Mk(7, 12)); rsi.Add(30);
        bars.Add(Mk(8, 13)); rsi.Add(40);
        bars.Add(Mk(7, 12)); rsi.Add(38);
        bars.Add(Mk(6.5m, 11.5m)); rsi.Add(36);
        bars.Add(Mk(5.5m, 10.5m)); rsi.Add(34);
        bars.Add(Mk(3, 9)); rsi.Add(35);   // pivot2 (Low=3, lower low; RSI=35, HIGHER low => divergence)
        bars.Add(Mk(5, 10)); rsi.Add(40);
        bars.Add(Mk(7, 12)); rsi.Add(45);
        bars.Add(Mk(9, 14, closeOverride: 9.5m)); rsi.Add(48); // confirmation: close(9.5) > pivot2's High(9)

        return (bars, rsi);
    }

    private static MarketContext MakeContext(List<Bar> bars, List<double> rsi, double atr = 0.0010)
    {
        var values = new Dictionary<string, double> { ["RSI_14"] = rsi[^1], ["ATR_14"] = atr };
        var series = new Dictionary<string, IReadOnlyList<double>> { ["RSI_14"] = rsi };
        var tick = new Tick(Symbol.Parse("EURUSD"), (decimal)bars[^1].Close, (decimal)bars[^1].Close, bars[^1].OpenTimeUtc);
        return new MarketContext(Symbol.Parse("EURUSD"), tick,
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            values, bars[^1].OpenTimeUtc, series);
    }

    [Fact]
    public void Evaluate_BullishDivergence_FiresLong()
    {
        var strategy = MakeStrategy();
        var (bars, rsi) = BuildBullishDivergenceFixture(padding: 60);
        bars.Count.Should().BeGreaterThanOrEqualTo(strategy.RequiredBarCount, "the fixture must satisfy the strategy's own bar-count gate");

        var intent = strategy.Evaluate(MakeContext(bars, rsi));

        intent.Should().NotBeNull("price made a lower low while RSI made a higher low, then price confirmed by closing above the divergence pivot's high");
        intent!.Direction.Should().Be(TradeDirection.Long);
    }

    [Fact]
    public void Evaluate_NoDivergence_TrendingRsiAgreesWithPrice_ReturnsNull()
    {
        // Same price shape (lower low), but RSI ALSO makes a lower low (no divergence — momentum agrees
        // with price) — must not fire.
        var strategy = MakeStrategy();
        var (bars, rsi) = BuildBullishDivergenceFixture(padding: 60);
        // Overwrite RSI at the pivot2 position to be LOWER than pivot1 (no divergence): pivot1 index is
        // `padding+4`, pivot2 index is `padding+10` in the pre-padding local layout used above.
        var pivot1Index = 60 + 4;
        var pivot2Index = 60 + 10;
        var localRsi = new List<double>(rsi) { };
        localRsi[pivot2Index] = localRsi[pivot1Index] - 5; // RSI makes a LOWER low too — agrees with price

        var intent = strategy.Evaluate(MakeContext(bars, localRsi));

        intent.Should().BeNull("RSI made a lower low alongside price — that is confirmation, not divergence");
    }

    [Fact]
    public void Evaluate_OnlyOnePivot_ReturnsNull()
    {
        var strategy = MakeStrategy();
        var bars = new List<Bar>();
        var rsi = new List<double>();
        var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < strategy.RequiredBarCount + 5; i++)
        {
            bars.Add(new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, time, 1.1000m, 1.1010m, 1.0990m, 1.1000m, 100));
            rsi.Add(50);
            time = time.AddHours(1);
        }

        strategy.Evaluate(MakeContext(bars, rsi)).Should().BeNull("flat data has no swing pivots at all");
    }

    [Fact]
    public void Evaluate_InsufficientBars_ReturnsNull()
    {
        var strategy = MakeStrategy();
        var bars = new List<Bar> { new(Symbol.Parse("EURUSD"), Timeframe.H1, DateTime.UtcNow, 1.1m, 1.1m, 1.1m, 1.1m, 100) };
        strategy.Evaluate(MakeContext(bars, [50])).Should().BeNull();
    }
}
