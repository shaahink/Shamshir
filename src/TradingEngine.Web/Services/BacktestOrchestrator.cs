using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using TradingEngine.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TradingEngine.CTraderRunner;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Caching;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Risk;
using TradingEngine.Risk.Filters;
using TradingEngine.Services;

namespace TradingEngine.Web.Services;

public sealed class BacktestOrchestrator : IBacktestCommandService, ILiveRunReader
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BacktestOrchestrator> _logger;
    private readonly BacktestProgressStore _progressStore;
    private readonly BacktestJournal _journal;
    private readonly IConfiguration _configuration;
    private readonly RunProgressBroadcaster _broadcaster;
    private readonly IRunDataCache? _runDataCache;
    private readonly IMemoryCache? _memoryCache;
    private readonly RunRegistry _registry;
    private readonly VenueRunnerRegistry _venues;
    private readonly ConcurrentDictionary<string, string> _idempotencyKeys = new();
    private readonly object _idempotencyLock = new();

    private const string RunsListCacheKey = "runs:all";
    private const int MaxIdempotencyKeys = 10_000;

    private readonly int _maxTapeConcurrency;
    private readonly SemaphoreSlim _tapeSemaphore;
    // X4: cTrader work no longer has a private serial semaphore here — it shares the one
    // CTraderProcessOwner lane (bounded parallel, shared with market-data downloads).
    private readonly CTraderProcessOwner _owner;
    private readonly RunConfigAssembler _configAssembler;
    private readonly RunRecordStore _records;
    private readonly ConcurrentQueue<(string RunId, BacktestConfig Config)> _queue = new();
    private readonly CancellationTokenSource _dequeueCts = new();
    private readonly Task? _dequeueTask;

    public int QueuedCount => _queue.Count;
    public int RunningTapeCount => _registry.All().Count(r => r.Status == "running" && !string.Equals(r.Venue, "ctrader", StringComparison.OrdinalIgnoreCase));
    public int RunningCtraderCount => _registry.All().Count(r => r.Status == "running" && string.Equals(r.Venue, "ctrader", StringComparison.OrdinalIgnoreCase));

    public int? GetQueuePosition(string runId)
    {
        var i = 0;
        foreach (var (rId, _) in _queue)
        {
            if (rId == runId) return i + 1;
            i++;
        }
        return null;
    }



    // BacktestRunState + RunWarning live in Runs/BacktestRunState.cs; progress projection + the
    // funnel tally live in Runs/RunProgressProjector.cs (both were nested here pre-refactor).
    public BacktestOrchestrator(
        IServiceScopeFactory scopeFactory,
        BacktestProgressStore progressStore,
        BacktestJournal journal,
        IConfiguration configuration,
        RunProgressBroadcaster broadcaster,
        ILogger<BacktestOrchestrator> logger,
        CTraderProcessOwner owner,
        RunConfigAssembler configAssembler,
        RunRecordStore records,
        RunRegistry registry,
        VenueRunnerRegistry venues,
        IRunDataCache? runDataCache = null,
        IMemoryCache? memoryCache = null)
    {
        _scopeFactory = scopeFactory;
        _progressStore = progressStore;
        _journal = journal;
        _configuration = configuration;
        _broadcaster = broadcaster;
        _owner = owner;
        _configAssembler = configAssembler;
        _records = records;
        _registry = registry;
        _venues = venues;
        _runDataCache = runDataCache;
        _memoryCache = memoryCache;
        _logger = logger;

        var configuredMax = _configuration.GetValue<int?>("RunQueue:MaxTapeConcurrency");
        _maxTapeConcurrency = configuredMax is > 0 ? configuredMax.Value : 3;
        _tapeSemaphore = new SemaphoreSlim(_maxTapeConcurrency, _maxTapeConcurrency);

        _dequeueTask = Task.Run(() => DequeueLoopAsync(_dequeueCts.Token));
    }

    private void EnqueueLog(string runId, ConcurrentQueue<string> queue, string msg)
    {
        _journal.Write(runId, "LOG", msg, queue);
    }

    public BacktestRunState Start(BacktestConfig cfg)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        cfg = cfg with { RunId = runId };
        var state = new BacktestRunState
        {
            RunId = runId,
            Symbol = cfg.Symbol,
            Period = cfg.Period,
            BarsTotal = RunRequestParser.EstimateBarCount(cfg.Start, cfg.End, cfg.Period),
            Venue = cfg.CustomParams.GetValueOrDefault("Venue") ?? "replay",
            GovernorEnabled = cfg.CustomParams.GetValueOrDefault("GovernorEnabled") != "false",
            RegimeEnabled = cfg.CustomParams.GetValueOrDefault("DisableRegime") != "true",
            CommissionPerMillion = (double)cfg.CommissionPerMillion,
            SpreadPips = (double)cfg.SpreadPips,
            InitialBalance = cfg.Balance,
            BacktestFrom = cfg.Start,
            BacktestTo = cfg.End,
            RiskProfileId = cfg.CustomParams.GetValueOrDefault("RiskProfileId"),
            Speed = float.TryParse(cfg.CustomParams.GetValueOrDefault("Speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var spd) ? Math.Clamp(spd, 0f, 10f) : 10f,
            ExplorationMode = cfg.CustomParams.GetValueOrDefault("ExplorationMode") == "true",
            RecordExcursions = cfg.CustomParams.GetValueOrDefault("RecordExcursions") == "true",
            // Set immediately, not just when RunAsync reaches its own (redundant) assignment: a live
            // GET can now observe this state the instant Start() returns, since X0's dequeue can start
            // RunAsync right away when a slot is free — Start() no longer blocks on a synchronous bar
            // count first (found live: a client polling right after POST /api/runs saw "[]" here).
            RunPlanJson = cfg.CustomParams.GetValueOrDefault("RunRows") ?? "[]",
        };
        _registry.Register(state);
        state.CancellationSource = new CancellationTokenSource();
        state.Status = RunStateMachine.Queued;

        _memoryCache?.Remove(RunsListCacheKey);

        RefreshBarCountAsync(state, cfg);
        EnqueueRun(runId, cfg, state);

        return state;
    }

    /// <summary>Enqueue a run for later execution. Persists as "queued" and signals the dequeue loop.</summary>
    private void EnqueueRun(string runId, BacktestConfig cfg, BacktestRunState state)
    {
        _queue.Enqueue((runId, cfg));
        EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Queued backtest {runId} (pos={_queue.Count})...");

        Task.Run(async () =>
        {
            try
            {
                await _records.WriteStartRecordAsync(runId, cfg, state.StartedAt, effectiveConfigJson: null,
                    status: RunStateMachine.Queued, queuePosition: _queue.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist queued run {RunId}", runId);
            }
        });

        TryDequeueNext();
    }

    /// <summary>Background loop that dequeues runs when slots are available.</summary>
    private async Task DequeueLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TryDequeueNext();
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Try to dequeue one run if a venue-appropriate slot is free.</summary>
    private void TryDequeueNext()
    {
        if (!_queue.TryPeek(out var peeked)) return;
        var (runId, cfg) = peeked;
        if (!_registry.TryGet(runId, out var state)) return;
        if (state.Status != RunStateMachine.Queued) return;

        var isCtrader = VenueRouting.ResolveUseCtrader(cfg.CustomParams.GetValueOrDefault("Venue"));
        // X4: cTrader admission goes through the shared owner lane (parallel with downloads, bounded);
        // tape keeps its own semaphore.
        var availableSlots = isCtrader ? _owner.AvailableSlots : _tapeSemaphore.CurrentCount;
        if (availableSlots == 0) return;

        if (!_queue.TryDequeue(out var dequeued)) return;
        if (!_registry.TryGet(dequeued.RunId, out state)) return;
        if (state.Status != RunStateMachine.Queued) { TryDequeueNext(); return; }

        TransitionRun(state, RunStateMachine.Starting);
        EnqueueLog(state.RunId, state.LogLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {state.RunId} ({dequeued.Config.CustomParams.GetValueOrDefault("Venue") ?? "replay"})...");

        state.RunTask = Task.Run(async () =>
        {
            IDisposable? lease = null;
            try
            {
                // Honor the run's own token while waiting for a slot: cancelling a run that is still
                // queued behind others must take effect immediately, not only once a slot frees up
                // (found live in the X0 cancel-mid-queue smoke test — WaitAsync(CancellationToken.None)
                // made a queued cancel invisible until the run would have started anyway).
                lease = isCtrader
                    ? await _owner.AcquireAsync(state.CancellationSource!.Token)
                    : await AcquireTapeLaneAsync(state.CancellationSource!.Token);
                await RunAsync(dequeued.RunId, dequeued.Config, state.CancellationSource!.Token);
            }
            catch (OperationCanceledException) when (lease is null)
            {
                TransitionRun(state, RunStateMachine.Cancelled);
                EnqueueLog(state.RunId, state.LogLines,
                    $"[{DateTime.UtcNow:HH:mm:ss}] Cancelled (was waiting for a concurrency slot).");
                _ = _records.WriteEndRecordAsync(dequeued.RunId, dequeued.Config, state.StartedAt,
                    new BacktestResult { RunId = dequeued.RunId, ExitCode = 0, ErrorMessage = "Cancelled while waiting in queue." },
                    RunTradeStats.Empty, effectiveConfigJson: null, status: state.Status);
            }
            finally
            {
                lease?.Dispose();
                TryDequeueNext();
            }
        });

        TryDequeueNext();
    }

    /// <summary>Real bar count from market data, replacing the calendar estimate (X1). Truly async —
    /// callers must await it. <see cref="Start"/> cannot (it runs inside a caller's <c>lock</c> block
    /// on one path), so it uses <see cref="RefreshBarCountAsync"/> instead; a blocking
    /// <c>.GetAwaiter().GetResult()</c> here previously starved the thread pool the moment X0 allowed
    /// concurrent <c>Start()</c> calls (found live in the X0 concurrency smoke test).</summary>
    private async Task<int> ResolveBarCountAsync(BacktestConfig cfg, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetService<IMarketDataStore>();
            if (store is not null)
            {
                var tf = RunRequestParser.ParseTimeframe(cfg.Period);
                var count = await store.CountBarsAsync(new Symbol(cfg.Symbol), tf, cfg.Start, cfg.End, ct);
                if (count > 0) return count;
            }
        }
        catch { }

        return RunRequestParser.EstimateBarCount(cfg.Start, cfg.End, cfg.Period);
    }

    /// <summary>Fire-and-forget upgrade of a just-registered run's placeholder BarsTotal (the calendar
    /// estimate) to the real bar count once the DB answers. Never blocks <see cref="Start"/> — progress
    /// display can tolerate a few hundred ms of estimate-then-real, but the request path cannot tolerate
    /// a synchronous DB round-trip under concurrent load.</summary>
    private void RefreshBarCountAsync(BacktestRunState state, BacktestConfig cfg)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var real = await ResolveBarCountAsync(cfg, CancellationToken.None);
                if (real > 0) state.BarsTotal = real;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve real bar count for run {RunId}", state.RunId);
            }
        });
    }

    public BacktestRunState? GetState(string runId) =>
        _registry.TryGet(runId, out var state) ? state : null;

    // iter-redesign P6.1: snapshot for a late-joining / reconnecting SignalR client so the live monitor
    // is never blank until the next throttled broadcast. Null when the run is unknown or already finalized
    // (those are served over AJAX). Status is normalized so the hub can pick RunProgress vs RunCompleted.
    public RunProgress? GetCurrentProgress(string runId)
    {
        var state = GetState(runId);
        if (state is null) return null;
        var status = state.Status switch
        {
            "completed" or "completed-with-warnings" or "failed" or "cancelled" => state.Status,
            _ => "running",
        };
        return RunProgressProjector.Build(state, status);
    }

    public IReadOnlyList<BacktestRunState> GetAll() => _registry.All();

    public async Task<string> StartAsync(BacktestConfig cfg, CancellationToken ct)
    {
        var idemKey = cfg.CustomParams.GetValueOrDefault("IdempotencyKey");
        if (string.IsNullOrWhiteSpace(idemKey))
        {
            var state = Start(cfg);
            await Task.CompletedTask;
            return state.RunId;
        }

        if (_idempotencyKeys.TryGetValue(idemKey, out var existingRunId))
        {
            _logger.LogInformation("Idempotency key {Key} already used for run {RunId} — returning existing", idemKey, existingRunId);
            return existingRunId;
        }

        lock (_idempotencyLock)
        {
            if (_idempotencyKeys.TryGetValue(idemKey, out existingRunId))
            {
                _logger.LogInformation("Idempotency key {Key} already used for run {RunId} — returning existing", idemKey, existingRunId);
                return existingRunId;
            }

            var state = Start(cfg);
            _idempotencyKeys[idemKey] = state.RunId;

            if (_idempotencyKeys.Count > MaxIdempotencyKeys)
            {
                _logger.LogWarning("Idempotency key dictionary exceeded {Max} entries — clearing", MaxIdempotencyKeys);
                _idempotencyKeys.Clear();
            }

            return state.RunId;
        }
    }

    public void Cancel(string runId)
    {
        if (!_registry.TryGet(runId, out var state))
            return;

        // P2.1 (F8): idempotent + truthful. A double-cancel or a cancel of an already-terminal run is a
        // no-op (the state machine forbids leaving a terminal state). We do NOT stamp `cancelled` here —
        // the run is still finalizing; its own finalize path makes the truthful terminal transition. We
        // record intent + trip the token, then best-effort kill the ctrader-cli tree so a live cTrader run
        // stops promptly instead of hanging until the 30-min linked timeout.
        if (RunStateMachine.IsTerminal(state.Status))
        {
            _logger.LogInformation("Cancel ignored for {RunId}: already terminal ({Status})", runId, state.Status);
            return;
        }

        state.CancelRequested = true;
        _logger.LogInformation("Cancel requested for {RunId} (status={Status})", runId, state.Status);
        state.CancellationSource?.Cancel();

        if (string.Equals(state.Venue, "ctrader", StringComparison.OrdinalIgnoreCase))
        {
            // The run's token was just cancelled, which makes CliWrap tree-kill the owned ctrader-cli.
            // This is the prompt, owned-PID backstop: reap ONLY this run's process (never by image name),
            // so a cancel of one run cannot touch a sibling parallel run/download or another worktree.
            _ = Task.Run(() => _owner.ReapByTag($"run:{runId}", "cancel"));
        }
    }

    /// <summary>
    /// P2.1 (F8): the SINGLE guarded writer for a run's lifecycle status. Every status change in the
    /// orchestrator routes through here so an illegal jump (or a stray assignment) can never bypass
    /// <see cref="RunStateMachine"/>. Non-throwing: an illegal transition from a non-terminal state is
    /// logged + journalled as a warning (a real ordering bug to fix), while any transition out of a
    /// terminal state is a benign idempotent no-op (double-cancel, teardown after completion).
    /// </summary>
    private void TransitionRun(BacktestRunState state, string to)
    {
        var from = state.Status;
        switch (RunStateMachine.Classify(from, to))
        {
            case RunStateMachine.TransitionKind.Legal:
                state.Status = to;
                _logger.LogDebug("Run {RunId} {From}->{To}", state.RunId, from, to);
                return;

            case RunStateMachine.TransitionKind.IdempotentNoOp:
                // Re-entering the current state (e.g. an OperationCanceled that lands while already
                // finalizing) or leaving a terminal (double-cancel, post-completion teardown). Not a bug —
                // do NOT warn/journal, or the LIFECYCLE flag stops meaning "real ordering violation".
                _logger.LogDebug("Run {RunId} transition {From}->{To} ignored (no-op)", state.RunId, from, to);
                return;

            default:
                _logger.LogWarning("Run {RunId} ILLEGAL transition {From}->{To} rejected; status unchanged", state.RunId, from, to);
                _journal.Write(state.RunId, "LIFECYCLE", $"illegal transition {from}->{to} rejected");
                return;
        }
    }

    public void SetSpeed(string runId, float speed)
    {
        if (!_registry.TryGet(runId, out var state)) return;
        speed = Math.Clamp(speed, 0f, 10f);
        state.Speed = speed;
        if (state.TapeAdapter is { } tape)
            tape.Speed = speed;
    }

    public async Task StopAllAsync()
    {
        _dequeueCts.Cancel();

        // Cancel all queued runs (they never started, so mark them cancelled directly).
        while (_queue.TryDequeue(out var queued))
        {
            if (_registry.TryGet(queued.RunId, out var qs))
            {
                TransitionRun(qs, RunStateMachine.Cancelled);
                qs.CancellationSource?.Cancel();
                EnqueueLog(queued.RunId, qs.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Cancelled (shutdown).");
                _ = _records.WriteEndRecordAsync(queued.RunId, queued.Config, qs.StartedAt,
                    new BacktestResult { RunId = queued.RunId, ExitCode = 0, ErrorMessage = "Cancelled (shutdown)." },
                    RunTradeStats.Empty, effectiveConfigJson: null, status: qs.Status);
            }
        }

        foreach (var state in _registry.All())
            state.CancellationSource?.Cancel();

        var tasks = _registry.All()
            .Select(s => s.RunTask)
            .Where(t => t is not null)
            .ToArray();

        if (tasks.Length > 0)
            await Task.WhenAll(tasks!);

        if (_dequeueTask is not null)
            await _dequeueTask;
    }

    private async Task RunAsync(string runId, BacktestConfig cfg, CancellationToken ct)
    {
        var state = _registry.GetRequired(runId);
        var startedAt = state.StartedAt;
        string? effectiveConfigJson = null;
        bool finalized = false;

        try
        {
            effectiveConfigJson = await _configAssembler.ResolveEffectiveConfigJsonAsync(cfg);
            await _records.WriteStartRecordAsync(runId, cfg, startedAt, effectiveConfigJson, status: RunStateMachine.Running);

            state.EffectiveConfigJson = effectiveConfigJson;
            state.RunPlanJson = cfg.CustomParams.GetValueOrDefault("RunRows") ?? "[]";

            TransitionRun(state, RunStateMachine.Running);
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {runId}...");

            BacktestResult result;

            var compareBoth = string.Equals(cfg.CustomParams.GetValueOrDefault("Compare"), "both", StringComparison.OrdinalIgnoreCase);

            if (compareBoth)
            {
                var comparePairId = Guid.NewGuid().ToString("N")[..8];
                result = await RunCompareBothAsync(runId, cfg, comparePairId, state, ct);
            }
            else
            {
                // The pluggable venue seam: the registry picks the runner for the run's "Venue"
                // selection (unknown/empty => replay). The runner executes the engine leg only;
                // the finalize below stays venue-agnostic.
                var runner = _venues.Resolve(cfg.CustomParams.GetValueOrDefault("Venue"));
                EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] {runner.StartLogLine}");
                result = await runner.ExecuteAsync(runId, cfg, state, ct);
            }

            finalized = await FinalizeRunAsync(runId, cfg, state, result, effectiveConfigJson, ct);
        }
        catch (OperationCanceledException)
        {
            // T9: the run was cancelled (user Cancel, the 30-min linked timeout, or host/stream teardown
            // at/near completion). Trades were persisted during the run, so this is NOT a failure — finalize
            // with the trades-so-far and an info log instead of scaring the user with a "failed" + error.
            var tradeStats = await _records.GetTradeStatsAsync(runId, cfg.Balance);
            var userCancelled = state.CancelRequested;
            TransitionRun(state, RunStateMachine.Finalizing);
            TransitionRun(state, userCancelled ? RunStateMachine.Cancelled : RunStateMachine.Completed);
            state.Error = null;
            var cancelResult = new BacktestResult
            {
                RunId = runId,
                ExitCode = 0,
                NetProfit = tradeStats.NetProfit,
                MaxDrawdownPct = tradeStats.MaxDrawdownPct,
                TotalTrades = tradeStats.TotalTrades,
                WinningTrades = tradeStats.WinningTrades,
                WinRatePct = tradeStats.WinRatePct,
            };
            state.Result = cancelResult;
            EnqueueLog(runId, state.LogLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Run {state.Status} ({tradeStats.TotalTrades} trades saved).");
            _logger.LogInformation("Backtest {RunId} ended via cancellation; status={Status} trades={Trades}",
                runId, state.Status, tradeStats.TotalTrades);
            finalized = await _records.WriteEndRecordAsync(runId, cfg, startedAt, cancelResult, tradeStats, effectiveConfigJson, status: state.Status);
        }
        catch (Exception ex)
        {
            TransitionRun(state, RunStateMachine.Failed);
            state.Error = ex.Message;
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Error: {ex.Message}");
            _logger.LogError(ex, "Backtest {RunId} failed", runId);

            var tradeStats = await _records.GetTradeStatsAsync(runId, cfg.Balance);

            finalized = await _records.WriteEndRecordAsync(runId, cfg, startedAt,
                new BacktestResult { RunId = runId, ExitCode = 1, ErrorMessage = ex.Message },
                tradeStats, effectiveConfigJson, status: state.Status);
        }
        finally
        {
            // P1: freeze the cache so completed/failed/cancelled runs return stable snapshots
            // (the getters rebuild only while running; MarkCompleted makes them freeze-once).
            _runDataCache?.MarkCompleted(runId);

            // iter-redesign P4.1: guarantee a terminal write. If WriteEndRecordAsync failed in the
            // try/catch above (or was never reached), do a last-ditch terminal write here so the run row
            // never stays at ExitCode=-1 / CompletedAtUtc=DateTime.MinValue.
            if (!finalized)
            {
                try
                {
                    var tradeStats = await _records.GetTradeStatsAsync(runId, cfg.Balance);
                    var terminalResult = state.Result ?? new BacktestResult
                    {
                        RunId = runId,
                        ExitCode = state.Status switch { "failed" => 1, _ => 0 },
                        ErrorMessage = state.Error,
                    };
                    await _records.WriteEndRecordAsync(runId, cfg, startedAt, terminalResult, tradeStats, effectiveConfigJson);
                }
                catch (Exception finalEx)
                {
                    _logger.LogError(finalEx, "P4.1 finally-net: terminal write for {RunId} also failed", runId);
                }
            }

            var doneJson = JsonSerializer.Serialize(
                new { done = true, status = state.Status, error = state.Error });
            _progressStore.GetWriter(runId).TryWrite(doneJson);
            _progressStore.Complete(runId);

            // iter-21 U1 — terminal frame, always delivered (bypasses the throttle).
            // iter-38 B7 / T9: pass the actual status so a user-cancelled run is reported as
            // "cancelled", not "completed" (the old ternary only distinguished "failed").
            _broadcaster.PublishDone(RunProgressProjector.Build(state, state.Status switch
            {
                "failed" => "failed",
                "cancelled" => "cancelled",
                _ => "completed"
            }));

            _broadcaster.RemoveRun(runId);
            _registry.Remove(runId);
            _memoryCache?.Remove(RunsListCacheKey);
        }
    }

    /// <summary>
    /// THE finalize sequence for an engine leg that returned a result — the single copy shared by
    /// <see cref="RunAsync"/> and the compare-both cTrader child leg (which used to hand-maintain a
    /// drift-prone duplicate of this block). Sets the terminal state on <paramref name="state"/> and
    /// returns whether the end record was durably written.
    ///
    /// P2.1 (F8): enters the transient `finalizing` state for the barrier + stats + end-record write.
    /// Every terminal transition goes finalizing->terminal through the state machine (never
    /// running->completed directly), so the lifecycle is enforced.
    /// </summary>
    private async Task<bool> FinalizeRunAsync(
        string runId, BacktestConfig cfg, BacktestRunState state, BacktestResult result,
        string? effectiveConfigJson, CancellationToken ct)
    {
        TransitionRun(state, RunStateMachine.Finalizing);

        // P0.3 (F6): trade-persistence integrity barrier. BEFORE computing stats, reconcile the run's
        // journalled closes against persisted TradeResults and backfill any that were lost (the audited
        // BTC scenario: fills journalled, venue killed before closes settled → 0 TradeResults, reported
        // TotalTrades=0). A shortfall attaches a TRADES_LOST warning → completed-with-warnings; the
        // backfill happens first so GetTradeStatsAsync counts the restored trades.
        // F19 (R0.1): scope to cTrader venue only — the barrier's journal pairing assumes cTrader's
        // OrderFilled close-fill shape; tape/replay venues produce a different journal shape that
        // triggers false-positive TRADES_PARTIALLY_UNRECONSTRUCTABLE warnings on perfectly healthy runs.
        if (result.Success && string.Equals(state.Venue, "ctrader", StringComparison.OrdinalIgnoreCase))
            await RunTradePersistenceBarrierAsync(runId, state, ct);

        var tradeStats = await _records.GetTradeStatsAsync(runId, cfg.Balance);

        result = result with
        {
            NetProfit = tradeStats.NetProfit,
            MaxDrawdownPct = tradeStats.MaxDrawdownPct,
            TotalTrades = tradeStats.TotalTrades,
            WinningTrades = tradeStats.WinningTrades,
            WinRatePct = tradeStats.WinRatePct,
        };
        // P0.2 (F5, Q5): fold any teardown/persistence warnings collected during the run (e.g. the
        // in-process cTrader leg's transport teardown) into the result. A run that produced a
        // complete result but hit a teardown fault is `completed-with-warnings`, never `failed`.
        var warningsJson = RunRecordStore.MergeWarningsJson(state, result.WarningsJson);
        result = result with { WarningsJson = warningsJson };

        state.Result = result;
        TransitionRun(state, result.Success
            ? (RunStatusResolver.HasWarnings(warningsJson)
                ? RunStateMachine.CompletedWithWarnings
                : RunStateMachine.Completed)
            : RunStateMachine.Failed);
        state.Error = result.ErrorMessage;

        EnqueueLog(runId, state.LogLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Done. Status={state.Status} Trades={result.TotalTrades} PnL={result.NetProfit:N2} DD={result.MaxDrawdownPct:P1} Gross={tradeStats.GrossPnL:N2} Comm={tradeStats.CommissionTotal:N2} Swap={tradeStats.SwapTotal:N2}");

        return await _records.WriteEndRecordAsync(runId, cfg, state.StartedAt, result, tradeStats, effectiveConfigJson, status: state.Status);
    }

    // P0.2 (F5, Q5): record a teardown/persistence anomaly against the run without failing it.
    private void AddTeardownWarning(string runId, string code, string detail)
    {
        if (_registry.TryGet(runId, out var state))
            state.Warnings.Enqueue(new RunWarning(code, detail, DateTime.UtcNow));
        _logger.LogWarning("RUN_WARNING|run={RunId}|code={Code}|detail={Detail}", runId, code, detail);
    }

    private async Task<BacktestResult> RunCompareBothAsync(
        string runId, BacktestConfig cfg, string comparePairId, BacktestRunState state, CancellationToken ct)
    {
        var logLines = state.LogLines;
        EnqueueLog(runId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] Compare mode — running tape first...");

        // Tag the parent with the pair id
        if (!cfg.CustomParams.ContainsKey("ComparePairId"))
            cfg.CustomParams["ComparePairId"] = comparePairId;

        // Step 1: tape run — use a dedicated copy to avoid mutating cfg
        var tapeCfg = cfg with
        {
            CustomParams = new Dictionary<string, string>(cfg.CustomParams)
            {
                ["Venue"] = "tape",
            },
        };
        var tapeResult = await _venues.Resolve("tape").ExecuteAsync(runId, tapeCfg, state, ct);

        if (!tapeResult.Success)
        {
            EnqueueLog(runId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] Tape run failed — skipping cTrader compare leg.");
            return tapeResult;
        }

        // Step 2: cTrader run — new runId, same config, tagged with the pair.
        // Must NOT inherit Compare="both" or RunAsync will recurse into RunCompareBothAsync.
        var ctraderRunId = Guid.NewGuid().ToString("N")[..8];
        var ctraderCfg = cfg with
        {
            RunId = ctraderRunId,
            CustomParams = new Dictionary<string, string>(cfg.CustomParams)
            {
                ["Venue"] = "ctrader",
                ["ComparePairId"] = comparePairId,
                ["ParentRunId"] = runId,
            },
        };
        ctraderCfg.CustomParams.Remove("Compare");

        // Manually register state without spawning a duplicate RunAsync —
        // Start() would fire RunAsync which would see Venue="ctrader" and run
        // the cTrader venue runner again, racing with our own call below.
        var ctraderState = new BacktestRunState
        {
            RunId = ctraderRunId,
            Venue = "ctrader",
            Symbol = cfg.Symbol,
            Period = cfg.Period,
            BarsTotal = await ResolveBarCountAsync(ctraderCfg, ct),
            GovernorEnabled = ctraderCfg.CustomParams.GetValueOrDefault("GovernorEnabled") != "false",
            RegimeEnabled = ctraderCfg.CustomParams.GetValueOrDefault("DisableRegime") != "true",
            CommissionPerMillion = (double)ctraderCfg.CommissionPerMillion,
            SpreadPips = (double)ctraderCfg.SpreadPips,
            InitialBalance = ctraderCfg.Balance,
            BacktestFrom = ctraderCfg.Start,
            BacktestTo = ctraderCfg.End,
            RiskProfileId = ctraderCfg.CustomParams.GetValueOrDefault("RiskProfileId"),
            StartedAt = DateTime.UtcNow,
        };
        ctraderState.CancellationSource = new CancellationTokenSource();
        _registry.Register(ctraderState);

        // F18 (R0.1): write a start record to DB immediately so the child run is visible from spawn
        // moment, even if a crash prevents WriteEndRecordAsync from running later.
        await _records.WriteStartRecordAsync(ctraderRunId, ctraderCfg, ctraderState.StartedAt, null);

        EnqueueLog(ctraderRunId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] Compare mode — running cTrader leg {ctraderRunId}...");

        try
        {
            TransitionRun(ctraderState, RunStateMachine.Running);
            EnqueueLog(ctraderRunId, ctraderState.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running via in-process cTrader engine...");
            var ctraderResult = await _venues.Resolve("ctrader").ExecuteAsync(ctraderRunId, ctraderCfg, ctraderState, ct);

            // This leg is the one every parity comparison is measured against. It used to hand-maintain
            // a parallel copy of RunAsync's finalize block (a copy that had already drifted once: no
            // barrier, no warnings merge, no persisted Status). It now shares the single FinalizeRunAsync.
            await FinalizeRunAsync(ctraderRunId, ctraderCfg, ctraderState, ctraderResult, effectiveConfigJson: null, ct);

            var finalResult = ctraderState.Result!;
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Compare complete. Tape={runId} ({tapeResult.TotalBars} bars) / cTrader={ctraderRunId} ({finalResult.TotalBars} bars / {finalResult.TotalTrades}t) — reconcile: GET /api/backtest/analytics/reconcile?left={runId}&right={ctraderRunId}");
        }
        catch (OperationCanceledException)
        {
            TransitionRun(ctraderState, RunStateMachine.Cancelled);
            ctraderState.Error = "Cancelled";
            EnqueueLog(ctraderRunId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] cTrader leg cancelled.");
            await WriteChildTerminalRecordAsync(ctraderRunId, ctraderCfg, ctraderState, exitCode: 0, errorMessage: null);
        }
        catch (Exception ex)
        {
            TransitionRun(ctraderState, RunStateMachine.Failed);
            ctraderState.Error = ex.Message;
            EnqueueLog(ctraderRunId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] cTrader leg failed: {ex.Message}");
            await WriteChildTerminalRecordAsync(ctraderRunId, ctraderCfg, ctraderState, exitCode: 1, errorMessage: ex.Message);
        }
        finally
        {
            ctraderState.CancellationSource?.Dispose();

            // The child run is finalized (terminal record written above) — mirror RunAsync's finalize
            // cleanup for the child id. Before this block the child's BacktestRunState stayed Register'ed
            // forever (one leaked state + broadcaster throttle entry per compare-both run), and a live
            // viewer of the child leg never received the terminal frame.
            _broadcaster.PublishDone(RunProgressProjector.Build(ctraderState, ctraderState.Status switch
            {
                "failed" => "failed",
                "cancelled" => "cancelled",
                _ => "completed"
            }));
            _runDataCache?.MarkCompleted(ctraderRunId);
            _broadcaster.RemoveRun(ctraderRunId);
            _registry.Remove(ctraderRunId);
            _memoryCache?.Remove(RunsListCacheKey);
        }

        return tapeResult;
    }

    /// <summary>Terminal end record for a compare-both child leg that ended via cancel/exception. The
    /// child used to get NO end record on these paths, leaving its row at ExitCode=-1 /
    /// CompletedAtUtc=MinValue forever (RunAsync has the P4.1 finally-net for the parent; this is the
    /// child leg's equivalent). Trades persisted during the run are counted as-is; cancellation is not
    /// an error (T9), so the cancel path passes a null <paramref name="errorMessage"/>.</summary>
    private async Task WriteChildTerminalRecordAsync(
        string runId, BacktestConfig cfg, BacktestRunState state, int exitCode, string? errorMessage)
    {
        try
        {
            var tradeStats = await _records.GetTradeStatsAsync(runId, cfg.Balance);
            await _records.WriteEndRecordAsync(runId, cfg, state.StartedAt,
                new BacktestResult { RunId = runId, ExitCode = exitCode, ErrorMessage = errorMessage },
                tradeStats, effectiveConfigJson: null, status: state.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal write for compare-both child {RunId} failed", runId);
        }
    }

    /// <summary>Acquire a tape-lane slot as an <see cref="IDisposable"/> lease, mirroring the owner lane
    /// so the dequeue loop treats both venues uniformly.</summary>
    private async Task<IDisposable> AcquireTapeLaneAsync(CancellationToken ct)
    {
        await _tapeSemaphore.WaitAsync(ct);
        return new SemaphoreLease(_tapeSemaphore);
    }

    private sealed class SemaphoreLease(SemaphoreSlim sem) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) sem.Release();
        }
    }

    // backfills any lost trades from the journal and, on a shortfall, records a TRADES_LOST warning so the
    // run finalizes `completed-with-warnings` (P0.2 plumbing) instead of silently reporting fewer trades.
    private async Task RunTradePersistenceBarrierAsync(string runId, BacktestRunState state, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var barrier = scope.ServiceProvider
                .GetRequiredService<TradingEngine.Infrastructure.Persistence.TradePersistenceBarrier>();
            var recon = await barrier.ReconcileAndBackfillAsync(runId, ct);

            if (recon.Failed)
            {
                AddTeardownWarning(runId, "TRADE_BARRIER_FAILED", recon.FailureDetail ?? "reconciliation failed");
                return;
            }

            // F6-R (P7.6): the barrier now attempts to reconstruct PublishTradeClosed from paired
            // OrderFilled open+close events + proposals. JournalCloseFills counts only the close
            // fills that COULD NOT be reconstructed (missing open fill or proposal in the journal).
            // When ALL were unreconstructable (Persisted+Backfilled==0 && JournalCloseFills>0), the
            // Unreconstructable flag is true. Partial recovery is also surfaced below.
            if (recon.Unreconstructable)
            {
                AddTeardownWarning(runId, $"TRADES_UNRECONSTRUCTABLE:{recon.JournalCloseFills}",
                    $"{recon.JournalCloseFills} venue close-fill(s) could not be reconstructed: missing open-fill or proposal data in the journal");
                return;
            }

            // Partial recovery: some close fills were reconstructed (Persisted+Backfilled>0) but others
            // could not be paired. Surface the gap.
            if (recon.JournalCloseFills > 0)
            {
                AddTeardownWarning(runId, $"TRADES_PARTIALLY_UNRECONSTRUCTABLE:{recon.JournalCloseFills}",
                    $"{recon.JournalCloseFills} close-fill(s) could not be paired with open-fill + proposal data; economics not recovered for those");
            }

            if (recon.HasLoss)
            {
                var stillMissing = recon.Expected - recon.Persisted - recon.Backfilled;
                AddTeardownWarning(runId, $"TRADES_LOST:{recon.Expected}:{recon.Persisted}",
                    $"journal had {recon.Expected} closes, {recon.Persisted} persisted; backfilled {recon.Backfilled}, still-missing {stillMissing}");
            }
        }
        catch (Exception ex)
        {
            AddTeardownWarning(runId, "TRADE_BARRIER_FAILED", ex.Message);
        }
    }

}
