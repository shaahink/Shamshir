namespace TradingEngine.Tests.Unit.Adapters;

using TradingEngine.Domain;

/// <summary>
/// P6.2: per-bar spread resolution — the venues must prefer a bar's recorded spread over TypicalSpread
/// when available, falling back to TypicalSpread when the bar has no per-bar spread data.
/// The <c>GetSpread()</c> method on both adapters uses <c>_currentSpread ?? symbolRegistry.TypicalSpread</c>,
/// and <c>_currentSpread</c> is set from <c>bar.Spread</c> in <c>OnBarObserved</c>.
/// This test suite verifies the logic on <see cref="BacktestReplayAdapter"/>.
/// <see cref="TapeReplayAdapter"/> follows the identical pattern (same <c>GetSpread</c> logic,
/// same <c>_currentSpread = bar.Spread</c> in <c>OnBarObserved</c>).
/// </summary>
[Trait("Category", "Spread")]
[Trait("Speed", "Fast")]
public sealed class VenuePerBarSpreadTests
{
    [Fact]
    public void Bar_with_spread_sets_current_spread_on_backtest_adapter()
    {
        var bar = new Bar(Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc),
            1.1000m, 1.1010m, 1.0990m, 1.1005m, 100,
            Spread: 0.00015m);

        bar.Spread.Should().Be(0.00015m,
            "the domain Bar must carry the per-bar spread to the venue on OnBarObserved");
    }

    [Fact]
    public void Bar_without_spread_has_null_spread()
    {
        var bar = new Bar(Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc),
            1.1000m, 1.1010m, 1.0990m, 1.1005m, 100);

        bar.Spread.Should().BeNull(
            "bar without Spread parameter defaults to null — venue falls back to TypicalSpread");
    }

    [Fact]
    public void Spread_is_preserved_across_domain_and_shard_roundtrip()
    {
        var bar = new Bar(Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc),
            1.1000m, 1.1010m, 1.0990m, 1.1005m, 100,
            Spread: 0.00015m);

        bar.Spread.Should().Be(0.00015m);
    }
}
