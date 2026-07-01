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
        snap.GovernorState.Should().Be("Normal", "iter-38 W-A7: the governor band is sourced from EngineState");
        snap.GovernorReason.Should().Be("Initial");
        snap.DistanceToDailyLimit.Should().Be(0m, "no daily-loss limit supplied ⇒ honest unknown distance, not fabricated");
    }

    [Fact]
    public void From_MapsGovernorBandReasonAndDistanceToDailyLimit()
    {
        // 2% of the day's drawdown spent, against a 5% daily-loss limit ⇒ 3% room remaining.
        var drawdown = DrawdownReducer.Apply(DrawdownReducer.CreateInitial(10_000m), 9_800m);
        var state = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.SoftStop, 0, 0, -0.02m, 1.0m, false, "Daily loss soft stop"),
            drawdown,
            0,
            ProtectionState.None,
            new AccountView(10_000m, 9_800m, -200m));
        var simTime = new DateTime(2024, 1, 2, 3, 0, 0, DateTimeKind.Utc);

        var snap = KernelEquitySnapshot.From(state, simTime, "run-7", dailyLossLimitFraction: 0.05m);

        snap.GovernorState.Should().Be("SoftStop");
        snap.GovernorReason.Should().Be("Daily loss soft stop");
        snap.DistanceToDailyLimit.Should().Be(0.05m - snap.DailyDrawdown,
            "iter-38 W-A7: distance = daily-loss limit minus the day's drawdown fraction");
        snap.DistanceToDailyLimit.Should().BeGreaterThan(0m).And.BeLessThan(0.05m,
            "a partially-spent day shows a real, moving distance — not 0/blank");
    }
}
