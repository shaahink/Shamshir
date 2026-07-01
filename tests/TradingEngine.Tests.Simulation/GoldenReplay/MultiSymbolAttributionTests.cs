using TradingEngine.Host;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// iter-37 Phase D (K-GAP-6) — multi-symbol fill attribution. The venue now stamps the instrument on
/// every <see cref="ExecutionEvent"/>, and the kernel feedback bridge attributes the fill/close to THAT
/// symbol (the old code guessed the first open position's symbol / EURUSD on a multi-symbol run). The
/// determinism + duplicate-lineage halves of Phase D are already covered by
/// <c>JournalSourceOfTruthTests.Journal_Determinism_ByteIdenticalAcrossRuns_MultiDay</c> and
/// <c>DuplicateRunE2ETests</c>.
/// </summary>
[Trait("Category", "Journal")]
[Trait("Speed", "Fast")]
public sealed class MultiSymbolAttributionTests
{
    private static readonly DateTime T = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Feedback_PrefersVenueStampedSymbol_OverFallbackGuess()
    {
        var usdjpy = Symbol.Parse("USDJPY");
        var exec = new ExecutionEvent(Guid.NewGuid(), OrderState.Filled, new Price(150.25m), 0.1m, null, T)
        {
            Symbol = usdjpy,
        };

        // Mirror KernelBacktestLoop.PumpAsync: prefer the carried symbol over a (deliberately wrong) guess.
        var symbol = exec.Symbol ?? Symbol.Parse("EURUSD");
        var evt = KernelFeedback.FromExecution(exec, symbol) as OrderFilled;

        evt.Should().NotBeNull();
        evt!.Symbol.Should().Be(usdjpy, "the fill attributes to the venue-stamped symbol, not the first-position/EURUSD guess");
    }

    [Fact]
    public void Feedback_FallsBackToResolvedSymbol_WhenVenueOmitsIt()
    {
        var exec = new ExecutionEvent(Guid.NewGuid(), OrderState.Filled, new Price(1.10m), 0.1m, null, T);
        exec.Symbol.Should().BeNull("legacy/cTrader execs may not carry a symbol");

        var fallback = Symbol.Parse("EURUSD");
        var symbol = exec.Symbol ?? fallback;
        var evt = KernelFeedback.FromExecution(exec, symbol) as OrderFilled;

        evt!.Symbol.Should().Be(fallback, "with no carried symbol the kernel still resolves by id (no throw)");
    }
}
