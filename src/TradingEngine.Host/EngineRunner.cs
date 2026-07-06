using TradingEngine.Engine;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Risk.Filters;
using TradingEngine.Services;
using TradingEngine.Services.Helpers;

namespace TradingEngine.Host;

/// <summary>
/// The engine's run logic, independent of the hosting model. <see cref="EngineWorker"/> is a thin
/// <c>BackgroundService</c> that just calls <see cref="RunAsync"/>; tests can call it directly.
///
/// iter-36 K4 — THE CUTOVER: this now drives the <b>kernel</b> for both live and backtest. Bars arrive on
/// <see cref="IBrokerAdapter.BarStream"/> (fed by the replay adapter or the cTrader adapter), the
/// <see cref="BarEvaluator"/> turns each into <c>OrderProposed</c> events, the pure kernel gates/sizes
/// them, the <see cref="IEffectExecutor"/> reaches the venue, and the venue feedback (fills/account)
/// re-enters as kernel events — all through <see cref="KernelBacktestLoop"/> with <see cref="EngineState"/>
/// as the single authority. The old imperative loop (TradingLoop + AccountProcessor + SimulateBarExits +
/// the OrderGate dispatch) is gone from the production path; those classes survive only as the test
/// regression oracle behind golden-snapshot.json.
/// </summary>
public sealed class EngineRunner
{
    private readonly IBrokerAdapter _broker;
    private readonly IRiskManager _riskManager;
    private readonly IReadOnlyList<IStrategy> _strategies;
    private readonly IEngineClock _clock;
    private readonly EngineMode _engineMode;
    private readonly EngineRunContext _runContext;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Func<string, string, decimal> _crossRate;
    private readonly CrossRateStore _crossRateStore;
    private readonly IRiskProfileResolver _riskProfileResolver;
    private readonly SizingPolicyOptions _sizingPolicy;
    private readonly ISignalGate? _signalGate;
    private readonly IEffectExecutor _effects;
    private readonly IEventBus _eventBus;
    private readonly IProgress<BacktestProgressEvent>? _progress;
    private readonly IEquitySink? _equitySink;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IJournalWriter _journal;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    private readonly IndicatorSnapshotService _indicatorSnapshot;
    private readonly BarEvaluator _evaluator;
    private readonly KernelTrailingEvaluator _trailing;
    private readonly KernelTimeFlattenEvaluator _timeFlatten;
    private readonly KernelWeekendFlattenEvaluator _weekendFlatten;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>>>? _preloadedAuxBars;

    private long _barCount;

    public EngineRunner(
        EngineWorkerDependencies deps,
        EngineRunContext runContext,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        _broker = deps.Market.Broker;
        _riskManager = deps.Risk.RiskManager;
        _strategies = deps.Strategies.Strategies.ToList();
        _clock = deps.Market.Clock;
        _engineMode = deps.Market.EngineMode;
        _runContext = runContext;
        _symbolRegistry = deps.Market.SymbolRegistry;
        _crossRate = deps.Risk.CrossRateProvider;
        _crossRateStore = deps.Market.CrossRateStore;
        _riskProfileResolver = deps.Risk.RiskProfileResolver;
        _sizingPolicy = deps.Risk.SizingPolicy;
        _signalGate = deps.Strategies.SignalGate;
        _effects = deps.Persistence.EffectExecutor
            ?? throw new InvalidOperationException("EffectExecutor is required for the kernel engine (iter-36 K4).");
        _eventBus = deps.Persistence.EventBus;
        _progress = deps.Persistence.Progress;
        _equitySink = deps.Persistence.EquitySink;
        _scopeFactory = deps.Persistence.ScopeFactory;
        _journal = deps.Persistence.StepJournal ?? new NullJournalWriter();
        _logger = logger;
        _preloadedAuxBars = deps.Persistence.PreloadedAuxBars;

        _indicatorSnapshot = new IndicatorSnapshotService(deps.Market.Indicators, _strategies);
        _evaluator = new BarEvaluator(
            _indicatorSnapshot, deps.Strategies.StrategyBank, deps.Strategies.RegimeDetector, _signalGate,
            deps.Strategies.EntryPlanner, _symbolRegistry, _crossRate,
            deps.Risk.NewsFilter, deps.Risk.SessionFilter, _riskManager, _riskProfileResolver,
            deps.Risk.Governor, logger, deps.Market.Indicators,
            referenceScales: null, exitCalibrationLookup: deps.Strategies.ExitCalibrationLookup);
        _trailing = new KernelTrailingEvaluator(
            deps.Strategies.PositionManager, _symbolRegistry, _indicatorSnapshot, _strategies,
            new TradingEngine.Services.AddOns.AddOnResolver(deps.Strategies.ExitCalibrationLookup), deps.Market.Indicators);
        _timeFlatten = new KernelTimeFlattenEvaluator(_strategies);
        _weekendFlatten = new KernelWeekendFlattenEvaluator(_strategies);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Kernel engine starting. Mode={Mode} Strategies={Count}", _engineMode, _strategies.Count);

        if (_signalGate is not null)
        {
            foreach (var s in _strategies)
                _signalGate.RegisterStrategy(s.Config);
        }

        _broker.RegisterConnectedHandler(() => { _indicatorSnapshot.Reset(); _evaluator.Reset(); _trailing.Reset(); });
        _broker.RegisterReconcileHandler(state =>
        {
            // Keep the RiskManager's risk-amount tracker's equity level fresh (the kernel owns DD/protection).
            if (state.Balance > 0) _riskManager.UpdateEquityLevels(state.Equity);
        });

        await _broker.ConnectAsync(ct);

        decimal initialBalance = _riskManager.InitialBalance > 0 ? _riskManager.InitialBalance : 10_000m;
        try
        {
            var accountState = await _broker.GetAccountStateAsync(ct);
            if (accountState.Balance > 0)
            {
                initialBalance = accountState.Balance;
                _riskManager.UpdateEquityLevels(accountState.Equity);
                _logger.LogInformation("Startup reconciliation: Balance={Balance} Equity={Equity}",
                    accountState.Balance, accountState.Equity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup reconciliation failed — continuing with clean state");
        }

        await _indicatorSnapshot.WarmUpIndicatorsAsync(ct);

        // P1.3/P1.5.2: register the full known range of auxiliary-timeframe bars (e.g. H4 for mtf-trend) —
        // but do NOT make them visible yet. Dumping the whole run's aux range in at t=0 leaked future bars
        // into every decision (a strategy's higher-TF indicator saw the run-end value from bar 1 onward —
        // lookahead bias). BarEvaluator.EvaluateAsync reveals aux bars incrementally, gated by close time,
        // via IndicatorSnapshotService.AdvanceAuxBarsAsync, one decision bar at a time.
        if (_preloadedAuxBars is { Count: > 0 })
        {
            foreach (var (symStr, byTf) in _preloadedAuxBars)
            {
                var symbol = Symbol.Parse(symStr);
                foreach (var (tf, barList) in byTf)
                {
                    _indicatorSnapshot.SetAuxBarSource(symbol, tf, barList);
                }
            }
        }

        // Build the kernel loop now — RiskManager constraints/ruleset are set by WireRiskRules during the
        // connect/warmup window above, so they're populated by here.
        var loop = BuildKernelLoop(initialBalance);
        var initialState = BuildInitialState(initialBalance);

        EngineState finalState;
        try
        {
            finalState = await loop.RunFromBrokerAsync(initialState, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            FlushTimingReport(loop);
            throw;
        }
        catch (OperationCanceledException)
        {
            FlushTimingReport(loop);
            _logger.LogInformation("Kernel engine: bar stream ended, tail-drain triggered OCE — treating as normal completion");
            finalState = initialState;
        }

        await FlushBacktestEquityAsync(ct);
        FlushTimingReport(loop);

        _logger.LogInformation("Kernel engine stopped. Bars={Bars} OpenPositions={Open}",
            Interlocked.Read(ref _barCount), finalState.Positions.Count);
    }

    private void FlushTimingReport(KernelBacktestLoop loop)
    {
        if (loop.TimingReport is { } timing)
        {
            WriteTimingReport(timing);
        }
    }

    // iter-37 K-GAP-2: at the end of a backtest, flush the in-memory BufferedEquitySink to the
    // EquitySnapshots table in one batched write so GET /api/runs/{id}/equity (which reads the table) is no
    // longer empty for a finished backtest. Live runs use PersistentEquitySink (per-bar), so this is a no-op.
    private async Task FlushBacktestEquityAsync(CancellationToken ct)
    {
        if (_engineMode != EngineMode.Backtest || _scopeFactory is null) return;
        if (_equitySink is not BufferedEquitySink buffered) return;

        var snapshots = buffered.GetSnapshots();
        if (snapshots.Count == 0) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IEquityRepository>();
            await EquitySnapshotFlush.FlushAsync(snapshots, repo, _engineMode, _runContext.RunId, ct);
            _logger.LogInformation("Flushed {Count} backtest equity snapshots to EquitySnapshots", snapshots.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush backtest equity snapshots");
        }
    }

    private void WriteTimingReport(TimingReport timing)
    {
        try
        {
            var profilingDir = Path.Combine(
                Path.GetTempPath(), "shamshir-profiling");
            Directory.CreateDirectory(profilingDir);
            var path = Path.Combine(profilingDir, $"{_runContext.RunId}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(timing,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write timing report for run {RunId}", _runContext.RunId);
        }
    }

    private KernelBacktestLoop BuildKernelLoop(decimal initialBalance)
    {
        var profile = ResolveActiveProfile();
        var ruleSet = _riskManager.ActiveRuleSet ?? DefaultRuleSet();
        var constraints = _riskManager.Constraints ?? ConstraintSet.Resolve(profile, ruleSet);

        var config = new KernelConfig(
            constraints, profile, _sizingPolicy,
            ResolveSymbol: _symbolRegistry.Get,
            ProjectOpenPositions: ProjectOpenPositions,
            Seed: 42);

        var kernel = new Kernel(config);
        var queue = new InMemoryEngineEventQueue();

        var dailyDdGuard = constraints.MaxDailyLoss > 0 && constraints.DailyDdEnabled
            ? new KernelDailyDdGuardEvaluator(constraints.MaxDailyLoss, constraints.DailyDdBase)
            : null;

        return new KernelBacktestLoop(
            kernel, _evaluator, _effects, _broker, queue, _journal,
            advanceVenue: bar => { UpdateCrossRates(bar); _broker.OnBarObserved(bar); },
            initialBalance, _runContext.RunId, _logger,
            captureRisk: RiskSnapshots.Capture,
            realizedEquity: null,                // production = mark-to-market via the venue AccountStream
            onBarProcessed: ReportBar,
            onEvent: ReportEvent,              // iter-38 B1: feed live-monitor counters
            evaluateTrailing: _trailing.Evaluate,
            evaluateTimeFlatten: _timeFlatten.Evaluate,
            evaluateDailyDdGuard: dailyDdGuard is not null ? dailyDdGuard.Evaluate : null,
            evaluateWeekendFlatten: _weekendFlatten.Evaluate,
            // iter-36 K-GAP-1: drive the prop-firm day/week/month resets off the active ruleset's reset clock
            // so multi-day runs re-base drawdown + reset the governor (C4/H7). Single-day golden never crosses.
            resetConfig: ResetConfig.FromRuleSet(ruleSet.DailyResetTimeUtc, ruleSet.DailyResetTimezone),
            diagnosticsEnabled: _runContext.DiagnosticsEnabled);
    }

    // iter-38 B1: feed the live-monitor counters (Signals/Orders/Fills/Rejections/Breaches) from the
    // authoriative kernel event stream. The kernel engine only fired BAR + CLOSE progress events before,
    // leaving the other counters permanently at 0 on the SignalR monitor (OPEN-ISSUES O1/T7a).
    private void ReportEvent(EngineEvent evt)
    {
        // iter-strategy-system P3: build a descriptive line for the live journal. The kernel events carry the
        // strategy/symbol/side/price/size — the old code threw them away (empty message), which is why the
        // Monitor journal read as bare event types with no "who did what".
        var (type, message) = evt switch
        {
            OrderProposed p => ("SIGNAL",
                $"{p.StrategyId} {p.Symbol.Value} {p.Direction} signal @ {p.SignalPriceMid:F5} "
                + $"(SL {p.StopLoss.Value:F5}{(p.TakeProfit is { } tp ? $", TP {tp.Value:F5}" : "")})"),
            OrderSubmitted s => ("ORDER",
                $"{s.StrategyId} {s.Symbol.Value} {s.Direction} {s.Lots:0.##} lots order"),
            OrderFilled f => ("EXEC",
                $"{f.Symbol.Value} filled {f.FilledLots:0.##} lots @ {f.FillPrice.Value:F5}"),
            OrderPartiallyFilled f => ("EXEC",
                $"{f.Symbol.Value} partial fill {f.FilledLots:0.##} lots @ {f.FillPrice.Value:F5}"),
            OrderRejected r => ("REJECTED", $"{r.Symbol.Value} rejected: {r.Reason}"),
            DrawdownBreached => ("BREACH", "Drawdown limit breached"),
            _ => ((string?)null, string.Empty),
        };
        if (type is not null)
            _progress?.Report(new BacktestProgressEvent(_runContext.RunId, type, message, evt.OccurredAtUtc));
    }

    private void ReportBar(Bar bar, EngineState state)
    {
        Interlocked.Increment(ref _barCount);
        _progress?.Report(new BacktestProgressEvent(
            _runContext.RunId, "BAR",
            $"Bar {bar.OpenTimeUtc:yyyy-MM-dd HH:mm} | close={bar.Close:F5} | total={Interlocked.Read(ref _barCount)}",
            _clock.UtcNow));

        // gap-4: persist an equity/DD snapshot from the AUTHORITATIVE EngineState so the Monitor (which polls
        // IAccountSnapshotStore) isn't blank under the kernel engine. Was AccountProcessor's job — now the
        // kernel owns drawdown/equity, so the snapshot is read straight off state (sim-time stamped).
        var snap = KernelEquitySnapshot.From(
            state, bar.OpenTimeUtc, _runContext.RunId,
            // iter-38 W-A7: feed the active ruleset's daily-loss limit so the snapshot carries a real
            // distance-to-daily-limit (and the governor band/reason) for the Monitor.
            (decimal)(_riskManager.ActiveRuleSet?.MaxDailyLossPercent ?? 0d));
        _equitySink?.Observe(snap);

        // iter-redesign-ctrader P4: publish EquityUpdated so EquityPersistenceHandler persists the snapshot
        // via the event-driven DB path (defense-in-depth — the FlushBacktestEquityAsync batch-write is the
        // primary path; this ensures the event-based persistence also fires for replay + cTrader).
        _ = _eventBus.PublishAsync(new EquityUpdated(
            EquitySnapshotFlush.ToEquity(snap, _engineMode), null!, bar.OpenTimeUtc, _runContext.RunId), CancellationToken.None);

        // iter-37 K-GAP-3: persist the per-run bar so the chart renders for LIVE + non-catalog runs (a
        // backtest over catalog data already has bars; BarQueryService dedups by timestamp, so the extra
        // per-run copy is harmless). BarPersistenceHandler (wired via WireEventHandlers) enqueues to the
        // BufferedBarWriter → IBarRepository. The old imperative TradingLoop published this; the kernel
        // path didn't, leaving live charts blank.
        _ = _eventBus.PublishAsync(new BarIngested(_runContext.RunId, bar), CancellationToken.None);

        // iter-36 K5: the per-strategy verdicts are folded onto the BarClosed StepRecord (the single
        // journal) by KernelBacktestLoop.BuildStepRecord — the old BarEvaluated → BarEvaluationHandler →
        // BarEvaluations path is gone. iter-37 F2's per-bar "why" funnel reads the StepRecord journal.
    }

    private IReadOnlyList<ProjectedPosition> ProjectOpenPositions(EngineState state)
    {
        var result = new List<ProjectedPosition>();
        foreach (var (_, ps) in state.Positions)
        {
            if (ps.Phase != PositionPhase.Open) continue;
            var si = _symbolRegistry.Get(ps.Symbol);
            var slPips = Math.Abs(ps.EntryPrice.Value - ps.CurrentStopLoss.Value) / si.PipSize;
            var pipValue = PipCalculator.PipValuePerLot(si, ps.EntryPrice.Value, _crossRate);
            result.Add(new ProjectedPosition(ps.Symbol.Value, slPips, ps.Lots, pipValue));
        }
        return result;
    }

    private RiskProfile ResolveActiveProfile()
    {
        var profileId = _strategies
            .Select(s => s.Config.RiskProfileId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? "standard";
        return _riskProfileResolver.Resolve(profileId);
    }

    private EngineState BuildInitialState(decimal initialBalance)
    {
        var drawdownType = _riskManager.ActiveRuleSet?.DrawdownType ?? "Fixed";
        return new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(initialBalance, drawdownType),
            0,
            ProtectionState.None,
            new AccountView(initialBalance, initialBalance, 0m))
        {
            // iter-redesign-ctrader P1: the venue determines who owns exit execution.
            // VenueManaged (cTrader, unified replay) → engine never detects SL/TP; EngineSimulated → legacy.
            ExitMode = _broker.ExitMode,
        };
    }

    private void UpdateCrossRates(Bar bar)
    {
        if (bar.Symbol.Value == "GBPUSD") _crossRateStore.GbpUsdRate = bar.Close;
        else if (bar.Symbol.Value == "USDJPY") _crossRateStore.UsdJpyRate = bar.Close;
    }

    private static PropFirmRuleSet DefaultRuleSet() => new(
        "none", "None", "Fixed", 0.05, 0.10, 0.10, 0,
        "BalancePlusFloating", "22:00:00", "UTC", false, "High", 0, 0,
        false, "21:00:00", "20:00:00", "NextTradingDay", false);
}
