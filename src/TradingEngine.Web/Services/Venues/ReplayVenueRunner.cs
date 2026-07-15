using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using TradingEngine.Domain.Interfaces;
using Microsoft.Extensions.Options;
using TradingEngine.CTraderRunner;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Caching;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Services;

namespace TradingEngine.Web.Services;

/// <summary>
/// The credential-free venue path: default per-run-bars replay (BacktestReplayAdapter) and the
/// opt-in fast tape venue (TapeReplayAdapter, Venue="tape"). Extracted verbatim from
/// BacktestOrchestrator.RunEngineReplayAsync — one runner for both venue ids because they
/// genuinely share this execution path.
/// </summary>
public sealed class ReplayVenueRunner(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    BacktestJournal journal,
    RunProgressBroadcaster broadcaster,
    RunConfigAssembler configAssembler,
    RunMarketContextLoader marketContext,
    ILogger<ReplayVenueRunner> logger,
    IRunDataCache? runDataCache = null) : IVenueRunner
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly BacktestJournal _journal = journal;
    private readonly RunProgressBroadcaster _broadcaster = broadcaster;
    private readonly RunConfigAssembler _configAssembler = configAssembler;
    private readonly RunMarketContextLoader _marketContext = marketContext;
    private readonly ILogger<ReplayVenueRunner> _logger = logger;
    private readonly IRunDataCache? _runDataCache = runDataCache;

    public IReadOnlyList<string> VenueIds { get; } = ["replay", "sim", "simulated", "tape"];
    public string StartLogLine => "Running engine replay...";

    public async Task<BacktestResult> ExecuteAsync(
        string runId, BacktestConfig cfg, BacktestRunState state, CancellationToken userCt)
    {
        var logLines = state.LogLines;
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
        var exitTf = RunRequestParser.ParseTimeframe(cfg.CustomParams.GetValueOrDefault("ExitTimeframe") ?? "M1");

        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

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
        var rowEntries = RunRequestParser.ParseRunPlanEntries(cfg);
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
                .Select(p => (Symbol.Parse(p.Symbol), RunRequestParser.ParseTimeframe(p.Timeframe),
                    (IReadOnlyDictionary<string, string?>?)p.StrategyPacks))
                .ToList();
        }
        else
        {
            var strategyIds = RunRequestParser.ParseStrategyIds(cfg);
            sharedConfig = await _configAssembler.BuildLoadedConfigFromDbAsync(cfg);
            var effectiveStrategyIds = strategyIds.Length > 0
                ? strategyIds
                : sharedConfig.StrategyConfigs.Where(s => s.Enabled).Select(s => s.Id).ToArray();
            runPlan = RunRequestParser.BuildRunPlan(effectiveStrategyIds, cfg.Symbols, cfg.Periods);
            activeStrategyIds = strategyIds;
            passes = runPlan.Entries
                .Select(e => (Sym: Symbol.Parse(e.Symbol), Tf: RunRequestParser.ParseTimeframe(e.Timeframe)))
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
        var accountCurrency = _marketContext.ResolveAccountCurrency();
        var crossRateSeries = await _marketContext.LoadCrossRateSeriesAsync(
            accountCurrency, passes.Select(p => p.Sym).Distinct().ToList(), solutionRoot,
            scope.ServiceProvider.GetService<IMarketDataStore>(), from, to, runId, logLines, cts.Token);
        var venueSymbolSpecs = await _marketContext.LoadVenueSymbolSpecsAsync(cts.Token);

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
            var passConfig = perRow ? await _configAssembler.BuildLoadedConfigFromDbAsync(cfg, packs) : sharedConfig!;
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
            _ = EngineHostLifecycle.StartEquityPollingAsync(innerHost, state, runId, equityCts.Token);

            var adapter = innerHost.Services.GetRequiredService<IBrokerAdapter>();
            await adapter.BarStream.Completion;

            var replayVenue = adapter as IReplayVenue;
            var barCount = replayVenue?.BarCount ?? 0;
            totalBars += barCount;
            if (barCount > 0) anyBars = true;

            if (replayVenue?.ExitResolution is { } exitResolution)
                state.ExitResolution = exitResolution;
            // BarsTotal is set by pre-query above — do NOT overwrite mid-loop
            // or the display shows nonsense like "99.9% (816 / 400)" on multi-pass runs.

            equityCts.Cancel();
            await EngineHostLifecycle.FlushRunPersistenceAsync(innerHost);
            EngineHostLifecycle.CaptureFinalEquity(state, innerHost, runId);
            await innerHost.StopAsync(CancellationToken.None);
            await EngineHostLifecycle.DisposeHostAsync(innerHost);

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


    private void EnqueueLog(string runId, ConcurrentQueue<string> queue, string msg)
    {
        _journal.Write(runId, "LOG", msg, queue);
    }
}
