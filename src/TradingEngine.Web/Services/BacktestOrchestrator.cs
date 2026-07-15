using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using TradingEngine.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
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

    public sealed class BacktestOrchestrator : IBacktestCommandService
{
    // F34: the currency every money figure in this engine is denominated in — pip values, risk sizing,
    // FTMO limits and the whole tape. A venue account in any other currency is not comparable to a tape
    // run, so the run fails instead of silently applying an FX factor to everything. Configurable via
    // Account:Currency: re-denominating to GBP is this value plus the GBPUSD data the rate feed loads.
    private const string DefaultAccountCurrency = "EUR";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BacktestOrchestrator> _logger;
    private readonly BacktestProgressStore _progressStore;
    private readonly BacktestJournal _journal;
    private readonly IConfiguration _configuration;
    private readonly CTraderConnectionOptions _ctraderOptions;
    private readonly RunProgressBroadcaster _broadcaster;
    private readonly EffectiveConfigResolver _configResolver;
    private readonly IRunDataCache? _runDataCache;
    private readonly IMemoryCache? _memoryCache;
    private readonly ConcurrentDictionary<string, BacktestRunState> _runs = new();
    private readonly ConcurrentDictionary<string, string> _idempotencyKeys = new();
    private readonly object _idempotencyLock = new();

    private const string RunsListCacheKey = "runs:all";
    private const int MaxIdempotencyKeys = 10_000;

    private readonly int _maxTapeConcurrency;
    private readonly SemaphoreSlim _tapeSemaphore;
    // X4: cTrader work no longer has a private serial semaphore here — it shares the one
    // CTraderProcessOwner lane (bounded parallel, shared with market-data downloads).
    private readonly CTraderProcessOwner _owner;
    private readonly ConcurrentQueue<(string RunId, BacktestConfig Config)> _queue = new();
    private readonly CancellationTokenSource _dequeueCts = new();
    private readonly Task? _dequeueTask;

    public int QueuedCount => _queue.Count;
    public int RunningTapeCount => _runs.Values.Count(r => r.Status == "running" && !string.Equals(r.Venue, "ctrader", StringComparison.OrdinalIgnoreCase));
    public int RunningCtraderCount => _runs.Values.Count(r => r.Status == "running" && string.Equals(r.Venue, "ctrader", StringComparison.OrdinalIgnoreCase));

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

    private sealed record TradeStats(decimal NetProfit, decimal GrossPnL, decimal CommissionTotal, decimal SwapTotal, decimal MaxDrawdownPct, int TotalTrades, int WinningTrades, double WinRatePct);

    // BacktestRunState + RunWarning live in Runs/BacktestRunState.cs; progress projection + the
    // funnel tally live in Runs/RunProgressProjector.cs (both were nested here pre-refactor).
    public BacktestOrchestrator(
        IServiceScopeFactory scopeFactory,
        BacktestProgressStore progressStore,
        BacktestJournal journal,
        IConfiguration configuration,
        IOptions<CTraderConnectionOptions> ctraderOptions,
        RunProgressBroadcaster broadcaster,
        EffectiveConfigResolver configResolver,
        ILogger<BacktestOrchestrator> logger,
        CTraderProcessOwner owner,
        IRunDataCache? runDataCache = null,
        IMemoryCache? memoryCache = null)
    {
        _scopeFactory = scopeFactory;
        _progressStore = progressStore;
        _journal = journal;
        _configuration = configuration;
        _ctraderOptions = ctraderOptions.Value;
        _broadcaster = broadcaster;
        _configResolver = configResolver;
        _owner = owner;
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

    // The New-Backtest strategy picker arrives as a comma-separated "StrategyIds" custom param
    // (empty/absent = run all configured strategies).
    private static string[] ParseStrategyIds(BacktestConfig cfg) =>
        cfg.CustomParams.TryGetValue("StrategyIds", out var ids) && !string.IsNullOrWhiteSpace(ids)
            ? ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

    // iter-strategy-system P1 (D3): the row-based builder serializes its enabled rows (as RunPlanEntry, incl.
    // per-row PackId) into CustomParams["RunRows"]. Absent/blank ⇒ legacy cross-product path.
    private static List<RunPlanEntry> ParseRunPlanEntries(BacktestConfig cfg)
    {
        if (!cfg.CustomParams.TryGetValue("RunRows", out var json) || string.IsNullOrWhiteSpace(json))
            return [];
        try { return JsonSerializer.Deserialize<List<RunPlanEntry>>(json) ?? []; }
        catch (Exception ex)
        {
            // A malformed RunRows must not silently run an empty plan that looks like "all strategies".
            throw new InvalidOperationException("Invalid RunRows payload.", ex);
        }
    }

    private static RunPlan BuildRunPlan(string[] strategyIds, string[] symbols, string[] periods)
    {
        var entries = new List<RunPlanEntry>();
        foreach (var sid in strategyIds)
        {
            foreach (var sym in symbols)
            {
                foreach (var pf in periods)
                {
                    entries.Add(new RunPlanEntry(sid, sym, pf));
                }
            }
        }
        return new RunPlan(entries);
    }

    private static Timeframe ParseTimeframe(string period) => period.ToUpperInvariant() switch
    {
        "M1" => Timeframe.M1,
        "M5" => Timeframe.M5,
        "M15" => Timeframe.M15,
        "M30" => Timeframe.M30,
        "H1" => Timeframe.H1,
        "H4" => Timeframe.H4,
        "D1" => Timeframe.D1,
        _ => Timeframe.H1,
    };

    public BacktestRunState Start(BacktestConfig cfg)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        cfg = cfg with { RunId = runId };
        var state = new BacktestRunState
        {
            RunId = runId,
            Symbol = cfg.Symbol,
            Period = cfg.Period,
            BarsTotal = EstimateBarCount(cfg.Start, cfg.End, cfg.Period),
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
        _runs[runId] = state;
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
                await WriteStartRecordAsync(runId, cfg, state.StartedAt, effectiveConfigJson: null,
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
        if (!_runs.TryGetValue(runId, out var state)) return;
        if (state.Status != RunStateMachine.Queued) return;

        var isCtrader = ResolveUseCtrader(cfg.CustomParams.GetValueOrDefault("Venue"));
        // X4: cTrader admission goes through the shared owner lane (parallel with downloads, bounded);
        // tape keeps its own semaphore.
        var availableSlots = isCtrader ? _owner.AvailableSlots : _tapeSemaphore.CurrentCount;
        if (availableSlots == 0) return;

        if (!_queue.TryDequeue(out var dequeued)) return;
        if (!_runs.TryGetValue(dequeued.RunId, out state)) return;
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
                _ = WriteEndRecordAsync(dequeued.RunId, dequeued.Config, state.StartedAt,
                    new BacktestResult { RunId = dequeued.RunId, ExitCode = 0, ErrorMessage = "Cancelled while waiting in queue." },
                    new TradeStats(0, 0, 0, 0, 0, 0, 0, 0), effectiveConfigJson: null, status: state.Status);
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
                var tf = ParseTimeframe(cfg.Period);
                var count = await store.CountBarsAsync(new Symbol(cfg.Symbol), tf, cfg.Start, cfg.End, ct);
                if (count > 0) return count;
            }
        }
        catch { }

        return EstimateBarCount(cfg.Start, cfg.End, cfg.Period);
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

    private static int EstimateBarCount(DateTime start, DateTime end, string period)
    {
        var duration = end - start;
        var minutes = period.ToUpperInvariant() switch
        {
            "M1" => 1.0,
            "M5" => 5.0,
            "M15" => 15.0,
            "M30" => 30.0,
            "H1" => 60.0,
            "H4" => 240.0,
            "D1" => 1440.0,
            _ => 60.0,
        };
        return (int)(duration.TotalMinutes / minutes);
    }

    /// <summary>
    /// iter-38 D6 / P0-B1: resolve which venue a run uses from its optional "Venue" selection. cTrader is
    /// EXPLICIT opt-in (<c>"ctrader"</c>); the default (no/empty selection) and any unknown value route to
    /// the credential-free replay venue. Pure + config-free so venue routing is deterministically testable.
    /// </summary>
    public static bool ResolveUseCtrader(string? venue) => venue?.ToLowerInvariant() switch
    {
        "ctrader" => true,
        "replay" or "sim" or "simulated" => false,
        _ => false,
    };

    public BacktestRunState? GetState(string runId) =>
        _runs.TryGetValue(runId, out var state) ? state : null;

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

    public IReadOnlyList<BacktestRunState> GetAll() => _runs.Values.ToList();

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
        if (!_runs.TryGetValue(runId, out var state))
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
        if (!_runs.TryGetValue(runId, out var state)) return;
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
            if (_runs.TryGetValue(queued.RunId, out var qs))
            {
                TransitionRun(qs, RunStateMachine.Cancelled);
                qs.CancellationSource?.Cancel();
                EnqueueLog(queued.RunId, qs.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Cancelled (shutdown).");
                _ = WriteEndRecordAsync(queued.RunId, queued.Config, qs.StartedAt,
                    new BacktestResult { RunId = queued.RunId, ExitCode = 0, ErrorMessage = "Cancelled (shutdown)." },
                    new TradeStats(0, 0, 0, 0, 0, 0, 0, 0), effectiveConfigJson: null, status: qs.Status);
            }
        }

        foreach (var (_, state) in _runs)
            state.CancellationSource?.Cancel();

        var tasks = _runs.Values
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
        var state = _runs[runId];
        var startedAt = state.StartedAt;
        string? effectiveConfigJson = null;
        bool finalized = false;

        try
        {
            effectiveConfigJson = await ResolveEffectiveConfigJsonAsync(cfg);
            await WriteStartRecordAsync(runId, cfg, startedAt, effectiveConfigJson, status: RunStateMachine.Running);

            state.EffectiveConfigJson = effectiveConfigJson;
            state.RunPlanJson = cfg.CustomParams.GetValueOrDefault("RunRows") ?? "[]";

            TransitionRun(state, RunStateMachine.Running);
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {runId}...");

            BacktestResult result;

            var useCtader = ResolveUseCtrader(cfg.CustomParams.GetValueOrDefault("Venue"));
            var compareBoth = string.Equals(cfg.CustomParams.GetValueOrDefault("Compare"), "both", StringComparison.OrdinalIgnoreCase);

            if (compareBoth)
            {
                var comparePairId = Guid.NewGuid().ToString("N")[..8];
                result = await RunCompareBothAsync(runId, cfg, comparePairId, state.LogLines, ct);
            }
            else if (useCtader)
            {
                EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running via in-process cTrader engine...");
                result = await RunEngineNetMqAsync(runId, cfg, state.LogLines, ct);
            }
            else
            {
                EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running engine replay...");
                result = await RunEngineReplayAsync(runId, cfg, state.LogLines, ct);
            }

            // P2.1 (F8): the engine loop has returned a result; enter the transient `finalizing` state for
            // the barrier + stats + end-record write. Every terminal transition below goes finalizing->terminal
            // through the state machine (never running->completed directly), so the lifecycle is enforced.
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

            var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);

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
            var warningsJson = MergeWarningsJson(state, result.WarningsJson);
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

            finalized = await WriteEndRecordAsync(runId, cfg, startedAt, result, tradeStats, effectiveConfigJson, status: state.Status);
        }
        catch (OperationCanceledException)
        {
            // T9: the run was cancelled (user Cancel, the 30-min linked timeout, or host/stream teardown
            // at/near completion). Trades were persisted during the run, so this is NOT a failure — finalize
            // with the trades-so-far and an info log instead of scaring the user with a "failed" + error.
            var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);
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
            finalized = await WriteEndRecordAsync(runId, cfg, startedAt, cancelResult, tradeStats, effectiveConfigJson, status: state.Status);
        }
        catch (Exception ex)
        {
            TransitionRun(state, RunStateMachine.Failed);
            state.Error = ex.Message;
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Error: {ex.Message}");
            _logger.LogError(ex, "Backtest {RunId} failed", runId);

            var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);

            finalized = await WriteEndRecordAsync(runId, cfg, startedAt,
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
                    var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);
                    var terminalResult = state.Result ?? new BacktestResult
                    {
                        RunId = runId,
                        ExitCode = state.Status switch { "failed" => 1, _ => 0 },
                        ErrorMessage = state.Error,
                    };
                    await WriteEndRecordAsync(runId, cfg, startedAt, terminalResult, tradeStats, effectiveConfigJson);
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
            _runs.TryRemove(runId, out _);
            _memoryCache?.Remove(RunsListCacheKey);
        }
    }

    private async Task WriteStartRecordAsync(string runId, BacktestConfig cfg, DateTime startedAt, string? effectiveConfigJson,
        string? status = null, int? queuePosition = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
            // iter-36 K6: content-address the run. DatasetId = hash of the data window spec (symbols/periods/
            // range); ConfigSetId = hash of the resolved effective config. Identical (DatasetId, ConfigSetId,
            // Seed) ⇒ a deterministic re-run; a duplicate keeps DatasetId, gets a new ConfigSetId + ParentRunId.
            var datasetSpec = $"{SymbolsJson(cfg.Symbols)}|{PeriodsJson(cfg.Periods)}|{cfg.Start:O}|{cfg.End:O}";
            var datasetId = TradingEngine.Infrastructure.ConfigSetHash.Compute(datasetSpec);
            // ConfigSetId = hash of EVERYTHING that determines behavior (ReplayModel): the resolved strategy
            // effective config PLUS the run's risk profile / strategy selection / per-strategy overrides — so
            // a duplicate that changes the risk profile gets a genuinely different ConfigSetId (K6).
            var configIdentity = System.Text.Json.JsonSerializer.Serialize(new
            {
                effective = effectiveConfigJson ?? "{}",
                riskProfileId = cfg.CustomParams.GetValueOrDefault("RiskProfileId"),
                strategyIds = cfg.CustomParams.GetValueOrDefault("StrategyIds"),
                overrides = cfg.CustomParams.GetValueOrDefault("StrategyOverrides"),
                // iter-38 PK3/R1: a pack or the regime-master change the run's behaviour, so they participate
                // in the ConfigSetId identity (different pack/regime ⇒ a genuinely different run, K6).
                usePackId = cfg.CustomParams.GetValueOrDefault("UsePackId"),
                perStrategyPacks = cfg.CustomParams.GetValueOrDefault("PerStrategyPackIds"),
                disableRegime = cfg.CustomParams.GetValueOrDefault("DisableRegime"),
                stripAddOns = cfg.CustomParams.GetValueOrDefault("StripAddOns"),
                // iter-strategy-system P1/P2: the row plan (per-row packs) + governor toggle change behaviour,
                // so they belong in the run's content address.
                runRows = cfg.CustomParams.GetValueOrDefault("RunRows"),
                governorEnabled = cfg.CustomParams.GetValueOrDefault("GovernorEnabled"),
                // iter-redesign P2.2: per-run protection toggle overrides change behaviour (a "Raw" run is a
                // genuinely different run from a guarded one), so they participate in the content address.
                dailyDdEnabled = cfg.CustomParams.GetValueOrDefault("DailyDdEnabled"),
                maxDdEnabled = cfg.CustomParams.GetValueOrDefault("MaxDdEnabled"),
                forceCloseOnBreachEnabled = cfg.CustomParams.GetValueOrDefault("ForceCloseOnBreachEnabled"),
                exposureEnabled = cfg.CustomParams.GetValueOrDefault("ExposureEnabled"),
                budgetEnabled = cfg.CustomParams.GetValueOrDefault("BudgetEnabled"),
                maxPositionsEnabled = cfg.CustomParams.GetValueOrDefault("MaxPositionsEnabled"),
                honestFills = cfg.CustomParams.GetValueOrDefault("HonestFills"),
                recordExcursions = cfg.CustomParams.GetValueOrDefault("RecordExcursions"),
                exitTimeframe = cfg.CustomParams.GetValueOrDefault("ExitTimeframe"),
            });
            var configSetId = TradingEngine.Infrastructure.ConfigSetHash.Compute(configIdentity);
            var parentRunId = cfg.CustomParams.GetValueOrDefault("ParentRunId");
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.MinValue,
                cfg.Symbol, cfg.Period, SymbolsJson(cfg.Symbols), PeriodsJson(cfg.Periods), cfg.Start, cfg.End,
                cfg.Balance, "", effectiveConfigJson ?? "{}", effectiveConfigJson,
                0, 0, 0, 0, 0, 0, 0, 0, -1, null,
                ReportJsonPath: null, DatasetId: datasetId, ConfigSetId: configSetId, Seed: 42,
                ParentRunId: string.IsNullOrWhiteSpace(parentRunId) ? null : parentRunId,
                RunPlanJson: cfg.CustomParams.GetValueOrDefault("RunRows") ?? "[]",
                Venue: cfg.CustomParams.GetValueOrDefault("Venue") ?? "replay",
                RiskProfileId: cfg.CustomParams.GetValueOrDefault("RiskProfileId"),
                GovernorEnabled: cfg.CustomParams.GetValueOrDefault("GovernorEnabled") != "false",
                RegimeEnabled: cfg.CustomParams.GetValueOrDefault("DisableRegime") != "true",
                CommissionPerMillion: cfg.CommissionPerMillion,
                SpreadPips: cfg.SpreadPips,
                ExplorationMode: cfg.CustomParams.GetValueOrDefault("ExplorationMode") == "true",
                RecordExcursions: cfg.CustomParams.GetValueOrDefault("RecordExcursions") == "true",
                Status: status,
                QueuePosition: queuePosition);
            await repo.SaveAsync(summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write start record for {RunId}", runId);
        }
    }

    private async Task<bool> WriteEndRecordAsync(
        string runId, BacktestConfig cfg, DateTime startedAt,
        BacktestResult result, TradeStats stats, string? effectiveConfigJson, string? status = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
            var wallElapsedMs = result.WallElapsedMs > 0
                ? result.WallElapsedMs
                : (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            var totalBars = result.TotalBars;
            var barsPerSec = result.BarsPerSec > 0
                ? result.BarsPerSec
                : wallElapsedMs > 0 ? totalBars / (wallElapsedMs / 1000.0) : 0;
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.UtcNow,
                cfg.Symbol, cfg.Period, SymbolsJson(cfg.Symbols), PeriodsJson(cfg.Periods), cfg.Start, cfg.End,
                cfg.Balance, result.AlgoHash, effectiveConfigJson ?? "{}", effectiveConfigJson,
                stats.NetProfit, stats.GrossPnL, stats.CommissionTotal, stats.SwapTotal, stats.MaxDrawdownPct,
                stats.TotalTrades, stats.WinningTrades, stats.WinRatePct,
                result.ExitCode, result.ErrorMessage,
                result.ReportJsonPath,
                WallElapsedMs: wallElapsedMs,
                BarsPerSec: barsPerSec,
                TotalBars: totalBars,
                WarningsJson: result.WarningsJson,
                ComparePairId: cfg.CustomParams.GetValueOrDefault("ComparePairId"),
                ParentRunId: cfg.CustomParams.GetValueOrDefault("ParentRunId"),
                ExplorationMode: cfg.CustomParams.GetValueOrDefault("ExplorationMode") == "true",
                RecordExcursions: cfg.CustomParams.GetValueOrDefault("RecordExcursions") == "true",
                Status: status);
            await repo.UpdateAsync(summary, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write end record for {RunId}", runId);
            return false;
        }
    }

    // P0.2 (F5, Q5): serialize the run's collected teardown/persistence warnings into a JSON array,
    // merging any warnings already carried on the result (e.g. from the cTrader leg). Returns null when
    // there are none, so a clean run keeps WarningsJson NULL and resolves to plain `completed`.
    private static string? MergeWarningsJson(BacktestRunState state, string? existingJson)
    {
        var warnings = new List<RunWarning>();

        if (RunStatusResolver.HasWarnings(existingJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<RunWarning>>(existingJson!);
                if (parsed is not null) warnings.AddRange(parsed);
            }
            catch { /* malformed prior warnings must never break finalization */ }
        }

        while (state.Warnings.TryDequeue(out var w))
            warnings.Add(w);

        return warnings.Count == 0 ? null : JsonSerializer.Serialize(warnings);
    }

    // P0.2 (F5, Q5): record a teardown/persistence anomaly against the run without failing it.
    private void AddTeardownWarning(string runId, string code, string detail)
    {
        if (_runs.TryGetValue(runId, out var state))
            state.Warnings.Enqueue(new RunWarning(code, detail, DateTime.UtcNow));
        _logger.LogWarning("RUN_WARNING|run={RunId}|code={Code}|detail={Detail}", runId, code, detail);
    }

    // P0.2 (F5, Q5): run one teardown step in isolation. A fault after a complete engine result becomes
    // a warning, never a propagated exception (which the outer catch would turn into `failed`).
    private async Task SafeTeardownStepAsync(string runId, string code, Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex) { AddTeardownWarning(runId, code, ex.Message); }
    }

    // Builds the engine's LoadedConfig from the DATABASE (canonical config source) rather than letting
    // the inner host re-read config/strategies/*.json. Strategy parameters, symbols, timeframe, regime
    // filter, order-entry and position-management all come from the seeded DB store, so what the New-
    // Backtest UI shows/edits is exactly what the engine evaluates. Risk profiles, prop-firm rules,
    // governor and sizing are also loaded from DB stores (seeded from JSON at startup).
    // iter-strategy-system P1: <paramref name="perPassPacks"/> drives per-row add-on packs (D3). When non-null
    // (the row-based builder), it is the strategy→packId map for ONE execution pass: each listed strategy is
    // force-enabled for the run (the user put it in a row, so a DB Enabled=false must not silently drop it)
    // and gets that row's pack — so the SAME strategy can carry DIFFERENT packs on different (symbol,tf) passes.
    // When null, the legacy global pack logic (UsePackId / PerStrategyPackIds) applies. The governor toggle
    // (D4) is honoured for both paths via CustomParams["GovernorEnabled"].
    private async Task<LoadedConfig> BuildLoadedConfigFromDbAsync(
        BacktestConfig cfg, IReadOnlyDictionary<string, string?>? perPassPacks = null)
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var baseConfig = new ConfigLoader(solutionRoot).LoadBase();

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IStrategyConfigStore>();
        var dbConfigs = await store.GetAllAsync(CancellationToken.None);

        // iter-redesign-ctrader P3.2: load DB risk profiles early so profileIsKnown checks BOTH the JSON
        // base config AND the DB store. Before this fix, only baseConfig.RiskProfiles was checked; a
        // "raw" profile seeded into the DB was invisible, so the strategy kept its stored (standard)
        // profile and the raw prop-firm toggles never loaded.
        var rpStore = scope.ServiceProvider.GetRequiredService<IRiskProfileStore>();
        var dbRiskProfiles = await rpStore.GetAllAsync(CancellationToken.None);
        var riskProfiles = dbRiskProfiles.Count > 0 ? dbRiskProfiles : baseConfig.RiskProfiles;

        var chosenProfile = cfg.CustomParams.GetValueOrDefault("RiskProfileId");
        var profileIsKnown = !string.IsNullOrWhiteSpace(chosenProfile)
            && riskProfiles.Any(r => r.Id == chosenProfile);

        var strategyConfigs = new List<StrategyConfigEntry>();
        {
            // iter-38 PK3 / D1: apply a named add-on pack over each strategy's own add-ons (per-strategy pack
            // wins over the global UsePackId; the pack REPLACES enrichments, baseline SL/TP stays — D4).
            var usePackId = cfg.CustomParams.GetValueOrDefault("UsePackId");
            var disableRegime = cfg.CustomParams.GetValueOrDefault("DisableRegime") == "true";   // iter-38 R1 run-master
            // iter-redesign P3.2 (D2): "no add-ons (raw)" mode — strip every add-on so the strategy runs its
            // baseline SL/TP only, with no breakeven/trailing/partial/ride/dynamic enrichment. Wins over any
            // pack so the owner can A/B raw vs add-on'd and watch the unmasked drawdown.
            var stripAddOns = cfg.CustomParams.GetValueOrDefault("StripAddOns") == "true";
            Dictionary<string, string>? perStrategyPacks = null;
            if (perPassPacks is null
                && cfg.CustomParams.TryGetValue("PerStrategyPackIds", out var ppJson) && !string.IsNullOrWhiteSpace(ppJson))
            {
                try { perStrategyPacks = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(ppJson); }
                catch (Exception ex) { _logger.LogWarning(ex, "Bad PerStrategyPackIds JSON — ignoring"); }
            }

            var packStore = scope.ServiceProvider.GetRequiredService<IAddOnPackStore>();
            var packCache = new Dictionary<string, AddOnPack?>();

            foreach (var c0 in dbConfigs)
            {
                var c = profileIsKnown ? c0 with { RiskProfileId = chosenProfile! } : c0;

                // iter-strategy-system P1 (D3): a row-selected strategy runs for this pass regardless of its
                // stored Enabled flag; routing to the right pass is the RunPlan's job (StrategyBankService).
                if (perPassPacks is not null && perPassPacks.ContainsKey(c.Id))
                    c = c with { Enabled = true };

                var packId = perPassPacks is not null
                    ? perPassPacks.GetValueOrDefault(c.Id)
                    : perStrategyPacks?.GetValueOrDefault(c.Id) ?? usePackId;
                if (!string.IsNullOrWhiteSpace(packId))
                {
                    if (!packCache.TryGetValue(packId, out var pack))
                    {
                        pack = await packStore.GetByIdAsync(packId, CancellationToken.None);
                        packCache[packId] = pack;
                    }
                    if (pack is not null)
                    {
                        c = c with {
                            PositionManagement = _configResolver.ApplyPack(c.PositionManagement, pack),
                            RegimeFilter = (c.RegimeFilter ?? new RegimeFilterOptions()) with {
                                DetectionEnabled = pack.RegimeDetectionEnabled
                            }
                        };
                    }
                }
                // iter-38 R1 run-master: force regime detection OFF for every strategy this run. The existing
                // per-strategy mechanism (RegimeFilterOptions.DetectionEnabled=false ⇒ Allows allow-all) then
                // lets the strategy trade in any regime — no engine-path change needed.
                if (disableRegime)
                    c = c with { RegimeFilter = (c.RegimeFilter ?? new RegimeFilterOptions()) with { DetectionEnabled = false } };

                // iter-redesign P3.2 (D2): strip add-ons last so it overrides both the strategy's stored
                // enrichments AND any applied pack — a "raw" run is provably free of breakeven/trailing/
                // partial/ride/dynamic-SL/TP (baseline SL/TP preserved).
                if (stripAddOns)
                    c = c with { PositionManagement = EffectiveConfigResolver.StripAddOns(c.PositionManagement) };

                // P3.2 exploration mode: after stripping add-ons (if requested), force every strategy
                // to the exploration preset — SL=ATR×4, TP=none, zero enrichments — so the entry signal
                // runs bare. The recorded excursion paths (RecordExcursions=true) are the raw measure of
                // entry quality that the P3.3 ExitReplayer calibrates exits from.
                if (cfg.CustomParams.GetValueOrDefault("ExplorationMode") == "true")
                    c = c with { PositionManagement = EffectiveConfigResolver.ApplyExplorationPreset(c.PositionManagement) };

                strategyConfigs.Add(c);
            }

            var runOverrides = ParseOverrides(cfg);
            if (runOverrides.Count > 0)
            {
                for (var i = 0; i < strategyConfigs.Count; i++)
                {
                    var c = strategyConfigs[i];
                    if (runOverrides.TryGetValue(c.Id, out var ovr))
                    {
                        var resolved = _configResolver.Resolve(c, ovr);
                        strategyConfigs[i] = c with
                        {
                            Parameters = resolved.Parameters,
                            PositionManagement = resolved.PositionManagement,
                            OrderEntry = resolved.OrderEntry,
                            RegimeFilter = resolved.RegimeFilter,
                            Reentry = resolved.Reentry,
                        };
                    }
                }
            }
        }

        var pfStore = scope.ServiceProvider.GetRequiredService<IPropFirmRuleSetStore>();
        var dbPropFirms = await pfStore.GetAllAsync(CancellationToken.None);
        var propFirms = dbPropFirms.Count > 0 ? dbPropFirms : baseConfig.PropFirms;

        GovernorOptions governor;
        try
        {
            var govStore = scope.ServiceProvider.GetRequiredService<IGovernorOptionsStore>();
            governor = await govStore.GetAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load governor options from DB — falling back to JSON config defaults (M19 fix)");
            governor = baseConfig.Governor;
        }

        // iter-strategy-system P1 (D4): run-level governor toggle. Default (absent/"true") keeps the stored
        // governor; "false" disables it for the whole run.
        if (cfg.CustomParams.GetValueOrDefault("GovernorEnabled") == "false")
            governor = governor with { Enabled = false };

        // iter-strategy-system P5: run-level protection toggle overrides. Default (absent/"true") keeps
        // the ruleset defaults. "false" forces the corresponding protection OFF by ANDing into every
        // ruleset's ProtectionToggles, regardless of which ruleset gets selected later.
        var perRunDailyDd = cfg.CustomParams.GetValueOrDefault("DailyDdEnabled") != "false";
        var perRunMaxDd = cfg.CustomParams.GetValueOrDefault("MaxDdEnabled") != "false";
        var perRunForceClose = cfg.CustomParams.GetValueOrDefault("ForceCloseOnBreachEnabled") != "false";
        // iter-redesign P2.2: exposure / daily-budget+heat / position-count limiters are now per-run
        // overridable too, so a "Raw" run can provably disable every limiter (not just the DD set).
        var perRunExposure = cfg.CustomParams.GetValueOrDefault("ExposureEnabled") != "false";
        var perRunBudget = cfg.CustomParams.GetValueOrDefault("BudgetEnabled") != "false";
        var perRunMaxPositions = cfg.CustomParams.GetValueOrDefault("MaxPositionsEnabled") != "false";
        if (!perRunDailyDd || !perRunMaxDd || !perRunForceClose
            || !perRunExposure || !perRunBudget || !perRunMaxPositions)
        {
            propFirms = propFirms.Select(pf => pf with
            {
                Toggles = pf.Toggles with
                {
                    DailyDdEnabled = pf.Toggles.DailyDdEnabled && perRunDailyDd,
                    MaxDdEnabled = pf.Toggles.MaxDdEnabled && perRunMaxDd,
                    ForceCloseOnBreachEnabled = pf.Toggles.ForceCloseOnBreachEnabled && perRunForceClose,
                    ExposureEnabled = pf.Toggles.ExposureEnabled && perRunExposure,
                    BudgetEnabled = pf.Toggles.BudgetEnabled && perRunBudget,
                    MaxPositionsEnabled = pf.Toggles.MaxPositionsEnabled && perRunMaxPositions,
                }
            }).ToList();
        }

        return new LoadedConfig(propFirms, riskProfiles)
        {
            StrategyConfigs = strategyConfigs,
            NewsWindows = baseConfig.NewsWindows,
            StrategyRotation = baseConfig.StrategyRotation,
            Governor = governor,
            SizingPolicy = baseConfig.SizingPolicy,
            Regime = baseConfig.Regime,
        };
    }

    private async Task<BacktestResult> RunCompareBothAsync(
        string runId, BacktestConfig cfg, string comparePairId, ConcurrentQueue<string> logLines, CancellationToken ct)
    {
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
        var tapeResult = await RunEngineReplayAsync(runId, tapeCfg, logLines, ct);

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
        // RunEngineNetMqAsync again, racing with our own call below.
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
        _runs[ctraderRunId] = ctraderState;

        // F18 (R0.1): write a start record to DB immediately so the child run is visible from spawn
        // moment, even if a crash prevents WriteEndRecordAsync from running later.
        await WriteStartRecordAsync(ctraderRunId, ctraderCfg, ctraderState.StartedAt, null);

        EnqueueLog(ctraderRunId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] Compare mode — running cTrader leg {ctraderRunId}...");

        try
        {
            TransitionRun(ctraderState, RunStateMachine.Running);
            EnqueueLog(ctraderRunId, ctraderState.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running via in-process cTrader engine...");
            var ctraderResult = await RunEngineNetMqAsync(ctraderRunId, ctraderCfg, ctraderState.LogLines, ct);

            TransitionRun(ctraderState, RunStateMachine.Finalizing);

            // This leg is the one every parity comparison is measured against, and it was the only leg
            // with no integrity checks on it: this path is a parallel copy of RunAsync's finalize block
            // that never ran the trade-persistence barrier and never merged the run's warnings, so a
            // compare-both cTrader leg could lose trades, or declare a EUR account, and still be stored
            // as a clean `completed` run with WarningsJson NULL. Both steps now run here, as they do in
            // RunAsync — barrier first, so its backfilled trades are counted by GetTradeStatsAsync.
            if (ctraderResult.Success)
                await RunTradePersistenceBarrierAsync(ctraderRunId, ctraderState, ct);

            var tradeStats = await GetTradeStatsAsync(ctraderRunId, cfg.Balance);
            var ctraderWarnings = MergeWarningsJson(ctraderState, ctraderResult.WarningsJson);
            ctraderResult = ctraderResult with
            {
                NetProfit = tradeStats.NetProfit,
                MaxDrawdownPct = tradeStats.MaxDrawdownPct,
                TotalTrades = tradeStats.TotalTrades,
                WinningTrades = tradeStats.WinningTrades,
                WinRatePct = tradeStats.WinRatePct,
                WarningsJson = ctraderWarnings,
            };

            ctraderState.Result = ctraderResult;
            ctraderState.Error = ctraderResult.ErrorMessage;
            TransitionRun(ctraderState, ctraderResult.Success
                ? (RunStatusResolver.HasWarnings(ctraderWarnings)
                    ? RunStateMachine.CompletedWithWarnings
                    : RunStateMachine.Completed)
                : RunStateMachine.Failed);

            await WriteEndRecordAsync(ctraderRunId, ctraderCfg, ctraderState.StartedAt, ctraderResult, tradeStats, null);

            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Compare complete. Tape={runId} ({tapeResult.TotalBars} bars) / cTrader={ctraderRunId} ({ctraderResult.TotalBars} bars / {ctraderResult.TotalTrades}t) — reconcile: GET /api/backtest/analytics/reconcile?left={runId}&right={ctraderRunId}");
        }
        catch (OperationCanceledException)
        {
            TransitionRun(ctraderState, RunStateMachine.Cancelled);
            ctraderState.Error = "Cancelled";
            EnqueueLog(ctraderRunId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] cTrader leg cancelled.");
        }
        catch (Exception ex)
        {
            TransitionRun(ctraderState, RunStateMachine.Failed);
            ctraderState.Error = ex.Message;
            EnqueueLog(ctraderRunId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] cTrader leg failed: {ex.Message}");
        }
        finally
        {
            ctraderState.CancellationSource?.Dispose();
        }

        return tapeResult;
    }

    private async Task<BacktestResult> RunEngineReplayAsync(
        string runId, BacktestConfig cfg, ConcurrentQueue<string> logLines, CancellationToken userCt = default)
    {
        var from = cfg.Start;
        var wallStart = DateTime.UtcNow;

        var dbPath = DbPathResolver.ResolveTradingDbPath(_configuration.GetValue<string>("Persistence:DbPath"));

        using var scope = _scopeFactory.CreateScope();
        var barRepo = scope.ServiceProvider.GetRequiredService<IBarRepository>();

        // iter-marketdata-tape P3: opt-in fast fake venue. Venue="tape" replays the canonical market-data
        // store in-process (no cTrader-cli/NetMQ) with dual-resolution exits (decision TF vs ExitTimeframe,
        // default m1). Default/empty venue keeps the existing per-run-bars BacktestReplayAdapter unchanged.
        var useTape = string.Equals(cfg.CustomParams.GetValueOrDefault("Venue"), "tape", StringComparison.OrdinalIgnoreCase);
        var to = useTape ? cfg.End.Date.AddDays(1) : cfg.End;
        var marketDataStore = useTape ? scope.ServiceProvider.GetService<IMarketDataStore>() : null;
        var exitTf = ParseTimeframe(cfg.CustomParams.GetValueOrDefault("ExitTimeframe") ?? "M1");

        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var state = _runs[runId];
        var progressCallback = new Progress<BacktestProgressEvent>(evt =>
        {
            _journal.Write(runId, evt.EventType, evt.Message);
            if (evt.EventType == "BAR")
            {
                Interlocked.Increment(ref state.BarCount);
                if (evt.Message.Length > 4 && evt.Message.StartsWith("Bar "))
                {
                    var pipeIdx = evt.Message.IndexOf(" | ", StringComparison.Ordinal);
                    state.SimTime = pipeIdx > 4 ? evt.Message[4..pipeIdx] : evt.Message[4..];
                }
            }
            RunProgressProjector.TallyEvent(state, evt);

            var barCount = Volatile.Read(ref state.BarCount) + 1;
            if (barCount <= 5 || barCount % 50 == 0)
                _journal.Write(runId, "LIVE_DIAG", $"BAR#{barCount} tally={state.Signals}s/{state.Orders}o/{state.Fills}f/{state.Closes}c");

            _broadcaster.Publish(RunProgressProjector.Build(state, "running"), force: evt.EventType == "BREACH" || barCount <= 3);
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(userCt,
            new CancellationTokenSource(TimeSpan.FromMinutes(30)).Token);

        // iter-strategy-system P1 (D3): prefer the explicit row plan (RunRows) — each row is a
        // (strategy × symbol × timeframe × pack), built per-pass so the same strategy can carry a different
        // pack on different passes. Otherwise fall back to the legacy Symbols×Periods×Strategies cross-product
        // with one shared config for every pass (behaviour unchanged).
        var rowEntries = ParseRunPlanEntries(cfg);
        var perRow = rowEntries.Count > 0;

        RunPlan runPlan;
        IReadOnlyList<string> activeStrategyIds;
        LoadedConfig? sharedConfig = null;
        List<(Symbol Sym, Timeframe Tf, IReadOnlyDictionary<string, string?>? Packs)> passes;

        if (perRow)
        {
            runPlan = new RunPlan(rowEntries);
            activeStrategyIds = rowEntries.Select(e => e.StrategyId).Distinct().ToArray();
            passes = RunPlanBuilder.IntoPasses(runPlan)
                .Select(p => (Symbol.Parse(p.Symbol), ParseTimeframe(p.Timeframe),
                    (IReadOnlyDictionary<string, string?>?)p.StrategyPacks))
                .ToList();
        }
        else
        {
            var strategyIds = ParseStrategyIds(cfg);
            sharedConfig = await BuildLoadedConfigFromDbAsync(cfg);
            var effectiveStrategyIds = strategyIds.Length > 0
                ? strategyIds
                : sharedConfig.StrategyConfigs.Where(s => s.Enabled).Select(s => s.Id).ToArray();
            runPlan = BuildRunPlan(effectiveStrategyIds, cfg.Symbols, cfg.Periods);
            activeStrategyIds = strategyIds;
            passes = runPlan.Entries
                .Select(e => (Sym: Symbol.Parse(e.Symbol), Tf: ParseTimeframe(e.Timeframe)))
                .Distinct()
                .Select(c => (c.Sym, c.Tf, (IReadOnlyDictionary<string, string?>?)null))
                .ToList();
        }

        if (passes.Count == 0)
        {
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] No symbol/timeframe combinations to run.");
            return new BacktestResult { RunId = runId, ExitCode = 1, AlgoHash = "", ErrorMessage = "No combinations." };
        }

        // F34: every currency this run touches must be priceable into the account denomination from real
        // data. Resolved and loaded here, once, because this is where market data and the run window are
        // both in scope — and it fails the run rather than falling back to a literal (a wrong cross rate
        // is a wrong lot size). Costs nothing on the common case: a USD account trading USD pairs needs
        // no legs at all.
        var accountCurrency = ResolveAccountCurrency();
        var crossRateSeries = await LoadCrossRateSeriesAsync(
            accountCurrency, passes.Select(p => p.Sym).Distinct().ToList(), solutionRoot,
            scope.ServiceProvider.GetService<IMarketDataStore>(), from, to, runId, logLines, cts.Token);
        var venueSymbolSpecs = await LoadVenueSymbolSpecsAsync(cts.Token);

        // P2.1: pre-query actual bar counts so progress shows % of real bars, not calendar estimate.
        // The foreach loop below also sums bar counts, but this locks in barsTotal BEFORE the first pass
        // starts, so the progress bar climbs to 100% smoothly instead of stalling at ~70%.
        var preQueryBars = 0;
        foreach (var (sym, tf, _) in passes)
        {
            if (useTape && marketDataStore is not null)
            {
                var tapeBars = await marketDataStore.ReadBarsAsync(sym, tf, cfg.Start, cfg.End, userCt);
                preQueryBars += tapeBars.Count;
            }
            else
            {
                var bars = await barRepo.GetAsync(sym, tf, cfg.Start, cfg.End, userCt);
                preQueryBars += bars.Count;
            }
        }
        if (preQueryBars > 0)
            state.BarsTotal = preQueryBars;

        var totalBars = 0;
        var anyBars = false;
        var passIndex = 0;

        foreach (var (sym, tf, packs) in passes)
        {
            passIndex++;
            // Per-row runs build a fresh config per pass so the SAME strategy can carry a DIFFERENT pack on
            // each (symbol,tf). Legacy runs reuse one shared config (cheaper, behaviour byte-identical).
            var passConfig = perRow ? await BuildLoadedConfigFromDbAsync(cfg, packs) : sharedConfig!;
            state.CurrentPass = $"{sym}/{tf}";
            state.PassIndex = passIndex;
            state.PassTotal = passes.Count;

            // P1.3: compute auxiliary-timeframe bars needed by multi-TF strategies (e.g. mtf-trend needs H4).
            IReadOnlyDictionary<string, IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>>>? auxBars = null;
            if (useTape && marketDataStore is not null && activeStrategyIds.Contains("mtf-trend"))
            {
                var auxTf = Timeframe.H4;
                if (tf != auxTf)
                {
                    var auxBarList = await marketDataStore.ReadBarsAsync(sym, auxTf, from, to, userCt);
                    if (auxBarList.Count > 0)
                    {
                        auxBars = new Dictionary<string, IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>>>
                        {
                            [sym.ToString()] = new Dictionary<Timeframe, IReadOnlyList<Bar>>
                            {
                                [auxTf] = auxBarList,
                            },
                        };
                    }
                }
            }

            var innerHost = EngineHostFactory.Create(new EngineHostOptions
            {
                RunId = runId,
                Mode = EngineMode.Backtest,
                AdapterFactory = sp =>
                {
                    if (useTape && marketDataStore is not null)
                    {
                        // P0.3 (D4): honest entry timing default ON; CustomParams["HonestFills"]="false"
                        // preserves the old optimistic (fill-at-signal-bar-close) behavior for A/B.
                        var honestFills = cfg.CustomParams.GetValueOrDefault("HonestFills") != "false";
                        // P3.1: opt-in excursion recorder, default OFF (unlike HonestFills) -- this is
                        // instrumentation for the exploration/exit-lab workflow (P3.2+), not a default-on
                        // behavior change. CustomParams["RecordExcursions"]="true" turns it on.
                        var recordExcursions = cfg.CustomParams.GetValueOrDefault("RecordExcursions") == "true";
                        var tapeAdapter = new TapeReplayAdapter(marketDataStore, sym, tf, exitTf, from, to,
                            cfg.Balance, sp.GetRequiredService<ISymbolInfoRegistry>(),
                            sp.GetRequiredService<Func<string, string, decimal>>(),
                            sp.GetRequiredService<ILogger<TapeReplayAdapter>>(),
                            honestFills, recordExcursions,
                            commissionPerMillion: (decimal?)cfg.CommissionPerMillion,
                            spreadPipsOverride: (decimal?)cfg.SpreadPips);
                        tapeAdapter.Speed = state.Speed;
                        state.TapeAdapter = tapeAdapter;
                        return tapeAdapter;
                    }
                    return new BacktestReplayAdapter(barRepo, sym, tf, from, to,
                        cfg.Balance, sp.GetRequiredService<ISymbolInfoRegistry>(),
                        sp.GetRequiredService<Func<string, string, decimal>>(),
                        sp.GetRequiredService<ILogger<BacktestReplayAdapter>>(),
                        commissionPerMillion: (decimal?)cfg.CommissionPerMillion);
                },
                DbPath = dbPath,
                SolutionRoot = solutionRoot,
                SymbolNames = cfg.Symbols,
                ActiveStrategyIds = activeStrategyIds,
                RunPlan = runPlan,
                PreloadedConfig = passConfig,
                Progress = progressCallback,
                MinLogLevel = LogLevel.Warning,
                DiagnosticsEnabled = _configuration.GetSection("Engine:Diagnostics").GetValue<bool>("Enabled"),
                RunDataCache = _runDataCache,
                SkipJournal = string.Equals(cfg.CustomParams.GetValueOrDefault("SkipJournal"), "true", StringComparison.OrdinalIgnoreCase),
                PreloadedAuxBars = auxBars,
                InitialBalance = cfg.Balance,
                AccountCurrency = accountCurrency,
                CrossRateSeries = crossRateSeries,
                VenueSymbolSpecs = venueSymbolSpecs,
            });
            state.EngineHost = innerHost;
            EngineHostFactory.WireEventHandlers(innerHost);
            EngineHostFactory.WireRiskRules(innerHost);

            if (_configuration.GetSection("Engine:Diagnostics").GetValue<bool>("Enabled"))
                _logger.LogWarning("Engine diagnostics enabled for run {RunId} — engine profiling → %TEMP%/shamshir-profiling/; cBot timing → run log (CBOT|TIMING)", runId);

            await innerHost.StartAsync(cts.Token);
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Pass {passIndex}/{passes.Count} {sym}/{tf} started...");

            using var equityCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            _ = StartEquityPollingAsync(innerHost, state, runId, equityCts.Token);

            var adapter = innerHost.Services.GetRequiredService<IBrokerAdapter>();
            await adapter.BarStream.Completion;

            var barCount = (adapter as IReplayVenue)?.BarCount ?? 0;
            totalBars += barCount;
            if (barCount > 0) anyBars = true;

            if (adapter is TapeReplayAdapter tape && tape.ExitResolution is not null)
                state.ExitResolution = tape.ExitResolution;
            // BarsTotal is set by pre-query (line 832) — do NOT overwrite mid-loop
            // or the display shows nonsense like "99.9% (816 / 400)" on multi-pass runs.

            equityCts.Cancel();
            await FlushRunPersistenceAsync(innerHost);
            CaptureFinalEquity(state, innerHost, runId);
            await innerHost.StopAsync(CancellationToken.None);
            await DisposeHostAsync(innerHost);

            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Pass {passIndex}/{passes.Count} {sym}/{tf} complete ({barCount} bars).");
        }

        state.BarsTotal = totalBars;
        state.EngineHost = null;

        if (!anyBars)
        {
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] No bars found for any symbol/timeframe in {cfg.Start:yyyy-MM-dd}–{cfg.End:yyyy-MM-dd}.");
            return new BacktestResult
            {
                RunId = runId,
                ExitCode = 1,
                AlgoHash = "",
                ErrorMessage = "No bars found for any symbol/timeframe combination."
            };
        }

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] All passes complete ({passes.Count} combinations, {totalBars} total bars).");
        var wallElapsedMs = (long)(DateTime.UtcNow - wallStart).TotalMilliseconds;
        return new BacktestResult
        {
            RunId = runId,
            ExitCode = 0,
            AlgoHash = "",
            WallElapsedMs = wallElapsedMs,
            BarsPerSec = wallElapsedMs > 0 ? totalBars / (wallElapsedMs / 1000.0) : 0,
            TotalBars = totalBars,
        };
    }

    private async Task<BacktestResult> RunEngineNetMqAsync(
        string runId, BacktestConfig cfg, ConcurrentQueue<string> logLines, CancellationToken ct)
    {
        var ctid = _ctraderOptions.CtId;
        var pwdFile = _ctraderOptions.PwdFile;
        var account = _ctraderOptions.Account;
        var wallStart = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(ctid) || string.IsNullOrWhiteSpace(pwdFile) || string.IsNullOrWhiteSpace(account))
        {
            EnqueueLog(runId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] CTrader credentials not configured");
            return new BacktestResult
            {
                RunId = runId,
                ExitCode = 1,
                AlgoHash = "",
                ErrorMessage = "CTrader credentials not configured."
            };
        }

        var algoPath = ResolveAlgoPath();
        var algoHash = ComputeAlgoHash(algoPath);

        var symbol = Symbol.Parse(cfg.Symbol);
        var timeframe = ParseTimeframe(cfg.Period);

        var (dataPort, commandPort) = AllocatePorts();

        var dbPath = DbPathResolver.ResolveTradingDbPath(_configuration.GetValue<string>("Persistence:DbPath"));

        var solutionRoot = DbPathResolver.FindRepoRoot();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct,
            new CancellationTokenSource(TimeSpan.FromMinutes(30)).Token);

        var progressCallback = new Progress<BacktestProgressEvent>(evt =>
        {
            _journal.Write(runId, evt.EventType, evt.Message);
            if (evt.EventType is "EXEC" or "REJECTED" or "NETMQ_CONNECTED" or "NETMQ_SENT"
                or "NETMQ_DROPPED" or "CBOT")
            {
                _journal.Write(runId, evt.EventType, evt.Message, logLines);
            }

            if (!_runs.TryGetValue(runId, out var runState)) return;
            if (evt.EventType == "BAR")
            {
                runState.BarCount++;
                if (evt.Message.Length > 4 && evt.Message.StartsWith("Bar "))
                {
                    var pipeIdx = evt.Message.IndexOf(" | ", StringComparison.Ordinal);
                    runState.SimTime = pipeIdx > 4 ? evt.Message[4..pipeIdx] : evt.Message[4..];
                }
            }
            RunProgressProjector.TallyEvent(runState, evt);
            _broadcaster.Publish(RunProgressProjector.Build(runState, "running"), force: evt.EventType == "BREACH");
        });

        var strategyIds = ParseStrategyIds(cfg);
        var loadedConfig = await BuildLoadedConfigFromDbAsync(cfg);
        var effectiveStrategyIds = strategyIds.Length > 0
            ? strategyIds
            : loadedConfig.StrategyConfigs.Where(s => s.Enabled).Select(s => s.Id).ToArray();
        var runPlan = BuildRunPlan(effectiveStrategyIds, cfg.Symbols, cfg.Periods);

        var accountCurrency = ResolveAccountCurrency();
        IReadOnlyDictionary<string, IReadOnlyList<CrossRatePoint>>? crossRateSeries;
        using (var rateScope = _scopeFactory.CreateScope())
        {
            crossRateSeries = await LoadCrossRateSeriesAsync(
                accountCurrency, cfg.Symbols.Select(Symbol.Parse).ToList(), solutionRoot,
                rateScope.ServiceProvider.GetService<IMarketDataStore>(),
                cfg.Start, cfg.End, runId, logLines, ct);
        }

        var innerHost = EngineHostFactory.Create(new EngineHostOptions
        {
            RunId = runId,
            Mode = EngineMode.Backtest,
            AdapterFactory = sp =>
            {
                var transport = new NetMqMessageTransport(
                    $"tcp://127.0.0.1:{dataPort}",
                    $"tcp://*:{commandPort}",
                    sp.GetRequiredService<ILogger<NetMqMessageTransport>>());
                var adapter = new CTraderBrokerAdapter(transport,
                    sp.GetRequiredService<ILogger<CTraderBrokerAdapter>>());
                adapter.OnSymbolSpec = spec =>
                {
                    sp.GetRequiredService<ISymbolInfoRegistry>().UpsertVenueSpec(spec);

                    // P4.4 (F44): persist it. The cTrader leg is the only leg that ever learns the broker's
                    // real commission/swap; without durable storage those numbers die with the process and
                    // the tape leg silently re-prices off the fabricated symbols.json rates.
                    //
                    // NOTE `sp` here is the INNER engine host's provider, which knows nothing of the web
                    // app's services — resolving the store from it throws inside HandleSymbolSpec and takes
                    // the whole cTrader leg down with it. Go through the orchestrator's own scope factory.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            await scope.ServiceProvider.GetRequiredService<IVenueSymbolSpecStore>().SaveAsync(spec);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to persist venue symbol spec for {Symbol}", spec.Symbol);
                        }
                    });
                };
                adapter.OnStatusChange = (type, msg) =>
                {
                    _journal.Write(runId, type, msg);
                    _journal.Write(runId, type, msg, logLines);
                    ((IProgress<BacktestProgressEvent>)progressCallback).Report(
                        new BacktestProgressEvent(runId, type, msg, DateTime.UtcNow));
                    Task.Run(async () =>
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                        db.VenueSessions.Add(new TradingEngine.Infrastructure.Persistence.Entities.VenueSessionEntity
                        {
                            RunId = runId, Event = type, Detail = msg, OccurredAtUtc = DateTime.UtcNow
                        });
                        await db.SaveChangesAsync();
                    });
                };
                return adapter;
            },
            DbPath = dbPath,
            SolutionRoot = solutionRoot,
            SymbolNames = cfg.Symbols,
            ActiveStrategyIds = strategyIds,
            RunPlan = runPlan,
            PreloadedConfig = loadedConfig,
            Progress = progressCallback,
            MinLogLevel = LogLevel.Warning,
            DiagnosticsEnabled = _configuration.GetSection("Engine:Diagnostics").GetValue<bool>("Enabled"),
            RunDataCache = _runDataCache,
            InitialBalance = cfg.Balance,
            // F34: the cTrader leg sizes orders in the engine exactly as the tape leg does, so it must model
            // the SAME account denomination — otherwise the two venues size differently from identical
            // signals and every per-trade lot comparison in the parity gate is meaningless.
            AccountCurrency = accountCurrency,
            CrossRateSeries = crossRateSeries,
            // ...and, for the same reason, the SAME venue-declared economics (F44).
            VenueSymbolSpecs = await LoadVenueSymbolSpecsAsync(ct),
        });
        EngineHostFactory.WireEventHandlers(innerHost);
        EngineHostFactory.WireRiskRules(innerHost);

        if (_configuration.GetSection("Engine:Diagnostics").GetValue<bool>("Enabled"))
            _logger.LogWarning("Engine diagnostics enabled for run {RunId} — engine profiling → %TEMP%/shamshir-profiling/; cBot timing → run log (CBOT|TIMING)", runId);

        try
        {
            await innerHost.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            await DisposeHostAsync(innerHost);
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Engine start failed: {ex.Message}");
            return new BacktestResult
            {
                RunId = runId,
                ExitCode = 1,
                AlgoHash = algoHash,
                ErrorMessage = $"Engine start failed: {ex.Message}"
            };
        }

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Engine started (in-process NetMQ). Ports={dataPort}/{commandPort}");

        _ = StartEquityPollingAsync(innerHost, _runs[runId], runId, cts.Token);

        var resultsDir = Path.Combine(Path.GetTempPath(), "shamshir-backtest", runId);
        Directory.CreateDirectory(resultsDir);
        var reportJsonPath = Path.Combine(resultsDir, "events.json");
        // The cBot writes its own resilient ledger here (ShamshirTradeLogger), independent of
        // cTrader-cli's crash-prone report-saving.
        var cbotReportDir = Path.Combine(resultsDir, "cbot");
        Directory.CreateDirectory(cbotReportDir);

        var cli = new CTraderCli();
        var diagnosticsEnabled = _configuration.GetSection("Engine:Diagnostics").GetValue<bool>("Enabled");
        var argList = new List<string>
        {
            $"--start={cfg.Start:dd/MM/yyyy}", $"--end={cfg.End:dd/MM/yyyy}",
            $"--symbol={cfg.Symbol}", $"--period={cfg.Period}",
            $"--balance={cfg.Balance}", $"--commission={cfg.CommissionPerMillion}",
            $"--spread={cfg.SpreadPips}", $"--data-mode={cfg.DataMode}",
            $"--ctid={ctid}", $"--pwd-file={pwdFile}", $"--account={account}",
            $"--DataPort={dataPort}", $"--CommandPort={commandPort}",
            $"--SymbolString={string.Join(",", cfg.Symbols)}",
            $"--Periods={string.Join(",", cfg.Periods)}",
            $"--ReportPath={cbotReportDir}",
            "--full-access",
        };
        // Phase-0 measurement (audit fast-track): turn on the cBot's per-bar round-trip + tick-publish timing
        // (emitted as CBOT|TIMING to ctrader-cli stdout in OnStop) on the SAME opt-in switch as the engine-side
        // timing. Measurement-only: it does NOT set Verbose, so F11/F12 stay suppressed and backtest behaviour
        // is unchanged. Without this, the cBot timing harness is only ever reachable from the E2E test harness.
        if (diagnosticsEnabled)
            argList.Add("--Diagnostics=true");
        var args = argList.ToArray();

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Launching ctrader-cli...");
        CTraderResult cliResult;
        int ctraderBarCount = 0;
        try
        {
            cliResult = await cli.BacktestAsync(algoPath, args, cts.Token,
                onStarted: pid => _owner.Register(pid, $"run:{runId}"));
        }
        finally
        {
            // P0.2 (F5, Q5): the engine has already produced a complete result by the time we get here.
            // Any exception during transport/host teardown must NOT propagate to the orchestrator's outer
            // catch (which would stamp this complete run `failed`). Each step is isolated and records a
            // warning instead — the run downgrades to `completed-with-warnings`, never `failed`. The
            // idempotent transport fix (6533c7e) removes the known NetMQPoller race at source; this is the
            // durable safety net for any future teardown fault.
            var adapter = innerHost.Services.GetRequiredService<IBrokerAdapter>();
            var barDone = adapter.BarStream.Completion;
            var safety = Task.Delay(TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(barDone, safety) == safety)
            {
                _logger.LogWarning("CTRADER|BAR_STREAM_TIMEOUT|run={RunId}|forcing disconnect", runId);
                EnqueueLog(runId, logLines,
                    $"[{DateTime.UtcNow:HH:mm:ss}] WARNING: Bar stream did not complete — forcing disconnect");
                AddTeardownWarning(runId, "BAR_STREAM_TIMEOUT", "Bar stream did not complete within 30s — forced disconnect");
                try { await adapter.DisconnectAsync(CancellationToken.None); } catch { }
                try { await barDone; } catch { }
            }
            ctraderBarCount = _runs.TryGetValue(runId, out var rs) ? rs.BarCount : 0;
            await SafeTeardownStepAsync(runId, "FLUSH_PERSISTENCE", () => FlushRunPersistenceAsync(innerHost));
            try { CaptureFinalEquity(_runs[runId], innerHost, runId); }
            catch (Exception ex) { AddTeardownWarning(runId, "CAPTURE_EQUITY", ex.Message); }
            await SafeTeardownStepAsync(runId, "HOST_STOP", () => innerHost.StopAsync(CancellationToken.None));
            await SafeTeardownStepAsync(runId, "HOST_DISPOSE", () => DisposeHostAsync(innerHost));
        }

        // iter-redesign-ctrader P6.3 / P2.1 → X4: after the engine has disposed and the run is done, reap
        // any remaining ctrader-cli process THIS run owns. The ChildProcessReaper job object kills on
        // parent-exit, but the persistent web app lives on. X4 makes this owned-PID (by run tag) instead
        // of by image name, so it is safe under parallel cTrader and never touches another worktree's cli.
        _owner.ReapByTag($"run:{runId}", "reap");

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] CLI exit code: {cliResult.ExitCode}");

        // Surface the cBot's Phase-0 timing (only present when --Diagnostics=true) into the run log, so a real
        // cTrader backtest can be profiled from the UI — this is the round-trip-window-vs-total + tick-publish
        // count the audit's fast-track said to measure FIRST to decide whether F11 or ctrader-cli tick replay
        // dominates wall-clock.
        // NOTE: the cBot's Print() output does NOT survive the cTrader CLI — nothing it prints reaches
        // StandardOutput. Anything the cBot must tell the engine goes over NetMQ or into its own
        // ledger (see WarnOnVenueCurrencyMismatch); this loop is kept only for the CLI's own lines.
        foreach (var line in cliResult.StandardOutput.Split('\n'))
        {
            var t = line.Trim();
            if (t.Contains("CBOT|TIMING") || t.Contains("CBOT|STOP"))
                EnqueueLog(runId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] {t}");
        }

        var isKnownCrash = cliResult.ExitCode != 0 && cliResult.IsKnownPostBacktestCrash;

        if (cliResult.ExitCode != 0 && !isKnownCrash)
        {
            var errMsg = !string.IsNullOrWhiteSpace(cliResult.StandardError)
                ? cliResult.StandardError.Trim()
                : cliResult.StandardOutput.Split('\n').LastOrDefault(l => l.Contains("Error"))?.Trim()
                    ?? $"ctrader-cli exited with code {cliResult.ExitCode}";
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] CLI error: {errMsg}");
        }

        var reportHtmlPath = "";
        var captureSucceeded = false;
        try
        {
            var algoDir = Path.GetDirectoryName(algoPath)!;
            var dataSrcDir = Path.Combine(algoDir, "data", "src");
            if (Directory.Exists(dataSrcDir))
            {
                var backtestDirs = Directory.GetDirectories(dataSrcDir, "Backtesting", SearchOption.AllDirectories)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .ToList();

                if (backtestDirs.Count > 0)
                {
                    var latestDir = backtestDirs[0];
                    var htmlFile = Path.Combine(latestDir, "report.html");
                    if (File.Exists(htmlFile))
                    {
                        var destHtml = Path.Combine(resultsDir, "report.html");
                        File.Copy(htmlFile, destHtml, overwrite: true);
                        reportHtmlPath = destHtml;
                        EnqueueLog(runId, logLines,
                            $"[{DateTime.UtcNow:HH:mm:ss}] cTrader report: {destHtml}");
                    }

                    foreach (var dir in backtestDirs)
                    {
                        var jsonFile = Path.Combine(dir, "events.json");
                        if (!File.Exists(jsonFile)) continue;
                        if (new FileInfo(jsonFile).Length == 0) continue;
                        File.Copy(jsonFile, reportJsonPath, overwrite: true);
                        captureSucceeded = true;
                        EnqueueLog(runId, logLines,
                            $"[{DateTime.UtcNow:HH:mm:ss}] cTrader events JSON: {reportJsonPath}");
                        break;
                    }
                }
            }

            if (!captureSucceeded && File.Exists(reportJsonPath))
            {
                captureSucceeded = true;
                EnqueueLog(runId, logLines,
                    $"[{DateTime.UtcNow:HH:mm:ss}] cTrader events JSON: {reportJsonPath}");
            }
        }
        catch (Exception ex)
        {
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Failed to capture cTrader report: {ex.Message}");
        }

        // Prefer the cBot's own report.json (always written, carries clientOrderId + per-trade history)
        // over cTrader's scraped events.json. This is the venue ledger used for reconciliation.
        var cbotReport = Path.Combine(cbotReportDir, CtraderReportHarvester.CbotReportFileName);
        string? finalReportPath =
            File.Exists(cbotReport) && new FileInfo(cbotReport).Length > 0 ? cbotReport
            : captureSucceeded ? reportJsonPath
            : null;
        if (finalReportPath == cbotReport)
            EnqueueLog(runId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] cBot ledger: {cbotReport}");

        // F34: the venue declares its deposit currency in its own ledger. Every figure in that ledger
        // — gross, net, commission, swap, equity — is denominated in it, while this engine models USD
        // throughout (pip values, risk sizing, FTMO limits, the entire tape). A mismatch is a silent
        // FX scaling on every number the run produces, so the run has to say so out loud. (The cBot's
        // Print output does NOT survive the cTrader CLI, so the report file is the only reliable
        // channel for this.)
        WarnOnVenueCurrencyMismatch(runId, finalReportPath, logLines);

        var wallElapsedMsCtrader = (long)(DateTime.UtcNow - wallStart).TotalMilliseconds;
        return new BacktestResult
        {
            RunId = runId,
            ExitCode = isKnownCrash ? 0 : cliResult.ExitCode,
            AlgoHash = algoHash,
            ErrorMessage = isKnownCrash ? null : (cliResult.ExitCode != 0
                ? cliResult.StandardError.Trim() ?? $"CLI exited with code {cliResult.ExitCode}"
                : null),
            ReportJsonPath = finalReportPath,
            WallElapsedMs = wallElapsedMsCtrader,
            BarsPerSec = wallElapsedMsCtrader > 0 ? ctraderBarCount / (wallElapsedMsCtrader / 1000.0) : 0,
            TotalBars = ctraderBarCount,
        };
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

    private static (int dataPort, int commandPort) AllocatePorts()
    {
        using var a = new TcpListener(IPAddress.Loopback, 0);
        using var b = new TcpListener(IPAddress.Loopback, 0);
        a.Start(); b.Start();
        var p1 = ((IPEndPoint)a.LocalEndpoint!).Port;
        var p2 = ((IPEndPoint)b.LocalEndpoint!).Port;
        a.Stop(); b.Stop();
        return (p1, p2);
    }

    // X4: the P2.1 image-name reaper (KillCtraderProcessTreeAsync + RecordReap) was removed. Killing
    // every ctrader-cli/cTrader.Automate by image name was safe only under the strict serial queue; it
    // cross-kills siblings under parallel cTrader and kills another worktree's cli. Reaping is now
    // owned-PID by run tag via CTraderProcessOwner.ReapByTag; the ChildProcessReaper Job Object remains
    // the crash/app-exit net.

    private string ResolveAlgoPath()
    {
        var configured = _configuration["CTrader:AlgoPath"];
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Release", "net6.0", "src.algo")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Debug", "net6.0", "src.algo")),
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("src.algo not found. Build TradingEngine.Adapters.CTrader first.");
    }

    private static string ComputeAlgoHash(string algoPath)
    {
        if (!File.Exists(algoPath)) return "missing";
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(algoPath);
        return Convert.ToHexString(sha.ComputeHash(fs))[..16].ToLowerInvariant();
    }

    // P0.3 (F6): trade-persistence integrity barrier. Runs after the engine produced a complete result
    // and the journal + TradeResults have drained. Reconciles journalled closes vs persisted TradeResults;
    // F34: read the deposit currency the venue declared in its own ledger and warn if it is not the
    // currency this engine models. A EUR-denominated venue account scales every figure a cTrader run
    // produces by the EURUSD rate (~0.86), which is indistinguishable from a strategy difference
    // unless somebody says the word "EUR" out loud — and nothing in the system ever did.
    private void WarnOnVenueCurrencyMismatch(string runId, string? reportPath, ConcurrentQueue<string> logLines)
    {
        if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath))
        {
            return;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(reportPath));
            if (!doc.RootElement.TryGetProperty("main", out var main) ||
                !main.TryGetProperty("accountCurrency", out var ccyEl))
            {
                return;
            }

            // F33: the venue-intent invariant. The cBot compares the protection the venue actually
            // holds against the prices the engine asked for, and counts the disagreements. This is the
            // check that turns "every cTrader stop was at the wrong distance" from a four-session blind
            // spot into a warning on the very first run.
            if (main.TryGetProperty("protectionMismatches", out var pmEl) &&
                pmEl.TryGetInt32(out var mismatches) && mismatches > 0)
            {
                AddTeardownWarning(runId, $"VENUE_PROTECTION_MISMATCH:{mismatches}",
                    $"{mismatches} position(s) carried a stop-loss/take-profit the venue did not hold at the price the engine asked for");
                EnqueueLog(runId, logLines,
                    $"[{DateTime.UtcNow:HH:mm:ss}] WARNING: {mismatches} position(s) had venue protection that did not match the engine's intent");
            }

            var modelled = ResolveAccountCurrency();
            var venueCurrency = ccyEl.GetString();
            if (string.IsNullOrWhiteSpace(venueCurrency) ||
                string.Equals(venueCurrency, modelled, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AddTeardownWarning(runId, $"VENUE_CURRENCY_MISMATCH:{venueCurrency}",
                $"venue account is denominated in {venueCurrency} but the engine models {modelled} — " +
                "every money figure from this run is scaled by an FX rate and is NOT comparable to a tape run");
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] WARNING: venue account currency is {venueCurrency}, engine models {modelled}");
        }
        catch (Exception ex)
        {
            AddTeardownWarning(runId, "VENUE_LEDGER_UNREADABLE", ex.Message);
        }
    }

    /// <summary>The account denomination (F34). One configured value; every symbol, cross rate and venue
    /// check reads it, so a GBP account is a config edit rather than a code change.</summary>
    private string ResolveAccountCurrency() =>
        _configuration.GetValue<string>("Account:Currency") is { Length: > 0 } c
            ? c.ToUpperInvariant()
            : DefaultAccountCurrency;

    /// <summary>
    /// P4.4 (F44): the broker's own commission/swap/contract economics, captured from a live cTrader
    /// session and persisted. EVERY EngineHostOptions built here must carry them — the engine host seeds
    /// its registry from symbols.json, which is fabricated, and only the cTrader leg can learn the real
    /// numbers for itself. Miss a site and that leg silently prices off fiction, which is precisely how
    /// F34 (currency) shipped: two of three option sites never received it.
    /// Never throws — an empty list just means "no venue session captured yet" and the registry warns.
    /// </summary>
    private async Task<IReadOnlyList<VenueSymbolSpec>> LoadVenueSymbolSpecsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IVenueSymbolSpecStore>().LoadAllAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load venue symbol specs — pricing falls back to symbols.json");
            return [];
        }
    }

    /// <summary>
    /// Load the USD-leg rate series this run needs (F34). Returns null when nothing needs converting — a
    /// USD account trading only USD-legged symbols — so the common path stays free.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<CrossRatePoint>>?> LoadCrossRateSeriesAsync(
        string accountCurrency,
        IReadOnlyList<Symbol> tradedSymbols,
        string solutionRoot,
        IMarketDataStore? marketData,
        DateTime fromUtc,
        DateTime toUtc,
        string runId,
        ConcurrentQueue<string> logLines,
        CancellationToken ct)
    {
        var catalog = new SymbolCatalog(solutionRoot, accountCurrency);
        var book = catalog.GetAll();
        var traded = tradedSymbols.Select(s => catalog.Resolve(s.Value)).ToList();

        var required = CrossRateSeriesLoader.RequiredCurrencies(accountCurrency, traded);
        if (required.Count == 0)
        {
            return null;
        }

        if (marketData is null)
        {
            throw new InvalidOperationException(
                $"Run needs cross rates for {string.Join(", ", required)} but no market-data store is " +
                "available to source them. A wrong cross rate is a wrong lot size, so the run stops here.");
        }

        var series = await CrossRateSeriesLoader.LoadAsync(
            accountCurrency, traded, book, marketData, Timeframe.H1, fromUtc, toUtc, ct);

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Cross rates ({accountCurrency} account): " +
            string.Join(", ", series.Select(kv => $"{kv.Key} {kv.Value.Count} obs")));

        return series;
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

    private async Task<TradeStats> GetTradeStatsAsync(string runId, decimal initialBalance)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var trades = await db.Trades
                .Where(t => t.RunId == runId)
                .OrderBy(t => t.ClosedAtUtc)
                .ToListAsync();

            if (trades.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0);

            var netPnL = trades.Sum(t => t.NetPnLAmount);
            var grossPnL = trades.Sum(t => t.GrossPnLAmount);
            var commissionTotal = trades.Sum(t => t.CommissionAmount);
            var swapTotal = trades.Sum(t => t.SwapAmount);
            var wins = trades.Count(t => t.NetPnLAmount > 0);
            var winRate = (double)wins / trades.Count;

            // Max drawdown from the engine's per-bar equity snapshots. Materialize first
            // then compute max to avoid EF Core translation issues with DefaultIfEmpty on nullable.
            var snapshotDds = await db.EquitySnapshots
                .Where(s => s.RunId == runId)
                .Select(s => (decimal?)s.CurrentMaxDrawdown)
                .ToListAsync();
            var snapshotDd = snapshotDds.Count > 0 ? snapshotDds.Max().GetValueOrDefault() : 0m;

            var equity = initialBalance;
            var peak = initialBalance;
            var tradeDd = 0m;
            foreach (var t in trades)
            {
                equity += t.NetPnLAmount;
                if (equity > peak) peak = equity;
                if (peak > 0)
                {
                    var dd = (peak - equity) / peak;
                    if (dd > tradeDd) tradeDd = dd;
                }
            }

            return new(netPnL, grossPnL, commissionTotal, swapTotal, snapshotDd > 0 ? snapshotDd : tradeDd, trades.Count, wins, winRate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query trade stats for {RunId}", runId);
            return new(0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    // Live equity/DD for the Monitor. Reads the engine's in-memory AccountSnapshotStore (which moves
    // as the run progresses), NOT IBrokerAdapter.GetAccountStateAsync — the replay adapter's
    // GetAccountStateAsync returns the INITIAL balance forever, which left the Monitor's equity/DD
    // frozen and the page feeling "stuck".
    private static async Task StartEquityPollingAsync(
        IHost innerHost, BacktestRunState state, string runId, CancellationToken ct)
    {
        var store = innerHost.Services.GetService<IAccountSnapshotStore>();
        if (store is null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                ApplySnapshot(state, await store.GetByRunIdAsync(runId, ct));
            }
        }
        catch (OperationCanceledException) { }
    }

    // Capture the final snapshot onto the run state BEFORE the inner host (and its in-memory store)
    // is disposed, so the terminal RunProgress frame and the saved run summary show real equity/DD.
    private static void CaptureFinalEquity(BacktestRunState state, IHost innerHost, string runId)
    {
        var store = innerHost.Services.GetService<IAccountSnapshotStore>();
        if (store is null) return;
        try { ApplySnapshot(state, store.GetByRunIdAsync(runId, CancellationToken.None).GetAwaiter().GetResult()); }
        catch { /* best effort */ }
    }

    // Drain the buffered DB writers while the inner host's provider is still fully alive, so a short
    // run's equity curve and the tail of its bars are persisted (otherwise the 5s equity flush window
    // and the 500-bar batch threshold drop the run's data — the empty equity chart / "no bars" bugs).
    private static async Task FlushRunPersistenceAsync(IHost host)
    {
        try { await host.Services.GetRequiredService<EquityPersistenceHandler>().FlushAsync(); }
        catch { /* best effort */ }
        try { await host.Services.GetRequiredService<BufferedBarWriter>().FlushAsync(); }
        catch { /* best effort */ }
        // iter-36 K5: the StepRecord journal drains via ChannelJournalWriter.FlushAsync on engine dispose
        // (Wait-mode, lossless) — the old PipelineEventWriter/BarEvaluationHandler force-drains are gone.
    }

    private static async Task DisposeHostAsync(IHost host)
    {
        // Several engine singletons (BufferedBarWriter, EquityPersistenceHandler) are IAsyncDisposable;
        // sync Dispose() can't run their async teardown. Dispose the host asynchronously.
        if (host is IAsyncDisposable ad) await ad.DisposeAsync();
        else host.Dispose();
    }

    private static void ApplySnapshot(BacktestRunState state, IReadOnlyList<AccountSnapshot> snaps)
    {
        if (snaps.Count == 0) return;
        var latest = snaps[^1];
        state.Equity = latest.Equity;
        state.Balance = latest.Balance;
        state.HasEquityObservation = true;
        state.DailyDdPct = latest.DailyDrawdown;
        state.MaxDdPct = latest.MaxDrawdown;
        state.OpenPositions = latest.OpenPositions;
        // iter-38 W-A7: the governor band/reason + distance-to-daily-limit are sourced from the
        // authoritative kernel EngineState (via KernelEquitySnapshot.From), so the Monitor no longer
        // shows a blank governor.
        state.GovernorState = latest.GovernorState;
        state.GovernorReason = latest.GovernorReason;
        state.DistanceToDailyLimit = latest.DistanceToDailyLimit;
    }

    private async Task<string?> ResolveEffectiveConfigJsonAsync(BacktestConfig cfg)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IStrategyConfigStore>();
            var storedConfigs = await store.GetAllAsync(CancellationToken.None);

            // iter-redesign-ctrader P3.1: include the resolved risk profile name in the audit config
            // so the stored EffectiveConfigJson reflects what actually ran — not just strategy overrides.
            var chosenProfile = cfg.CustomParams.GetValueOrDefault("RiskProfileId");
            var profileIsKnown = !string.IsNullOrWhiteSpace(chosenProfile)
                && (await scope.ServiceProvider.GetRequiredService<IRiskProfileStore>()
                    .GetAllAsync(CancellationToken.None) is { Count: > 0 } dbProf
                    ? dbProf
                    : new ConfigLoader(Path.GetFullPath(Path.Combine(
                        AppContext.BaseDirectory, "..", "..", "..", "..", ".."))).LoadBase().RiskProfiles)
                .Any(r => r.Id == chosenProfile);

            var overrides = ParseOverrides(cfg);
            var resolvedEntries = new List<EffectiveConfigEntry>();

            var strategyIds = ParseStrategyIds(cfg);
            if (strategyIds.Length == 0)
                strategyIds = storedConfigs.Where(s => s.Enabled).Select(s => s.Id).ToArray();

            foreach (var sid in strategyIds)
            {
                var stored = storedConfigs.FirstOrDefault(s => s.Id == sid);
                if (stored is null) continue;
                var ovr = overrides.GetValueOrDefault(sid);

                // Stamp the chosen risk profile onto the stored config so the audit JSON reflects it.
                var stamped = profileIsKnown ? stored with { RiskProfileId = chosenProfile! } : stored;
                resolvedEntries.Add(_configResolver.Resolve(stamped, ovr));
            }

            if (resolvedEntries.Count == 0) return null;

            return JsonSerializer.Serialize(resolvedEntries, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve effective config for run {RunId}", cfg.RunId);
            return null;
        }
    }

    private static Dictionary<string, StrategyOverride> ParseOverrides(BacktestConfig cfg)
    {
        if (!cfg.CustomParams.TryGetValue("StrategyOverrides", out var json) ||
            string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, StrategyOverride>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string SymbolsJson(IReadOnlyList<string> symbols) =>
        JsonSerializer.Serialize(symbols ?? []);

    private static string PeriodsJson(IReadOnlyList<string> periods) =>
        JsonSerializer.Serialize(periods ?? []);
}
