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

        _indicatorSnapshot = new IndicatorSnapshotService(deps.Market.Indicators, _strategies);
        _evaluator = new BarEvaluator(
            _indicatorSnapshot, deps.Strategies.StrategyBank, deps.Strategies.RegimeDetector, _signalGate,
            deps.Strategies.EntryPlanner, _symbolRegistry, _crossRate,
            deps.Risk.NewsFilter, deps.Risk.SessionFilter, _riskManager, _riskProfileResolver,
            deps.Risk.Governor, logger);
        _trailing = new KernelTrailingEvaluator(
            deps.Strategies.PositionManager, _symbolRegistry, _indicatorSnapshot, _strategies);
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

        // Build the kernel loop now — RiskManager constraints/ruleset are set by WireRiskRules during the
        // connect/warmup window above, so they're populated by here.
        var loop = BuildKernelLoop(initialBalance);
        var initialState = BuildInitialState(initialBalance);

        var finalState = await loop.RunFromBrokerAsync(initialState, ct);

        await FlushBacktestEquityAsync(ct);

        _logger.LogInformation("Kernel engine stopped. Bars={Bars} OpenPositions={Open}",
            Interlocked.Read(ref _barCount), finalState.Positions.Count);
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

        return new KernelBacktestLoop(
            kernel, _evaluator, _effects, _broker, queue, _journal,
            advanceVenue: bar => { UpdateCrossRates(bar); _broker.OnBarObserved(bar); },
            initialBalance, _runContext.RunId, _logger,
            captureRisk: RiskSnapshots.Capture,
            realizedEquity: null,                // production = mark-to-market via the venue AccountStream
            onBarProcessed: ReportBar,
            evaluateTrailing: _trailing.Evaluate,
            // iter-36 K-GAP-1: drive the prop-firm day/week/month resets off the active ruleset's reset clock
            // so multi-day runs re-base drawdown + reset the governor (C4/H7). Single-day golden never crosses.
            resetConfig: ResetConfig.FromRuleSet(ruleSet.DailyResetTimeUtc, ruleSet.DailyResetTimezone));
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
        _equitySink?.Observe(KernelEquitySnapshot.From(state, bar.OpenTimeUtc, _runContext.RunId));

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
            result.Add(new ProjectedPosition(slPips, ps.Lots, pipValue));
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
            new AccountView(initialBalance, initialBalance, 0m));
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
