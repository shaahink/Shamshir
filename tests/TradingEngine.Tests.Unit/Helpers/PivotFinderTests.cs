using TradingEngine.Services.Helpers;

namespace TradingEngine.Tests.Unit.Helpers;

/// <summary>
/// P2.2 gate: PivotFinder is a pure fractal swing-high/low detector, built and tested standalone before
/// RsiDivergenceStrategy is rewritten to use it (per PLAN.md's own agent guidance — pin the pivot math with
/// hand-constructed fixtures before touching the strategy, since real market data can never pin an
/// assertion).
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class PivotFinderTests
{
    private static Bar B(int i, decimal low, decimal high) =>
        new(Symbol.Parse("EURUSD"), Timeframe.H1, new DateTime(2024, 1, 1).AddHours(i),
            (low + high) / 2, high, low, (low + high) / 2, 100);

    // A single V: lows descend to a floor at index 4, then rise. Highs kept flat/irrelevant.
    private static List<Bar> SingleV() =>
    [
        B(0, 10, 20), B(1, 9, 20), B(2, 8, 20), B(3, 7, 20),
        B(4, 6, 20), // the pivot low
        B(5, 7, 20), B(6, 8, 20), B(7, 9, 20), B(8, 10, 20),
    ];

    [Fact]
    public void FindSwingLows_SingleV_FindsExactlyOnePivotAtTheFloor()
    {
        var pivots = PivotFinder.FindSwingLows(SingleV(), strength: 2);

        pivots.Should().ContainSingle();
        pivots[0].Index.Should().Be(4);
        pivots[0].Price.Should().Be(6m);
    }

    [Fact]
    public void FindSwingLows_MonotonicSeries_FindsNoPivots()
    {
        var bars = Enumerable.Range(0, 10).Select(i => B(i, 10 - i, 20 - i)).ToList();
        PivotFinder.FindSwingLows(bars, strength: 2).Should().BeEmpty("a purely descending series has no local minimum");
    }

    [Fact]
    public void FindSwingLows_TiedAdjacentLows_AreNotPivots()
    {
        // Two bars share the lowest Low (8) — neither should be confirmed (strict inequality only).
        var bars = new List<Bar> { B(0, 10, 20), B(1, 9, 20), B(2, 8, 20), B(3, 8, 20), B(4, 9, 20), B(5, 10, 20) };
        PivotFinder.FindSwingLows(bars, strength: 1).Should().BeEmpty("a tied bottom is ambiguous and must not be reported as a pivot from either side");
    }

    [Fact]
    public void FindSwingLows_WShape_FindsBothPivots_InOrder()
    {
        // Two V's: first floor at index 4 (Low=6), second (lower) floor at index 10 (Low=3).
        var bars = new List<Bar>
        {
            B(0, 10, 20), B(1, 9, 20), B(2, 8, 20), B(3, 7, 20),
            B(4, 6, 20), // pivot 1
            B(5, 7, 20), B(6, 8, 20), B(7, 7, 20), B(8, 6, 20), B(9, 5, 20),
            B(10, 3, 20), // pivot 2 (lower low)
            B(11, 5, 20), B(12, 7, 20), B(13, 9, 20),
        };

        var pivots = PivotFinder.FindSwingLows(bars, strength: 2);

        pivots.Should().HaveCount(2);
        pivots[0].Index.Should().Be(4);
        pivots[0].Price.Should().Be(6m);
        pivots[1].Index.Should().Be(10);
        pivots[1].Price.Should().Be(3m, "the second floor is a genuine lower low");
    }

    [Fact]
    public void FindSwingHighs_SingleInvertedV_FindsExactlyOnePivotAtThePeak()
    {
        var bars = new List<Bar>
        {
            B(0, 0, 10), B(1, 0, 11), B(2, 0, 12), B(3, 0, 13),
            B(4, 0, 14), // the pivot high
            B(5, 0, 13), B(6, 0, 12), B(7, 0, 11), B(8, 0, 10),
        };

        var pivots = PivotFinder.FindSwingHighs(bars, strength: 2);

        pivots.Should().ContainSingle();
        pivots[0].Index.Should().Be(4);
        pivots[0].Price.Should().Be(14m);
    }

    [Fact]
    public void FindSwingLows_TooFewBarsForStrength_ReturnsEmpty_NotThrow()
    {
        var bars = new List<Bar> { B(0, 10, 20), B(1, 9, 20) };
        var act = () => PivotFinder.FindSwingLows(bars, strength: 5);
        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Fact]
    public void FindSwingLows_ZeroStrength_Throws()
    {
        var act = () => PivotFinder.FindSwingLows(SingleV(), strength: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FindSwingLows_MostRecentBarsWithinStrengthWindow_NeverReturnedAsPivot()
    {
        // The pivot detector is non-repainting: a bar within `strength` of the tape's end cannot yet be
        // confirmed (there aren't enough later bars to compare against), so it must never appear as a pivot
        // even if its Low happens to be a local minimum among what's visible so far.
        var bars = SingleV();
        bars.Add(B(9, 4, 20)); // a NEW lower low right at the tail — too recent to confirm with strength=2

        var pivots = PivotFinder.FindSwingLows(bars, strength: 2);

        pivots.Should().NotContain(p => p.Index == 9, "a pivot within `strength` bars of the end cannot be confirmed yet");
    }
}
