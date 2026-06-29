using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Engine;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Configuration;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Tests.Simulation.GoldenReplay;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.EngineTruth;

/// <summary>
/// Phase 0 reproduction tests for the iter-redesign plan. Each test encodes a known defect
/// that must FAIL before the Phase 1 engine-truth fixes land, and PASS after.
/// </summary>
[Trait("Category", "EngineTruth")]
[Trait("Speed", "Fast")]
public sealed class EngineTruthReproTests
{
    // ════════════════════════════════════════════════════════════════
    // E2 — Entry-bar ordering race → illegal transitions (§1.2)
    // Expected: FAIL (golden journal contains IllegalTransition records)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoIllegalTransitions_OnGoldenRun()
    {
        var run = await KernelLoopHarness.RunGoldenAsync();

        var illegalCount = 0;
        foreach (var r in run.Records)
        {
            if (r.DecisionReason is not null && r.DecisionReason.Contains("Illegal"))
                illegalCount++;
            if (r.EffectsJson is not null && r.EffectsJson.Contains("IllegalTransition"))
                illegalCount++;
        }

        illegalCount.Should().Be(0,
            "the engine must never produce IllegalTransition records — every event must be a legal arm in the position FSM");
    }

    // ════════════════════════════════════════════════════════════════
    // E3 — Exit reason defaults to FORCE instead of SL/TP (§1.3)
    // Expected: FAIL (golden trades show FORCE instead of SL)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExitReasonReflectsSlOrTp()
    {
        var run = await KernelLoopHarness.RunGoldenAsync();

        run.ClosedTrades.Should().NotBeEmpty(
            "the golden run must produce closed trades to test exit reasons");

        foreach (var t in run.ClosedTrades)
        {
            t.Result.ExitReason.Should().NotBe("FORCE",
                $"trade {t.Result.PositionId}: exit reason must reflect the actual SL/TP hit, not FORCE");
        }
    }

    // ════════════════════════════════════════════════════════════════
    // E1 — Leaked open book latches the risk gate (§1.1)
    // Unit-level test on EngineReducer.HandleBarClosed to prove
    // terminal-phase positions are retained in state.Positions and counted
    // by the gate. A complementary full-loop test requires a venue that
    // models non-filling resting-limit orders; that harness will be added
    // when FakeVenue is extended in Phase 1.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void HandleBarClosed_SubmittedPosition_ProducesNoIllegalTransition()
    {
        var orderId = Guid.NewGuid();
        var symbol = Symbol.Parse("EURUSD");
        var stuckPosition = new PositionState(
            orderId, orderId, symbol, TradeDirection.Long,
            0.1m, new Price(1.1000m), new Price(1.0950m), new Price(1.1100m),
            DateTime.UtcNow, "test-strategy", PositionPhase.Submitted);

        var state = new EngineState(
            new Dictionary<Guid, PositionState> { [orderId] = stuckPosition },
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(10_000m, "Fixed"),
            1, ProtectionState.None, AccountView.Flat);

        var barClosed = new BarClosed(symbol, Timeframe.H1, 1.1000m, 1.1005m, 1.0995m, 1.1000m, DateTime.UtcNow);

        var decision = EngineReducer.Apply(state, barClosed);

        // E2 (P1.1/P1.2 defense-in-depth): HandleBarClosed must NOT blindly apply BarClosed to a
        // non-Open position. A Submitted position waiting for its fill must be skipped — never
        // routed through the FSM's illegal-transition default arm.
        // Expected to FAIL until HandleBarClosed skips non-Open/Reducing positions.
        var illegalEffect = decision.Effects.OfType<RecordDecisionEvent>()
            .FirstOrDefault(e => e.Decision.Event == "IllegalTransition");
        illegalEffect.Should().BeNull(
            "P1.2 fix required: a BarClosed on a Submitted position must be a no-op skip, not an IllegalTransition");
    }

    [Fact]
    public void HandleBarClosed_KeepsClosedPositionAfterBar()
    {
        var orderId = Guid.NewGuid();
        var symbol = Symbol.Parse("EURUSD");
        var closedPosition = new PositionState(
            orderId, orderId, symbol, TradeDirection.Long,
            0.1m, new Price(1.1000m), new Price(1.0950m), new Price(1.1100m),
            DateTime.UtcNow, "test-strategy", PositionPhase.Closed);

        var state = new EngineState(
            new Dictionary<Guid, PositionState> { [orderId] = closedPosition },
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(10_000m, "Fixed"),
            0, ProtectionState.None, AccountView.Flat);

        var barClosed = new BarClosed(symbol, Timeframe.H1, 1.1000m, 1.1005m, 1.0995m, 1.1000m, DateTime.UtcNow);

        var decision = EngineReducer.Apply(state, barClosed);

        // E1: Closed positions must be purged, not retained.
        decision.State.Positions.Should().NotContainKey(orderId,
            "P1.1 fix required: HandleBarClosed must purge terminal (Closed/Rejected/Cancelled) positions");
    }

    // ════════════════════════════════════════════════════════════════
    // C2 — Raw profile / guard toggles (§2.2)
    // After P2.2: with every limiter toggle OFF, the gate applies ZERO
    // exposure/budget/position-count limiters — a "raw" run is provably raw.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void PreTradeGate_RawProfile_AcceptsWithNoLimiterRejections()
    {
        var rawConstraints = new ConstraintSet(
            Id: "raw",
            MaxDailyLoss: 0m,
            MaxTotalLoss: 0m,
            MaxWeeklyLoss: 0m,
            MaxMonthlyLoss: 0m,
            ProfitTarget: 0m,
            DrawdownType: "Fixed",
            DailyDdBase: DailyDdBase.DailyStart,
            RiskPerTrade: 0.01m,
            MaxConcurrentPositions: 5,
            MaxExposure: 0.001m,  // deliberately tiny: would reject if ExposureEnabled were on
            AllowTradesDuringNews: true,
            AllowWeekendHolding: true,
            ForceCloseOnBreach: false,
            DailyDdEnabled: false,
            MaxDdEnabled: false,
            WeeklyDdEnabled: false,
            MonthlyDdEnabled: false,
            ProfitTargetEnabled: false,
            ForceCloseOnBreachEnabled: false,
            NewsFilterEnabled: false,
            WeekendFilterEnabled: false,
            GovernorEnabled: false,
            ExposureEnabled: false,
            BudgetEnabled: false,
            MaxPositionsEnabled: false);

        var profile = new RiskProfile(
            "raw", "Raw", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
            false, "raw", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

        var sizing = new SizingPolicyOptions
        {
            BudgetUseFraction = 0.25,
        };

        var symbol = new SymbolInfo(
            Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

        var proposal = new OrderProposed(
            Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long, TradingEngine.Domain.OrderType.Market,
            null, new Price(1.0950m), new Price(1.1100m), "test-strategy", 1.1000m, 50m, 10m,
            DateTime.UtcNow);

        var state = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(10_000m, "Fixed"),
            0, ProtectionState.None,
            new AccountView(10_000m, 10_000m, 0m));

        var openPositions = Array.Empty<ProjectedPosition>();

        var result = PreTradeGate.Evaluate(state, proposal, rawConstraints, profile, sizing, symbol, openPositions);

        // C2: with all toggles off, the gate accepts — no BudgetBlocked, no MAX_EXPOSURE, no MAX_POSITIONS.
        result.Accepted.Should().BeTrue(
            $"a raw run must apply zero limiters — got rejection '{result.RejectReason}'");
        result.Lots.Should().BeGreaterThan(0m, "an accepted proposal must be sized");
    }

    [Fact]
    public void PreTradeGate_StandardProfile_StillEnforcesLimiters()
    {
        // The inverse guard: the SAME proposal under standard (toggles ON) IS rejected — proving the
        // toggles, not some unrelated change, are what makes "raw" raw.
        var stdConstraints = new ConstraintSet(
            Id: "standard",
            MaxDailyLoss: 0m,
            MaxTotalLoss: 0m,
            MaxWeeklyLoss: 0m,
            MaxMonthlyLoss: 0m,
            ProfitTarget: 0m,
            DrawdownType: "Fixed",
            DailyDdBase: DailyDdBase.DailyStart,
            RiskPerTrade: 0.01m,
            MaxConcurrentPositions: 5,
            MaxExposure: 0.001m,
            AllowTradesDuringNews: true,
            AllowWeekendHolding: true,
            ForceCloseOnBreach: false);  // all toggles default to ON

        var profile = new RiskProfile(
            "standard", "Standard", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
            false, "standard", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

        var sizing = new SizingPolicyOptions { BudgetUseFraction = 0.25 };
        var symbol = new SymbolInfo(
            Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);
        var proposal = new OrderProposed(
            Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long, TradingEngine.Domain.OrderType.Market,
            null, new Price(1.0950m), new Price(1.1100m), "test-strategy", 1.1000m, 50m, 10m,
            DateTime.UtcNow);
        var state = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(10_000m, "Fixed"),
            0, ProtectionState.None,
            new AccountView(10_000m, 10_000m, 0m));

        var result = PreTradeGate.Evaluate(state, proposal, stdConstraints, profile, sizing, symbol, Array.Empty<ProjectedPosition>());

        result.Accepted.Should().BeFalse("standard toggles ON must enforce the exposure limiter");
        result.RejectReason.Should().Contain("MAX_EXPOSURE",
            "the explainable rejection must name the limiter that fired");
    }

    // ════════════════════════════════════════════════════════════════
    // P1.4 — Engine invariants: the live book never leaks (§1.1)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GoldenRun_FinalState_SatisfiesEngineInvariants()
    {
        var run = await KernelLoopHarness.RunGoldenAsync();

        var violations = EngineInvariants.Inspect(run.Final);
        violations.Should().BeEmpty(
            "after a full run the live book must contain only live positions — no leaked terminal positions");
    }

    [Fact]
    public void HandleBarClosed_OutputState_IsHealthy_AfterTerminalPurge()
    {
        var symbol = Symbol.Parse("EURUSD");

        // A mixed book: one Open (live), one Closed (terminal — must be purged), one Submitted (live, pending fill)
        var openId = Guid.NewGuid();
        var closedId = Guid.NewGuid();
        var submittedId = Guid.NewGuid();

        PositionState Make(Guid id, PositionPhase phase) => new(
            id, id, symbol, TradeDirection.Long, 0.1m,
            new Price(1.1000m), new Price(1.0950m), new Price(1.1100m),
            DateTime.UtcNow, "test", phase);

        var state = new EngineState(
            new Dictionary<Guid, PositionState>
            {
                [openId] = Make(openId, PositionPhase.Open),
                [closedId] = Make(closedId, PositionPhase.Closed),
                [submittedId] = Make(submittedId, PositionPhase.Submitted),
            },
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(10_000m, "Fixed"),
            3, ProtectionState.None, AccountView.Flat);

        var barClosed = new BarClosed(symbol, Timeframe.H1, 1.1000m, 1.1005m, 1.0995m, 1.1000m, DateTime.UtcNow);

        var decision = EngineReducer.Apply(state, barClosed);

        // Invariant: no terminal positions retained after the bar.
        EngineInvariants.IsHealthy(decision.State).Should().BeTrue(
            "HandleBarClosed must leave only live positions: " +
            string.Join("; ", EngineInvariants.Inspect(decision.State).Select(v => v.Detail)));

        decision.State.Positions.Should().ContainKey(openId, "the live Open position survives");
        decision.State.Positions.Should().NotContainKey(closedId, "the Closed position is purged");
        decision.State.Positions.Should().ContainKey(submittedId, "a pending Submitted position is still live");
    }

    // ════════════════════════════════════════════════════════════════
    // P2.1 — the "raw" preset is provably raw end-to-end (config → ConstraintSet)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void RawConfig_LoadsWithEveryLimiterToggleOff()
    {
        var solutionRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var config = new ConfigLoader(solutionRoot).LoadBase();

        var raw = config.PropFirms.FirstOrDefault(f => f.Id == "raw");
        raw.Should().NotBeNull("config/prop-firms/raw.json must load");

        var t = raw!.Toggles;
        // Every protection / limiter toggle must be OFF for the raw preset.
        using (new FluentAssertions.Execution.AssertionScope())
        {
            t.DailyDdEnabled.Should().BeFalse();
            t.MaxDdEnabled.Should().BeFalse();
            t.WeeklyDdEnabled.Should().BeFalse();
            t.MonthlyDdEnabled.Should().BeFalse();
            t.ProfitTargetEnabled.Should().BeFalse();
            t.ForceCloseOnBreachEnabled.Should().BeFalse();
            t.NewsFilterEnabled.Should().BeFalse();
            t.WeekendFilterEnabled.Should().BeFalse();
            t.GovernorEnabled.Should().BeFalse();
            t.ExposureEnabled.Should().BeFalse("P2.1: raw.json must disable the exposure limiter");
            t.BudgetEnabled.Should().BeFalse("P2.1: raw.json must disable the daily-budget/heat limiter");
            t.MaxPositionsEnabled.Should().BeFalse("P2.1: raw.json must disable the position-count limiter");
        }

        // And the resolved ConstraintSet the engine consumes carries the toggles through.
        var rawProfile = config.RiskProfiles.First(p => p.Id == "raw");
        var constraints = ConstraintSet.Resolve(rawProfile, raw);
        constraints.ExposureEnabled.Should().BeFalse();
        constraints.BudgetEnabled.Should().BeFalse();
        constraints.MaxPositionsEnabled.Should().BeFalse();
    }

}
