using TradingEngine.Engine;
using TradingEngine.Host;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// iter-36 K4 gap-4: the kernel engine feeds the Monitor's equity/DD snapshot off the authoritative
/// <see cref="EngineState"/> (the imperative AccountProcessor used to). Pins that the state→snapshot
/// mapping is faithful so the Monitor isn't blank + shows the kernel's real drawdown.
/// </summary>
[Trait("Category", "KernelAcceptance")]
[Trait("Speed", "Fast")]
public sealed class KernelEquitySnapshotTests
{
    [Fact]
    public void From_MapsAuthoritativeStateOntoSnapshot()
    {
        var drawdown = DrawdownReducer.Apply(DrawdownReducer.CreateInitial(10_000m), 9_500m);
        var state = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            drawdown,
            0,
            ProtectionState.None,
            new AccountView(10_000m, 9_500m, -500m));
        var simTime = new DateTime(2024, 1, 2, 3, 0, 0, DateTimeKind.Utc);

        var snap = KernelEquitySnapshot.From(state, simTime, "run-42");

        snap.SimTimeUtc.Should().Be(simTime);
        snap.RunId.Should().Be("run-42");
        snap.Balance.Should().Be(10_000m);
        snap.Equity.Should().Be(9_500m);
        snap.FloatingPnL.Should().Be(-500m);
        snap.PeakEquity.Should().Be(10_000m);
        snap.MaxDrawdown.Should().Be(0.05m, "the snapshot carries the kernel's authoritative max drawdown");
        snap.OpenPositions.Should().Be(0);
    }
}
