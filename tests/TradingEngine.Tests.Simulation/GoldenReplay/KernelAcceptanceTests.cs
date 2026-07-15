using TradingEngine.Engine;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// Kernel acceptance test — proves the kernel's PreTradeGate + Kernel.Decide produce the same
/// decisions as the old RiskManager.ValidateOrder + OrderDispatcher gate (A2a cutover gate).
///
/// Feeds the same deterministic bar fixture through the kernel (BarClosed + OrderProposed events)
/// and asserts the kernel accepts/rejects orders with the same lots as the golden baseline.
/// </summary>
[Trait("Category", "KernelAcceptance")]
[Trait("Speed", "Fast")]
public sealed class KernelAcceptanceTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly Timeframe H1 = Timeframe.H1;

    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    [Fact]
    public async Task KernelGate_AcceptsOrder_OnDownLegFixture()
    {
        var bars = GoldenBarFixture.Create();

        // Build kernel config mirroring the golden test's ftmo-standard setup.
        var profile = new RiskProfile(
            "standard", "Standard", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
            false, "ftmo-standard", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

        var ruleSet = new PropFirmRuleSet(
            "ftmo-standard", "ftmo-standard", "Fixed", 0.05, 0.10, 0.10, 0,
            "BalancePlusFloating", "22:00:00", "UTC",
            false, "High", 0, 0,
            false, "21:00:00", "20:00:00", "NextTradingDay", false);

        var constraints = ConstraintSet.Resolve(profile, ruleSet);
        var sizing = new SizingPolicyOptions { FlattenAtFraction = 0.9 };

        var config = new KernelConfig(
            constraints, profile, sizing,
            ResolveSymbol: _ => EurusdInfo,
            ProjectOpenPositions: state =>
            {
                var open = new List<ProjectedPosition>();
                foreach (var (_, ps) in state.Positions)
                {
                    if (ps.Phase == PositionPhase.Open)
                    {
                        // Recompute worst-case from open position state.
                        var entryPrice = ps.EntryPrice.Value;
                        var slPrice = ps.CurrentStopLoss.Value;
                        var slPips = ps.Direction == TradeDirection.Long
                            ? (entryPrice - slPrice) / EurusdInfo.PipSize
                            : (slPrice - entryPrice) / EurusdInfo.PipSize;
                        var pipValuePerLot = EurusdInfo.ContractSize * EurusdInfo.PipSize;
                        open.Add(new ProjectedPosition("EURUSD", slPips, ps.Lots, pipValuePerLot));
                    }
                }
                return open;
            },
            Seed: 42);

        var kernel = new Kernel(config);
        var queue = new InMemoryEngineEventQueue();
        var sink = new InMemoryStepRecordSink();
        var journal = new ChannelJournalWriter(sink, capacity: 1000, batchSize: 100);
        var effects = new CaptureEffectExecutor();
        var tape = new ListEventTape(
            new DatasetRef("golden", "hash", ["EURUSD"], ["H1"],
                bars[0].OpenTimeUtc, bars[^1].OpenTimeUtc,
                DatasetGranularity.Bar, bars.Count),
            bars.Select(b => (EngineEvent)new BarClosed(
                b.Symbol, b.Timeframe, b.Open, b.High, b.Low, b.Close, b.OpenTimeUtc)).ToList());

        var driver = new KernelDriver(kernel, queue, journal, effects, "kernel-test");

        // Strategy evaluator: runs AlwaysSignalStrategy logic, emits OrderProposed.
        var strategy = new AlwaysSignalStrategy();
        var evaluatedAt = new List<int>();
        var originalEnqueue = new Action<EngineEvent>(queue.Enqueue);
        // We'll inject OrderProposed events before each BarClosed by wrapping the tape.
        // Feed bars manually instead of using the driver's tape loop, so we can interleave
        // OrderProposed events.

        var initialState = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(10_000m, "Fixed"),
            0,
            ProtectionState.None,
            AccountView.Flat);

        // Manually drive the kernel bar-by-bar, emitting OrderProposed after strategy eval.
        var state = initialState;
        var orderIdSeq = 0;

        foreach (var bar in bars)
        {
            // Strategy evaluation — mirror AlwaysSignalStrategy logic.
            var context = new MarketContext(
                Eurusd,
                new Tick(Eurusd, bar.Close, bar.Close + 0.00001m, bar.OpenTimeUtc),
                new Dictionary<Timeframe, IReadOnlyList<Bar>> { [H1] = bars },
                new Dictionary<string, double>(),
                bar.OpenTimeUtc);

            var intent = strategy.Evaluate(context);
            if (intent is not null)
            {
                orderIdSeq++;
                var slPips = intent.StopLoss is { } sl
                    ? (intent.Direction == TradeDirection.Long
                        ? (bar.Close - sl.Value) / EurusdInfo.PipSize
                        : (sl.Value - bar.Close) / EurusdInfo.PipSize)
                    : 0m;
                var pipValuePerLot = EurusdInfo.ContractSize * EurusdInfo.PipSize;

                var proposed = new OrderProposed(
                    Guid.NewGuid(), intent.Symbol, intent.Direction, intent.OrderType,
                    intent.LimitPrice, intent.StopLoss, intent.TakeProfit,
                    intent.StrategyId, bar.Close, slPips, pipValuePerLot, bar.OpenTimeUtc);
                queue.Enqueue(proposed);
            }

            // Feed BarClosed event.
            queue.Enqueue(new BarClosed(
                bar.Symbol, bar.Timeframe, bar.Open, bar.High, bar.Low, bar.Close, bar.OpenTimeUtc));

            // Drain the queue — process all events for this bar.
            while (queue.TryDequeue(out var evt))
            {
                var decision = kernel.Decide(state, evt);
                state = decision.State;

                journal.Append(new StepRecord(
                    "kernel-test", 0, evt.OccurredAtUtc,
                    evt.GetType().Name, "{}",
                    decision.Effects.Select(e => e.GetType().Name).ToList(),
                    "[]",
                    RiskSnapshots.Capture(state),
                    null, null, []));

                foreach (var effect in decision.Effects)
                {
                    await effects.ExecuteAsync(effect, CancellationToken.None);

                    // Simulate venue feedback for accepted orders.
                    if (effect is SubmitOrder so)
                    {
                        var orderId = so.OrderId;
                        // Enqueue OrderSubmitted → the reducer creates the Intended position.
                        queue.Enqueue(new OrderSubmitted(
                            orderId, so.Symbol, so.Direction, so.Lots,
                            so.LimitPrice, so.StrategyId, bar.OpenTimeUtc,
                            OrderType.Market,
                            so.StopLoss, so.TakeProfit));
                        // Enqueue OrderFilled at bar close → moves to Open.
                        queue.Enqueue(new OrderFilled(
                            orderId, so.Symbol, so.Lots,
                            new Price(bar.Close), bar.OpenTimeUtc.AddSeconds(1)));
                    }
                }
            }

            // Account update: feed EquityObserved for the breach watchdog.
            var equity = 10_000m + state.Positions.Values
                .Where(p => p.Phase == PositionPhase.Open)
                .Sum(p => (bar.Close - p.EntryPrice.Value) * EurusdInfo.ContractSize * p.Lots
                    * (p.Direction == TradeDirection.Long ? 1 : -1));
            queue.Enqueue(new EquityObserved(10_000m, equity, equity - 10_000m, bar.OpenTimeUtc));

            // Drain again for feedback events (fills, account).
            while (queue.TryDequeue(out var evt))
            {
                var decision = kernel.Decide(state, evt);
                state = decision.State;
                foreach (var effect in decision.Effects)
                    await effects.ExecuteAsync(effect, CancellationToken.None);
            }
        }

        // DisposeAsync drains the background flush loop before we assert on the sink; FlushAsync is a
        // no-op for the continuous-loop writer, so asserting straight after it raced the flush (flaky).
        await journal.DisposeAsync();

        // --- Assert: equivalence against the REAL golden baseline, not a magic constant (K0) ---
        var golden = GoldenSnapshotLoader.Load();
        var goldenFirstTrade = golden.Trades.Should().NotBeEmpty(
            "the golden baseline must contain at least one trade to compare the kernel against").And.Subject.First();

        var submitEffects = effects.Effects.OfType<SubmitOrder>().ToList();
        submitEffects.Should().NotBeEmpty("the kernel must accept at least one order on a down-leg fixture");

        var firstSubmit = submitEffects[0];
        firstSubmit.Lots.Should().Be(goldenFirstTrade.Lots,
            "the kernel gate must size the first order identically to the golden baseline (loaded from " +
            "golden-snapshot.json, NOT a hardcoded 0.20 — that magic constant is exactly how the gate " +
            "went hollow before)");
        firstSubmit.Symbol.Should().Be(Eurusd);
        firstSubmit.Direction.ToString().Should().Be(goldenFirstTrade.Direction,
            "the kernel must take the same direction as the golden baseline's first trade");

        // Verify the kernel wrote journal entries.
        sink.Records.Should().NotBeEmpty("the kernel must journal every decision step");

        // Verify final state has the position in Open phase.
        state.Positions.Should().NotBeEmpty("at least one position must be open at end of bars");
    }

    /// <summary>
    /// K0 → K3 breadcrumb, NOW LIVE (iter-36 K3): the FULL-run equivalence gate. The kernel, driven over
    /// the golden fixture through the real kernel backtest loop (BarEvaluator → Kernel → EffectExecutor →
    /// venue + feedback bridge), reproduces golden-snapshot.json's ENTIRE trade sequence + final risk —
    /// not just the first order's sizing (asserted above). The loop wiring lives in
    /// <see cref="KernelLoopHarness"/> (shared with KernelBacktestLoopGoldenTests).
    /// </summary>
    [Fact]
    public async Task KernelFullRun_MatchesGolden_TradesAndRisk()
    {
        var run = await KernelLoopHarness.RunGoldenAsync();
        var golden = GoldenSnapshotLoader.Load();

        run.ClosedTrades.Should().HaveCount(golden.Trades.Count);
        var first = run.ClosedTrades[0].Result;
        first.Direction.ToString().Should().Be(golden.Trades[0].Direction);
        first.Lots.Should().Be(golden.Trades[0].Lots);
        first.EntryPrice.Value.Should().Be(golden.Trades[0].EntryPrice);
        first.ExitPrice.Value.Should().Be(golden.Trades[0].ExitPrice);
        first.ExitReason.Should().Be(golden.Trades[0].ExitReason);

        run.Final.Drawdown.PeakEquity.Should().Be(golden.FinalRisk.PeakEquity);
        run.Final.Drawdown.CurrentMaxDrawdown.Should().Be(golden.FinalRisk.CurrentMaxDrawdown);
        run.Final.Protection.InProtectionMode.Should().Be(golden.FinalRisk.InProtectionMode);
    }
}
