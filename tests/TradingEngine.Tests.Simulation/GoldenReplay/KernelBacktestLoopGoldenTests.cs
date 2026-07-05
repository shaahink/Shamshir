using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Engine;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// K3 gate (iter-36 cutover): the full golden run driven END-TO-END through the new kernel backtest loop —
/// <c>BarTape → BarEvaluator (K1) → Kernel → EffectExecutor → FakeVenue + venue-feedback bridge (K2)</c> —
/// with <see cref="EngineState"/> as the single authority. Must reproduce <c>golden-snapshot.json</c>'s
/// closed trades and final risk, and be deterministic across two runs.
///
/// The venue is the FakeVenue (zero-cost, closes at the SL/TP price) and equity is the realized model
/// (initial + closed net PnL) — the SAME economics the golden oracle (EngineHarness) used, so this proves
/// the kernel loop is behaviour-equivalent to the imperative loop the golden encodes. Wiring the
/// mark-to-market BacktestReplayAdapter as the default production venue is the K4 flip (with a reviewed
/// golden re-baseline for the floating-DD difference).
/// </summary>
[Trait("Category", "KernelAcceptance")]
[Trait("Speed", "Fast")]
public sealed class KernelBacktestLoopGoldenTests
{
    [Fact]
    public async Task KernelLoop_ReproducesGolden_TradesAndRisk()
    {
        var run = await KernelLoopHarness.RunGoldenAsync();
        var golden = GoldenSnapshotLoader.Load();

        // --- Closed trades match golden exactly (direction / lots / entry / exit / reason) ---
        var closed = run.ClosedTrades;
        closed.Should().HaveCount(golden.Trades.Count,
            "the kernel loop must close the same number of trades as the golden oracle");

        for (var i = 0; i < golden.Trades.Count; i++)
        {
            var g = golden.Trades[i];
            var t = closed[i].Result;
            t.Direction.ToString().Should().Be(g.Direction);
            t.Lots.Should().Be(g.Lots);
            t.EntryPrice.Value.Should().Be(g.EntryPrice);
            t.ExitPrice.Value.Should().Be(g.ExitPrice);
            t.ExitReason.Should().Be(g.ExitReason);
        }

        // --- Final risk (authoritative kernel EngineState) matches golden's finalRisk ---
        var dd = run.Final.Drawdown;
        dd.PeakEquity.Should().Be(golden.FinalRisk.PeakEquity);
        dd.CurrentDailyDrawdown.Should().Be(golden.FinalRisk.CurrentDailyDrawdown);
        dd.CurrentMaxDrawdown.Should().Be(golden.FinalRisk.CurrentMaxDrawdown);
        run.Final.Protection.InProtectionMode.Should().Be(golden.FinalRisk.InProtectionMode);
    }

    [Fact]
    public async Task KernelLoop_IsDeterministic_AcrossRuns()
    {
        var run1 = await KernelLoopHarness.RunGoldenAsync();
        var run2 = await KernelLoopHarness.RunGoldenAsync();

        run1.JournalJson.Should().Be(run2.JournalJson,
            "the kernel backtest loop must be bit-identical across two runs of the same tape");
    }

    [Fact]
    public async Task KernelLoop_DrivenFromBrokerStream_ReproducesGolden()
    {
        // The production entry point (RunFromBrokerAsync) drives off the venue's BarStream — the
        // mode-agnostic path live + backtest share (iter-36 K4). It must produce the same result as the
        // tape driver.
        var run = await KernelLoopHarness.RunGoldenAsync(viaBrokerStream: true);
        var golden = GoldenSnapshotLoader.Load();

        run.ClosedTrades.Should().HaveCount(golden.Trades.Count);
        run.ClosedTrades[0].Result.ExitReason.Should().Be(golden.Trades[0].ExitReason);
        run.ClosedTrades[0].Result.Lots.Should().Be(golden.Trades[0].Lots);
        run.Final.Drawdown.CurrentMaxDrawdown.Should().Be(golden.FinalRisk.CurrentMaxDrawdown);
    }

    // P2.4/D6 gate: proves the NEW evaluateTimeFlatten wiring end-to-end through the real kernel loop
    // (queue -> pump -> EngineReducer.HandleCloseRequested -> CloseOpenPosition effect -> venue), not just
    // KernelTimeFlattenEvaluator in isolation. A hook that unconditionally requests a flatten for every open
    // position must force-close the golden fixture's position with reason "TimeFlatten", earlier than its
    // natural SL/TP exit would have.
    [Fact]
    public async Task KernelLoop_TimeFlattenHook_ForceClosesOpenPosition()
    {
        IReadOnlyList<(Guid, string)> FlattenEveryOpenPosition(Bar bar, EngineState state) =>
            state.Positions.Where(kv => kv.Value.Phase == PositionPhase.Open)
                .Select(kv => (kv.Key, "TimeFlatten")).ToList();

        var run = await KernelLoopHarness.RunGoldenAsync(evaluateTimeFlatten: FlattenEveryOpenPosition);

        run.ClosedTrades.Should().NotBeEmpty("the time-flatten hook must force-close the open position");
        run.ClosedTrades[0].Result.ExitReason.Should().Be("TimeFlatten");
    }
}

/// <summary>
/// Builds and runs the golden fixture through the real <see cref="KernelBacktestLoop"/> with a FakeVenue
/// (ClientOrderId-honoring) and the real <see cref="EffectExecutor"/>. Shared by the K3 gate above and the
/// un-skipped K0 breadcrumb (KernelAcceptanceTests.KernelFullRun_MatchesGolden_TradesAndRisk).
/// </summary>
internal static class KernelLoopHarness
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");

    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static readonly RiskProfile StandardProfile = new(
        "standard", "Standard", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
        false, "ftmo-standard", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

    private static PropFirmRuleSet FtmoRuleSet() => new(
        "ftmo-standard", "ftmo-standard", "Fixed", 0.05, 0.10, 0.10, 0,
        "BalancePlusFloating", "22:00:00", "UTC",
        false, "High", 0, 0,
        false, "21:00:00", "20:00:00", "NextTradingDay", false);

    public sealed record Result(
        IReadOnlyList<TradeClosed> ClosedTrades,
        EngineState Final,
        string JournalJson,
        IReadOnlyList<StepRecord> Records);

    public static async Task<Result> RunGoldenAsync(
        bool viaBrokerStream = false,
        IReadOnlyList<Bar>? bars = null,
        ResetConfig? resetConfig = null,
        Func<Bar, EngineState, IReadOnlyList<(Guid PositionId, string Reason)>>? evaluateTimeFlatten = null)
    {
        var barsList = bars ?? GoldenBarFixture.Create();
        const decimal initialBalance = 10_000m;

        var ruleSet = FtmoRuleSet();
        var constraints = ConstraintSet.Resolve(StandardProfile, ruleSet);
        var sizing = new SizingPolicyOptions { FlattenAtFraction = 0.9 };

        var config = new KernelConfig(
            constraints, StandardProfile, sizing,
            ResolveSymbol: _ => EurusdInfo,
            ProjectOpenPositions: state =>
            {
                var open = new List<ProjectedPosition>();
                foreach (var (_, ps) in state.Positions)
                {
                    if (ps.Phase != PositionPhase.Open) continue;
                    var slPips = ps.Direction == TradeDirection.Long
                        ? (ps.EntryPrice.Value - ps.CurrentStopLoss.Value) / EurusdInfo.PipSize
                        : (ps.CurrentStopLoss.Value - ps.EntryPrice.Value) / EurusdInfo.PipSize;
                    open.Add(new ProjectedPosition(slPips, ps.Lots, EurusdInfo.ContractSize * EurusdInfo.PipSize));
                }
                return open;
            },
            Seed: 42);

        var kernel = new Kernel(config);

        // One strategy instance shared by the evaluator (reads it) and the EffectExecutor (resets it via
        // OnTradeResult so AlwaysSignalStrategy reopens after a close — golden's second order).
        IReadOnlyList<IStrategy> strategies = [new AlwaysSignalStrategy()];

        Func<string, string, decimal> crossRate = (_, _) => 1.0m;

        var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
        symbolRegistry.Get(Arg.Any<Symbol>()).Returns(EurusdInfo);

        var indicators = Substitute.For<IIndicatorService>();
        var indicatorSnapshot = new IndicatorSnapshotService(indicators, strategies);

        var strategyBank = Substitute.For<IStrategyBank>();
        strategyBank.GetActive(Arg.Any<Symbol>(), Arg.Any<Timeframe>(), Arg.Any<MarketRegime>())
            .ReturnsForAnyArgs(strategies);

        var regimeDetector = Substitute.For<IRegimeDetector>();
        regimeDetector.Detect(Arg.Any<Symbol>(), Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<IReadOnlyDictionary<string, double>>())
            .ReturnsForAnyArgs(MarketRegime.Trending);

        var newsFilter = Substitute.For<INewsFilter>();
        var sessionFilter = new SessionFilter();

        var riskManager = Substitute.For<IRiskManager>();
        riskManager.ActiveRuleSet.Returns(ruleSet);
        riskManager.CheckComplianceBlock(Arg.Any<TradeIntent>(), Arg.Any<RiskProfile>()).Returns((string?)null);
        riskManager.Drawdown.Returns(DrawdownReducer.CreateInitial(initialBalance, "Fixed"));

        var riskProfileResolver = Substitute.For<IRiskProfileResolver>();
        riskProfileResolver.Resolve(Arg.Any<string>()).Returns(StandardProfile);

        var evaluator = new BarEvaluator(
            indicatorSnapshot, strategyBank, regimeDetector, signalGate: null,
            new EntryPlanner(symbolRegistry, NullLogger<EntryPlanner>.Instance),
            symbolRegistry, crossRate, newsFilter, sessionFilter, riskManager, riskProfileResolver,
            governor: null, NullLogger.Instance, new TradingEngine.Infrastructure.Indicators.SkenderIndicatorService());

        var eventBus = new CollectingEventBus();
        var decisionJournal = new InMemoryDecisionJournal();
        var positionManager = Substitute.For<IPositionManager>();
        var clock = new ManualClock { UtcNow = barsList[0].OpenTimeUtc };
        var venue = new FakeVenue(symbolRegistry, crossRate);

        var effects = new EffectExecutor(
            venue, eventBus, decisionJournal,
            equitySink: null, progress: null,
            new EngineRunContext("kernel-loop"), clock,
            NullLogger<EffectExecutor>.Instance,
            symbolRegistry, crossRate, strategies,
            riskManager, positionManager,
            governor: null, signalGate: null);

        var queue = new InMemoryEngineEventQueue();
        var journal = new ListJournalWriter();

        var loop = new KernelBacktestLoop(
            kernel, evaluator, effects, venue, queue, journal,
            advanceVenue: bar => { venue.CurrentMarketPrice = bar.Close; venue.BrokerTimeUtc = bar.OpenTimeUtc; },
            initialBalance, runId: "kernel-loop", NullLogger.Instance,
            captureRisk: RiskSnapshots.Capture,
            realizedEquity: () => initialBalance + eventBus.OfType<TradeClosed>().Sum(tc => tc.Result.NetPnL.Amount),
            evaluateTimeFlatten: evaluateTimeFlatten,
            resetConfig: resetConfig);

        var dataset = new DatasetRef("golden", "hash", ["EURUSD"], ["H1"],
            barsList[0].OpenTimeUtc, barsList[^1].OpenTimeUtc, DatasetGranularity.Bar, barsList.Count);
        var tape = new ListEventTape(dataset,
            barsList.Select(b => (EngineEvent)new BarClosed(b.Symbol, b.Timeframe, b.Open, b.High, b.Low, b.Close, b.OpenTimeUtc)).ToList());

        await venue.ConnectAsync(CancellationToken.None);
        var initialState = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(initialBalance, "Fixed"),
            0, ProtectionState.None, AccountView.Flat);

        EngineState finalState;
        if (viaBrokerStream)
        {
            // Production path: bars arrive on the venue's BarStream; drive via RunFromBrokerAsync.
            foreach (var b in barsList) venue.PostBar(b);
            venue.CompleteBars();
            finalState = await loop.RunFromBrokerAsync(initialState, CancellationToken.None);
        }
        else
        {
            finalState = await loop.RunAsync(tape, initialState, CancellationToken.None);
        }

        return new Result(eventBus.OfType<TradeClosed>(), finalState, journal.Serialize(), journal.Records);
    }

    private sealed class ListJournalWriter : IJournalWriter
    {
        // Mirror SqliteStepRecordSink.EventJsonOpts so the harness inspects the SAME serialized bytes
        // production persists. F3 moved EventJson/EffectsJson serialization off the pump thread into the
        // sink, leaving them empty on the raw StepRecord; re-materialise them here from RawEvent/RawEffects
        // exactly as the sink does, or golden tests that parse EventJson (OrderIdOf) break and the
        // determinism comparison over EffectsJson goes vacuous ("" == "").
        private static readonly JsonSerializerOptions SinkOpts = new()
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        private readonly List<StepRecord> _records = [];

        public void Append(StepRecord record)
        {
            if (string.IsNullOrEmpty(record.EventJson) && record.RawEvent is not null)
                record = record with { EventJson = JsonSerializer.Serialize(record.RawEvent, record.RawEvent.GetType(), SinkOpts) };
            if (string.IsNullOrEmpty(record.EffectsJson) && record.RawEffects is not null)
                record = record with { EffectsJson = JsonSerializer.Serialize(record.RawEffects, SinkOpts) };
            _records.Add(record);
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public IReadOnlyList<StepRecord> Records => _records;

        // Compare effect payloads + risk across runs (the determinism contract); the run-constant RunId
        // and the sim-time are stable, the order ids are the evaluator's deterministic counter.
        public string Serialize() => JsonSerializer.Serialize(
            _records.Select(r => new { r.Seq, r.EventKind, r.EffectKinds, r.EffectsJson, r.Risk }),
            new JsonSerializerOptions { WriteIndented = false, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
    }
}
