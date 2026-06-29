using TradingEngine.Engine;
using TradingEngine.Host;

namespace TradingEngine.Tests.Simulation.EngineTruth;

/// <summary>
/// O2 (§1.6) lock — every equity/account snapshot must be stamped with SIM-TIME (the bar's
/// OpenTimeUtc), never wall-clock (DateTime.UtcNow), so the equity chart + time scrubber can be
/// built from a strictly sim-time-ordered series. The kernel maps state→snapshot in
/// <see cref="KernelEquitySnapshot.From"/>; this proves it carries sim-time + state values faithfully.
/// </summary>
[Trait("Category", "EngineTruth")]
[Trait("Speed", "Fast")]
public sealed class EquitySimTimeTests
{
    private static EngineState StateWith(AccountView account, DrawdownState dd) => new(
        new Dictionary<Guid, PositionState>(),
        new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
        dd, 0, ProtectionState.None, account);

    [Fact]
    public void KernelEquitySnapshot_StampsSimTime_NotWallClock()
    {
        var simTime = new DateTime(2024, 1, 15, 14, 0, 0, DateTimeKind.Utc);
        var state = StateWith(
            new AccountView(10_000m, 10_000m, 0m),
            DrawdownReducer.CreateInitial(10_000m, "Fixed"));

        var snapshot = KernelEquitySnapshot.From(state, simTime, "test-run");

        snapshot.SimTimeUtc.Should().Be(simTime,
            "equity snapshots must use sim-time (bar OpenTimeUtc), never DateTime.UtcNow");
        // The sim-time must NOT be anywhere near 'now' (proving no wall-clock leak).
        snapshot.SimTimeUtc.Should().BeBefore(DateTime.UtcNow.AddYears(-1));
    }

    [Fact]
    public void KernelEquitySnapshot_CarriesEngineStateFaithfully()
    {
        var simTime = new DateTime(2024, 1, 15, 14, 0, 0, DateTimeKind.Utc);
        var dd = new DrawdownState(
            InitialAccountBalance: 10_000m,
            PeakEquity: 10_000m,
            DailyStartEquity: 9_800m,
            WeeklyStartEquity: 10_000m,
            MonthlyStartEquity: 10_000m,
            CurrentDailyDrawdown: 0.05m,
            CurrentMaxDrawdown: 0.07m,
            CurrentWeeklyDrawdown: 0.01m,
            CurrentMonthlyDrawdown: 0.03m,
            DrawdownVelocity: 0m,
            DrawdownType: "Fixed");
        var state = StateWith(new AccountView(10_000m, 9_500m, -500m), dd);

        var snapshot = KernelEquitySnapshot.From(state, simTime, "test-run");

        using (new FluentAssertions.Execution.AssertionScope())
        {
            snapshot.SimTimeUtc.Should().Be(simTime);
            snapshot.Balance.Should().Be(10_000m);
            snapshot.Equity.Should().Be(9_500m);
            snapshot.FloatingPnL.Should().Be(-500m);
            snapshot.PeakEquity.Should().Be(10_000m);
            snapshot.DailyStartEquity.Should().Be(9_800m);
            snapshot.DailyDrawdown.Should().Be(0.05m);
            snapshot.MaxDrawdown.Should().Be(0.07m);
        }
    }
}
