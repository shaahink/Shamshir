using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
    private readonly ConcurrentDictionary<string, BacktestRunState> _runs = new();

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
        // RunProgress envelope. Mutated only from the single Progress callback thread.
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
    }

    public BacktestOrchestrator(
        IServiceScopeFactory scopeFactory,
        BacktestProgressStore progressStore,
        BacktestJournal journal,
        IConfiguration configuration,
        RunProgressBroadcaster broadcaster,
        EffectiveConfigResolver configResolver,
        ILogger<BacktestOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _progressStore = progressStore;
        _journal = journal;
        _configuration = configuration;
        _broadcaster = broadcaster;
        _configResolver = configResolver;
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
            RecentJournal: journal);
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
        };
        _runs[runId] = state;
        state.CancellationSource = new CancellationTokenSource();

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

            await WriteEndRecordAsync(runId, cfg, startedAt, result, tradeStats, effectiveConfigJson);
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
            await WriteEndRecordAsync(runId, cfg, startedAt, cancelResult, tradeStats, effectiveConfigJson);
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.Error = ex.Message;
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Error: {ex.Message}");
            _logger.LogError(ex, "Backtest {RunId} failed", runId);

            var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);

            await WriteEndRecordAsync(runId, cfg, startedAt,
                new BacktestResult { RunId = runId, ExitCode = 1, ErrorMessage = ex.Message },
                tradeStats, effectiveConfigJson);
        }
        finally
        {
            var doneJson = JsonSerializer.Serialize(
                new { done = true, status = state.Status, error = state.Error });
            _progressStore.GetWriter(runId).TryWrite(doneJson);
            _progressStore.Complete(runId);

            // iter-21 U1 — terminal frame, always delivered (bypasses the throttle).
            _broadcaster.PublishDone(BuildProgress(state,
                state.Status == "failed" ? "failed" : "completed"));

            _broadcaster.RemoveRun(runId);
            _runs.TryRemove(runId, out _);
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
            });
            var configSetId = TradingEngine.Infrastructure.ConfigSetHash.Compute(configIdentity);
            var parentRunId = cfg.CustomParams.GetValueOrDefault("ParentRunId");
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.MinValue,
                cfg.Symbol, cfg.Period, SymbolsJson(cfg.Symbols), PeriodsJson(cfg.Periods), cfg.Start, cfg.End,
                cfg.Balance, "", "{}", effectiveConfigJson,
                0, 0, 0, 0, 0, 0, 0, 0, -1, null,
                ReportJsonPath: null, DatasetId: datasetId, ConfigSetId: configSetId, Seed: 42,
                ParentRunId: string.IsNullOrWhiteSpace(parentRunId) ? null : parentRunId);
            await repo.SaveAsync(summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write start record for {RunId}", runId);
        }
    }

    private async Task WriteEndRecordAsync(
        string runId, BacktestConfig cfg, DateTime startedAt,
        BacktestResult result, TradeStats stats, string? effectiveConfigJson)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.UtcNow,
                cfg.Symbol, cfg.Period, SymbolsJson(cfg.Symbols), PeriodsJson(cfg.Periods), cfg.Start, cfg.End,
                cfg.Balance, result.AlgoHash, "{}", effectiveConfigJson,
                stats.NetProfit, stats.GrossPnL, stats.CommissionTotal, stats.SwapTotal, stats.MaxDrawdownPct,
                stats.TotalTrades, stats.WinningTrades, stats.WinRatePct,
                result.ExitCode, result.ErrorMessage,
                result.ReportJsonPath);
            await repo.UpdateAsync(summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write end record for {RunId}", runId);
        }
    }

    // Builds the engine's LoadedConfig from the DATABASE (canonical config source) rather than letting
    // the inner host re-read config/strategies/*.json. Strategy parameters, symbols, timeframe, regime
    // filter, order-entry and position-management all come from the seeded DB store, so what the New-
    // Backtest UI shows/edits is exactly what the engine evaluates. Risk profiles, prop-firm rules,
    // governor and sizing are also loaded from DB stores (seeded from JSON at startup).
    private async Task<LoadedConfig> BuildLoadedConfigFromDbAsync(BacktestConfig cfg)
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var baseConfig = new ConfigLoader(solutionRoot).LoadBase();

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IStrategyConfigStore>();
        var dbConfigs = await store.GetAllAsync(CancellationToken.None);

        var chosenProfile = cfg.CustomParams.GetValueOrDefault("RiskProfileId");
        var profileIsKnown = !string.IsNullOrWhiteSpace(chosenProfile)
            && baseConfig.RiskProfiles.Any(r => r.Id == chosenProfile);

        var strategyConfigs = new List<StrategyConfigEntry>();
        {
            // iter-38 PK3 / D1: apply a named add-on pack over each strategy's own add-ons (per-strategy pack
            // wins over the global UsePackId; the pack REPLACES enrichments, baseline SL/TP stays — D4).
            var usePackId = cfg.CustomParams.GetValueOrDefault("UsePackId");
            var disableRegime = cfg.CustomParams.GetValueOrDefault("DisableRegime") == "true";   // iter-38 R1 run-master
            Dictionary<string, string>? perStrategyPacks = null;
            if (cfg.CustomParams.TryGetValue("PerStrategyPackIds", out var ppJson) && !string.IsNullOrWhiteSpace(ppJson))
            {
                try { perStrategyPacks = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(ppJson); }
                catch (Exception ex) { _logger.LogWarning(ex, "Bad PerStrategyPackIds JSON — ignoring"); }
            }

            var packStore = scope.ServiceProvider.GetRequiredService<IAddOnPackStore>();
            var packCache = new Dictionary<string, AddOnPack?>();

            foreach (var c0 in dbConfigs)
            {
                var c = profileIsKnown ? c0 with { RiskProfileId = chosenProfile! } : c0;
                var packId = perStrategyPacks?.GetValueOrDefault(c.Id) ?? usePackId;
                if (!string.IsNullOrWhiteSpace(packId))
                {
                    if (!packCache.TryGetValue(packId, out var pack))
                    {
                        pack = await packStore.GetByIdAsync(packId, CancellationToken.None);
                        packCache[packId] = pack;
                    }
                    if (pack is not null)
                        c = c with { PositionManagement = _configResolver.ApplyPack(c.PositionManagement, pack) };
                }
                // iter-38 R1 run-master: force regime detection OFF for every strategy this run. The existing
                // per-strategy mechanism (RegimeFilterOptions.DetectionEnabled=false ⇒ Allows allow-all) then
                // lets the strategy trade in any regime — no engine-path change needed.
                if (disableRegime)
                    c = c with { RegimeFilter = (c.RegimeFilter ?? new RegimeFilterOptions()) with { DetectionEnabled = false } };
                strategyConfigs.Add(c);
            }
        }

        var rpStore = scope.ServiceProvider.GetRequiredService<IRiskProfileStore>();
        var dbRiskProfiles = await rpStore.GetAllAsync(CancellationToken.None);
        var riskProfiles = dbRiskProfiles.Count > 0 ? dbRiskProfiles : baseConfig.RiskProfiles;

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
        var symbol = Symbol.Parse(cfg.Symbol);
        var timeframe = ParseTimeframe(cfg.Period);
        var from = cfg.Start;
        var to = cfg.End;

        var dbPath = _configuration.GetValue<string>("Persistence:DbPath")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "data", "trading.db"));

        using var scope = _scopeFactory.CreateScope();
        var barRepo = scope.ServiceProvider.GetRequiredService<IBarRepository>();

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
                    // Message looks like "Bar 2024-01-01 00:00 | close=…" — keep the whole timestamp
                    // (date AND time) up to the " | " separator so the sim clock advances intra-day.
                    var pipeIdx = evt.Message.IndexOf(" | ", StringComparison.Ordinal);
                    state.SimTime = pipeIdx > 4 ? evt.Message[4..pipeIdx] : evt.Message[4..];
                }
            }
            TallyEvent(state, evt);

            // iter-21 U1 — push a throttled progress frame to the run's SignalR group. Breaches
            // force a frame through so the breach alert is never swallowed by the throttle window.
            _broadcaster.Publish(BuildProgress(state, "running"), force: evt.EventType == "BREACH");
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(userCt,
            new CancellationTokenSource(TimeSpan.FromMinutes(30)).Token);

        var innerHost = EngineHostFactory.Create(new EngineHostOptions
        {
            RunId = runId,
            Mode = EngineMode.Backtest,
            AdapterFactory = sp => new BacktestReplayAdapter(barRepo, symbol, timeframe, from, to,
                cfg.Balance, sp.GetRequiredService<ISymbolInfoRegistry>(),
                sp.GetRequiredService<Func<string, string, decimal>>(),
                sp.GetRequiredService<ILogger<BacktestReplayAdapter>>()),
            DbPath = dbPath,
            SolutionRoot = solutionRoot,
            SymbolNames = cfg.Symbols,
            ActiveStrategyIds = ParseStrategyIds(cfg),
            PreloadedConfig = await BuildLoadedConfigFromDbAsync(cfg),
            Progress = progressCallback,
            MinLogLevel = LogLevel.Warning,
        });
        state.EngineHost = innerHost;
        EngineHostFactory.WireEventHandlers(innerHost);
        EngineHostFactory.WireRiskRules(innerHost);

        await innerHost.StartAsync(cts.Token);
        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Engine started. Replaying bars...");

        var adapter = innerHost.Services.GetRequiredService<IBrokerAdapter>();
        _ = StartEquityPollingAsync(innerHost, state, runId, cts.Token);
        await adapter.BarStream.Completion;

        var barCount = (adapter as BacktestReplayAdapter)?.BarCount ?? 0;
        state.BarsTotal = barCount;
        if (barCount == 0)
        {
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] No bars found for {cfg.Symbol}/{cfg.Period} in {cfg.Start:yyyy-MM-dd}–{cfg.End:yyyy-MM-dd}. Run scripts/seed-bars.ps1 to seed data.");
            await innerHost.StopAsync(CancellationToken.None);
            await DisposeHostAsync(innerHost);
            return new BacktestResult
            {
                RunId = runId,
                ExitCode = 1,
                AlgoHash = "",
                ErrorMessage = $"No bars found for {cfg.Symbol}/{cfg.Period}."
            };
        }

        await Task.Delay(2_000, cts.Token);

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Engine replay complete.");
        await FlushRunPersistenceAsync(innerHost);
        CaptureFinalEquity(state, innerHost, runId);
        await innerHost.StopAsync(CancellationToken.None);
        await DisposeHostAsync(innerHost);

        return new BacktestResult { RunId = runId, ExitCode = 0, AlgoHash = "" };
    }

    private async Task<BacktestResult> RunEngineNetMqAsync(
        string runId, BacktestConfig cfg, ConcurrentQueue<string> logLines, CancellationToken ct)
    {
        var ctid = _configuration["CTrader:CtId"];
        var pwdFile = _configuration["CTrader:PwdFile"];
        var account = _configuration["CTrader:Account"];
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
                };
                return adapter;
            },
            DbPath = dbPath,
            SolutionRoot = solutionRoot,
            SymbolNames = cfg.Symbols,
            ActiveStrategyIds = ParseStrategyIds(cfg),
            PreloadedConfig = await BuildLoadedConfigFromDbAsync(cfg),
            Progress = progressCallback,
            MinLogLevel = LogLevel.Warning,
        });
        EngineHostFactory.WireEventHandlers(innerHost);
        EngineHostFactory.WireRiskRules(innerHost);

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
        var args = new[]
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

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Launching ctrader-cli...");
        CTraderResult cliResult;
        try
        {
            cliResult = await cli.BacktestAsync(algoPath, args, cts.Token);
        }
        finally
        {
            await FlushRunPersistenceAsync(innerHost);
            CaptureFinalEquity(_runs[runId], innerHost, runId);
            await innerHost.StopAsync(CancellationToken.None);
            await DisposeHostAsync(innerHost);
        }

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] CLI exit code: {cliResult.ExitCode}");

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

        return new BacktestResult
        {
            RunId = runId,
            ExitCode = isKnownCrash ? 0 : cliResult.ExitCode,
            AlgoHash = algoHash,
            ErrorMessage = isKnownCrash ? null : (cliResult.ExitCode != 0
                ? cliResult.StandardError.Trim() ?? $"CLI exited with code {cliResult.ExitCode}"
                : null),
            ReportJsonPath = finalReportPath,
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
        // Allow background flush loops (Equity every 5s, PipelineEvents every 3s) to catch the
        // last few items before we force-drain everything. The old 5s settle was a blunt instrument;
        // a shorter pause here is enough because FlushAsync/FlushRemainingAsync drain synchronously.
        await Task.Delay(1_000);

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
                var plan = new SymbolTimeframePair(cfg.Symbol, cfg.Period);
                resolvedEntries.Add(_configResolver.Resolve(stored, ovr, plan));
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
