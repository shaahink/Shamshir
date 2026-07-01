using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
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

public sealed class BacktestOrchestrator : IBacktestCommandService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BacktestOrchestrator> _logger;
    private readonly BacktestProgressStore _progressStore;
    private readonly BacktestJournal _journal;
    private readonly IConfiguration _configuration;
    private readonly RunProgressBroadcaster _broadcaster;
    private readonly EffectiveConfigResolver _configResolver;
    private readonly IRunDataCache? _runDataCache;
    private readonly IMemoryCache? _memoryCache;
    private readonly ConcurrentDictionary<string, BacktestRunState> _runs = new();

    private const string RunsListCacheKey = "runs:all";

    private sealed record TradeStats(decimal NetProfit, decimal GrossPnL, decimal CommissionTotal, decimal SwapTotal, decimal MaxDrawdownPct, int TotalTrades, int WinningTrades, double WinRatePct);

    public sealed record BacktestRunState
    {
        public required string RunId { get; init; }
        public DateTime StartedAt { get; init; } = DateTime.UtcNow;
        public string Status { get; set; } = "starting";
        public BacktestResult? Result { get; set; }
        public string? Error { get; set; }
        public string Symbol { get; init; } = "";
        public string Period { get; init; } = "";
        public ConcurrentQueue<string> LogLines { get; init; } = new();
        public CancellationTokenSource? CancellationSource { get; set; }
        public Task? RunTask { get; set; }
        public IHost? EngineHost { get; set; }
        public int BarCount;
        public int BarsTotal { get; set; }
        public string SimTime { get; set; } = "";
        public IReadOnlyList<string> GetLogs() => LogLines.ToArray();

        // iter-21 U1 — live funnel counters + a small ring of recent journal lines for the
        // RunProgress envelope. A Progress<T> created on a thread with no captured SyncContext
        // posts its callbacks to the thread pool, so these can fire concurrently.
        public int Signals;
        public int Orders;
        public int Fills;
        public int Closes;
        public int Rejections;
        public int Breaches;
        public readonly Queue<DecisionRecordView> RecentJournal = new();
        public long Seq;

        // iter-24/21 — engine equity snapshot fields, populated from AccountSnapshotStore
        // after the run completes for the final RunProgress envelope.
        public decimal Equity;
        public decimal Balance;
        public decimal DailyDdPct;
        public decimal MaxDdPct;
        public decimal DistanceToDailyLimit;
        public int OpenPositions;
        public string? GovernorState;
        public string? GovernorReason;

        // iter-strategy-system P1/P3: which multi-pass combination is running now (for the live Monitor).
        public string? CurrentPass;
        public int PassIndex;
        public int PassTotal;

        // P4: config fields for memory-first run detail (no DB read while running).
        public string? Venue;
        public bool GovernorEnabled = true;
        public bool RegimeEnabled = true;
        public double CommissionPerMillion;
        public double SpreadPips;
    }

    public BacktestOrchestrator(
        IServiceScopeFactory scopeFactory,
        BacktestProgressStore progressStore,
        BacktestJournal journal,
        IConfiguration configuration,
        RunProgressBroadcaster broadcaster,
        EffectiveConfigResolver configResolver,
        ILogger<BacktestOrchestrator> logger,
        IRunDataCache? runDataCache = null,
        IMemoryCache? memoryCache = null)
    {
        _scopeFactory = scopeFactory;
        _progressStore = progressStore;
        _journal = journal;
        _configuration = configuration;
        _broadcaster = broadcaster;
        _configResolver = configResolver;
        _runDataCache = runDataCache;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    // iter-21 U1 — project the live run state into the throttled SignalR envelope. Fields the
    // orchestrator can't yet source (equity curve, governor, daily-DD) stay at honest zero/null
    // until iter-20 wires the kernel; the page renders an empty-state rather than fabricating.
    private RunProgress BuildProgress(BacktestRunState state, string status)
    {
        DateTime? simTime = DateTime.TryParse(state.SimTime, out var t) ? t : null;
        var elapsedMs = (long)(DateTime.UtcNow - state.StartedAt).TotalMilliseconds;
        var barsPerSec = elapsedMs > 0 ? state.BarCount / (elapsedMs / 1000.0) : 0;
        DecisionRecordView[] journal;
        lock (state.RecentJournal) { journal = state.RecentJournal.ToArray(); }

        var barsTotal = state.BarsTotal > 0 ? state.BarsTotal : 0;
        double percent;
        double? etaSeconds;
        if (status == "completed")
        {
            percent = 100.0;
            etaSeconds = 0;
        }
        else if (barsTotal > 0 && state.BarCount > 0)
        {
            percent = state.BarCount >= barsTotal ? 99.9 : (double)state.BarCount / barsTotal * 100.0;
            etaSeconds = barsPerSec > 0 ? (barsTotal - state.BarCount) / barsPerSec : null;
        }
        else
        {
            percent = 0;
            etaSeconds = null;
        }

        return new RunProgress(
            state.RunId, status, simTime,
            BarsProcessed: state.BarCount, BarsTotal: barsTotal, Percent: percent, EtaSeconds: etaSeconds,
            WallElapsedMs: elapsedMs, BarsPerSec: barsPerSec,
            Equity: state.Equity, Balance: state.Balance, OpenPositions: state.OpenPositions,
            DailyDdPct: state.DailyDdPct, MaxDdPct: state.MaxDdPct,
            DistanceToDailyLimit: state.DistanceToDailyLimit,
            GovernorState: state.GovernorState, GovernorReason: state.GovernorReason,
            Counters: new RunCounters(state.Signals, state.Orders, state.Fills,
                state.Closes, state.Rejections, state.Breaches),
            RecentJournal: journal,
            CurrentPass: state.CurrentPass, PassIndex: state.PassIndex, PassTotal: state.PassTotal);
    }

    internal static void TallyEvent(BacktestRunState state, BacktestProgressEvent evt)
    {
        // Counter keys must match the event-type strings the ENGINE actually emits:
        //   TradingLoop → "SIGNAL"/"ORDER"; MarketEventSource → "EXEC" (fill) / "REJECTED";
        //   EffectExecutor → "CLOSE" (on trade close); AccountProcessor → "BREACH".
        // The old keys ("FILL"/"REJECT"/no breach producer) never matched, so Fills/Rejections/
        // Breaches were always 0 and Closes undercounted.
        // Interlocked: a Progress<T> created on a thread with no captured SyncContext (the background
        // RunAsync task) posts its callbacks to the thread pool, so these can fire concurrently.
        switch (evt.EventType)
        {
            case "SIGNAL": Interlocked.Increment(ref state.Signals); break;
            case "ORDER": Interlocked.Increment(ref state.Orders); break;
            case "EXEC": Interlocked.Increment(ref state.Fills); break;
            case "CLOSE": Interlocked.Increment(ref state.Closes); break;
            case "REJECTED": case "OrderRejected": Interlocked.Increment(ref state.Rejections); break;
            case "BREACH": Interlocked.Increment(ref state.Breaches); break;
        }

        if (evt.EventType is "SIGNAL" or "ORDER" or "EXEC" or "CLOSE" or "REJECTED" or "BREACH")
        {
            lock (state.RecentJournal)
            {
                var indicatorDict = evt.Indicators is { Count: > 0 }
                    ? evt.Indicators.ToDictionary(kv => kv.Key, kv => (object)kv.Value)
                    : null;
                var detail = System.Text.Json.JsonSerializer.Serialize(new
                {
                    equity = state.Equity,
                    balance = state.Balance,
                    dailyDdPct = state.DailyDdPct,
                    barCount = state.BarCount,
                    indicators = indicatorDict,
                });
                var simTime = DateTime.TryParse(state.SimTime, out var parsed) ? parsed : DateTime.UtcNow;
                state.RecentJournal.Enqueue(new DecisionRecordView(
                    ++state.Seq, simTime, null, null, evt.EventType,
                    null, null, null, evt.Message, detail));
                while (state.RecentJournal.Count > 30)
                    state.RecentJournal.Dequeue();
            }
        }
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
        };
        _runs[runId] = state;
        state.CancellationSource = new CancellationTokenSource();

        _memoryCache?.Remove(RunsListCacheKey);

        EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {runId}...");

        state.RunTask = RunAsync(runId, cfg, state.CancellationSource.Token);

        return state;
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
            "completed" or "failed" or "cancelled" => state.Status,
            _ => "running",
        };
        return BuildProgress(state, status);
    }

    public IReadOnlyList<BacktestRunState> GetAll() => _runs.Values.ToList();

    public async Task<string> StartAsync(BacktestConfig cfg, CancellationToken ct)
    {
        var state = Start(cfg);
        await Task.CompletedTask;
        return state.RunId;
    }

    public void Cancel(string runId)
    {
        if (_runs.TryGetValue(runId, out var state))
        {
            state.Status = "cancelled";
            state.CancellationSource?.Cancel();
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var (_, state) in _runs)
            state.CancellationSource?.Cancel();

        var tasks = _runs.Values
            .Select(s => s.RunTask)
            .Where(t => t is not null)
            .ToArray();

        if (tasks.Length > 0)
            await Task.WhenAll(tasks!);
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
            await WriteStartRecordAsync(runId, cfg, startedAt, effectiveConfigJson);

            state.Status = "running";
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {runId}...");

            BacktestResult result;

            // Venue is selectable per run (New-Backtest page). iter-38 D6: the default (and any unknown
            // selection) now routes to the credential-free REPLAY venue; cTrader is EXPLICIT opt-in only
            // (venue == "ctrader"). This kills the symptom of T1/T2/T6/T7/T11/T12 for default dev runs,
            // which previously fell through to the wall-clock-buggy in-process cTrader path.
            var useCtader = ResolveUseCtrader(cfg.CustomParams.GetValueOrDefault("Venue"));
            if (useCtader)
            {
                EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running via in-process cTrader engine...");
                result = await RunEngineNetMqAsync(runId, cfg, state.LogLines, ct);
            }
            else
            {
                EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running engine replay...");
                result = await RunEngineReplayAsync(runId, cfg, state.LogLines, ct);
            }

            var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);

            result = result with
            {
                NetProfit = tradeStats.NetProfit,
                MaxDrawdownPct = tradeStats.MaxDrawdownPct,
                TotalTrades = tradeStats.TotalTrades,
                WinningTrades = tradeStats.WinningTrades,
                WinRatePct = tradeStats.WinRatePct,
            };

            state.Result = result;
            state.Status = result.Success ? "completed" : "failed";
            state.Error = result.ErrorMessage;

            EnqueueLog(runId, state.LogLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Done. Trades={result.TotalTrades} PnL={result.NetProfit:N2} DD={result.MaxDrawdownPct:P1} Gross={tradeStats.GrossPnL:N2} Comm={tradeStats.CommissionTotal:N2} Swap={tradeStats.SwapTotal:N2}");

            finalized = await WriteEndRecordAsync(runId, cfg, startedAt, result, tradeStats, effectiveConfigJson);
        }
        catch (OperationCanceledException)
        {
            // T9: the run was cancelled (user Cancel, the 30-min linked timeout, or host/stream teardown
            // at/near completion). Trades were persisted during the run, so this is NOT a failure — finalize
            // with the trades-so-far and an info log instead of scaring the user with a "failed" + error.
            var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);
            var userCancelled = state.Status == "cancelled";
            state.Status = userCancelled ? "cancelled" : "completed";
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
            finalized = await WriteEndRecordAsync(runId, cfg, startedAt, cancelResult, tradeStats, effectiveConfigJson);
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.Error = ex.Message;
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Error: {ex.Message}");
            _logger.LogError(ex, "Backtest {RunId} failed", runId);

            var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);

            finalized = await WriteEndRecordAsync(runId, cfg, startedAt,
                new BacktestResult { RunId = runId, ExitCode = 1, ErrorMessage = ex.Message },
                tradeStats, effectiveConfigJson);
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
            _broadcaster.PublishDone(BuildProgress(state, state.Status switch
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

    private async Task WriteStartRecordAsync(string runId, BacktestConfig cfg, DateTime startedAt, string? effectiveConfigJson)
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
            });
            var configSetId = TradingEngine.Infrastructure.ConfigSetHash.Compute(configIdentity);
            var parentRunId = cfg.CustomParams.GetValueOrDefault("ParentRunId");
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.MinValue,
                cfg.Symbol, cfg.Period, SymbolsJson(cfg.Symbols), PeriodsJson(cfg.Periods), cfg.Start, cfg.End,
                cfg.Balance, "", "{}", effectiveConfigJson,
                0, 0, 0, 0, 0, 0, 0, 0, -1, null,
                ReportJsonPath: null, DatasetId: datasetId, ConfigSetId: configSetId, Seed: 42,
                ParentRunId: string.IsNullOrWhiteSpace(parentRunId) ? null : parentRunId,
                RunPlanJson: cfg.CustomParams.GetValueOrDefault("RunRows") ?? "[]",
                Venue: cfg.CustomParams.GetValueOrDefault("Venue") ?? "replay",
                RiskProfileId: cfg.CustomParams.GetValueOrDefault("RiskProfileId"),
                GovernorEnabled: cfg.CustomParams.GetValueOrDefault("GovernorEnabled") != "false",
                RegimeEnabled: cfg.CustomParams.GetValueOrDefault("DisableRegime") != "true",
                CommissionPerMillion: cfg.CommissionPerMillion,
                SpreadPips: cfg.SpreadPips);
            await repo.SaveAsync(summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write start record for {RunId}", runId);
        }
    }

    private async Task<bool> WriteEndRecordAsync(
        string runId, BacktestConfig cfg, DateTime startedAt,
        BacktestResult result, TradeStats stats, string? effectiveConfigJson)
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
                cfg.Balance, result.AlgoHash, "{}", effectiveConfigJson,
                stats.NetProfit, stats.GrossPnL, stats.CommissionTotal, stats.SwapTotal, stats.MaxDrawdownPct,
                stats.TotalTrades, stats.WinningTrades, stats.WinRatePct,
                result.ExitCode, result.ErrorMessage,
                result.ReportJsonPath,
                WallElapsedMs: wallElapsedMs,
                BarsPerSec: barsPerSec,
                TotalBars: totalBars);
            await repo.UpdateAsync(summary, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write end record for {RunId}", runId);
            return false;
        }
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

                strategyConfigs.Add(c);
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

    private async Task<BacktestResult> RunEngineReplayAsync(
        string runId, BacktestConfig cfg, ConcurrentQueue<string> logLines, CancellationToken userCt = default)
    {
        var from = cfg.Start;
        var to = cfg.End;
        var wallStart = DateTime.UtcNow;

        var dbPath = _configuration.GetValue<string>("Persistence:DbPath")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "data", "trading.db"));

        using var scope = _scopeFactory.CreateScope();
        var barRepo = scope.ServiceProvider.GetRequiredService<IBarRepository>();

        // iter-marketdata-tape P3: opt-in fast fake venue. Venue="tape" replays the canonical market-data
        // store in-process (no cTrader-cli/NetMQ) with dual-resolution exits (decision TF vs ExitTimeframe,
        // default m1). Default/empty venue keeps the existing per-run-bars BacktestReplayAdapter unchanged.
        var useTape = string.Equals(cfg.CustomParams.GetValueOrDefault("Venue"), "tape", StringComparison.OrdinalIgnoreCase);
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
            TallyEvent(state, evt);

            var barCount = Volatile.Read(ref state.BarCount) + 1;
            if (barCount <= 5 || barCount % 50 == 0)
                _journal.Write(runId, "LIVE_DIAG", $"BAR#{barCount} tally={state.Signals}s/{state.Orders}o/{state.Fills}f/{state.Closes}c");

            _broadcaster.Publish(BuildProgress(state, "running"), force: evt.EventType == "BREACH" || barCount <= 3);
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

        // P2.1: pre-query actual bar counts so progress shows % of real bars, not calendar estimate.
        // The foreach loop below also sums bar counts, but this locks in barsTotal BEFORE the first pass
        // starts, so the progress bar climbs to 100% smoothly instead of stalling at ~70%.
        var preQueryBars = 0;
        foreach (var (sym, tf, _) in passes)
        {
            var bars = await barRepo.GetAsync(sym, tf, cfg.Start, cfg.End, userCt);
            preQueryBars += bars.Count;
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

            var innerHost = EngineHostFactory.Create(new EngineHostOptions
            {
                RunId = runId,
                Mode = EngineMode.Backtest,
                AdapterFactory = sp =>
                {
                    if (useTape && marketDataStore is not null)
                    {
                        return new TapeReplayAdapter(marketDataStore, sym, tf, exitTf, from, to,
                            cfg.Balance, sp.GetRequiredService<ISymbolInfoRegistry>(),
                            sp.GetRequiredService<Func<string, string, decimal>>(),
                            sp.GetRequiredService<ILogger<TapeReplayAdapter>>());
                    }
                    return new BacktestReplayAdapter(barRepo, sym, tf, from, to,
                        cfg.Balance, sp.GetRequiredService<ISymbolInfoRegistry>(),
                        sp.GetRequiredService<Func<string, string, decimal>>(),
                        sp.GetRequiredService<ILogger<BacktestReplayAdapter>>());
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

            var barCount = (adapter as BacktestReplayAdapter)?.BarCount ?? 0;
            totalBars += barCount;
            if (barCount > 0) anyBars = true;
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
        var ctid = _configuration["CTrader:CtId"];
        var pwdFile = _configuration["CTrader:PwdFile"];
        var account = _configuration["CTrader:Account"];
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

        var dbPath = _configuration.GetValue<string>("Persistence:DbPath")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "data", "trading.db"));

        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

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
            TallyEvent(runState, evt);
            _broadcaster.Publish(BuildProgress(runState, "running"), force: evt.EventType == "BREACH");
        });

        var symbolInfo = new SymbolInfo(symbol, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

        var strategyIds = ParseStrategyIds(cfg);
        var loadedConfig = await BuildLoadedConfigFromDbAsync(cfg);
        var effectiveStrategyIds = strategyIds.Length > 0
            ? strategyIds
            : loadedConfig.StrategyConfigs.Where(s => s.Enabled).Select(s => s.Id).ToArray();
        var runPlan = BuildRunPlan(effectiveStrategyIds, cfg.Symbols, cfg.Periods);

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
            cliResult = await cli.BacktestAsync(algoPath, args, cts.Token);
        }
        finally
        {
            var adapter = innerHost.Services.GetRequiredService<IBrokerAdapter>();
            await adapter.BarStream.Completion;
            ctraderBarCount = _runs[runId].BarCount;
            await FlushRunPersistenceAsync(innerHost);
            CaptureFinalEquity(_runs[runId], innerHost, runId);
            await innerHost.StopAsync(CancellationToken.None);
            await DisposeHostAsync(innerHost);
        }

        // iter-redesign-ctrader P6.3: after the engine has disposed and the run is done, kill any
        // remaining ctrader-cli child processes. The ChildProcessReaper arms a job object that kills
        // on parent-exit, but for a persistent web app the parent lives on — we need explicit cleanup.
        // The cli.BacktestAsync may have returned but left grandchild processes alive.
        try
        {
            // ctrader-cli.exe may spawn cTrader.Automate.exe or other children.
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("ctrader-cli"))
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        _logger.LogInformation("CTRADER|REAP|pid={Pid}|killing orphan ctrader-cli", proc.Id);
                        proc.Kill(entireProcessTree: true);
                        _ = Task.Run(async () =>
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                            db.VenueSessions.Add(new TradingEngine.Infrastructure.Persistence.Entities.VenueSessionEntity
                            {
                                RunId = runId, Event = "CTRADER|REAP", Detail = $"killed orphan ctrader-cli pid={proc.Id}", OccurredAtUtc = DateTime.UtcNow
                            });
                            await db.SaveChangesAsync();
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CTRADER|REAP_FAIL|pid={Pid}", proc.Id);
                }
                finally { proc.Dispose(); }
            }
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("cTrader.Automate"))
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        _logger.LogInformation("CTRADER|REAP|pid={Pid}|killing orphan cTrader.Automate", proc.Id);
                        proc.Kill(entireProcessTree: true);
                        _ = Task.Run(async () =>
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                            db.VenueSessions.Add(new TradingEngine.Infrastructure.Persistence.Entities.VenueSessionEntity
                            {
                                RunId = runId, Event = "CTRADER|REAP", Detail = $"killed orphan cTrader.Automate pid={proc.Id}", OccurredAtUtc = DateTime.UtcNow
                            });
                            await db.SaveChangesAsync();
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CTRADER|REAP_FAIL|pid={Pid}", proc.Id);
                }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CTRADER|REAP_ERR|failed to reap orphan processes");
        }

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] CLI exit code: {cliResult.ExitCode}");

        // Surface the cBot's Phase-0 timing (only present when --Diagnostics=true) into the run log, so a real
        // cTrader backtest can be profiled from the UI — this is the round-trip-window-vs-total + tick-publish
        // count the audit's fast-track said to measure FIRST to decide whether F11 or ctrader-cli tick replay
        // dominates wall-clock.
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
