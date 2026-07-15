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
/// The cTrader venue path: in-process NetMQ engine host + the real compiled cBot under
/// ctrader-cli. Extracted verbatim from BacktestOrchestrator.RunEngineNetMqAsync, together with
/// its port allocation, algo resolution/hashing and the F33/F34 venue-ledger checks.
/// </summary>
public sealed class CTraderVenueRunner(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IOptions<CTraderConnectionOptions> ctraderOptions,
    BacktestJournal journal,
    RunProgressBroadcaster broadcaster,
    CTraderProcessOwner owner,
    RunConfigAssembler configAssembler,
    RunMarketContextLoader marketContext,
    RunRegistry registry,
    ILogger<CTraderVenueRunner> logger,
    IRunDataCache? runDataCache = null) : IVenueRunner
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly CTraderConnectionOptions _ctraderOptions = ctraderOptions.Value;
    private readonly BacktestJournal _journal = journal;
    private readonly RunProgressBroadcaster _broadcaster = broadcaster;
    private readonly CTraderProcessOwner _owner = owner;
    private readonly RunConfigAssembler _configAssembler = configAssembler;
    private readonly RunMarketContextLoader _marketContext = marketContext;
    private readonly RunRegistry _registry = registry;
    private readonly ILogger<CTraderVenueRunner> _logger = logger;
    private readonly IRunDataCache? _runDataCache = runDataCache;

    public IReadOnlyList<string> VenueIds { get; } = ["ctrader"];
    public string StartLogLine => "Running via in-process cTrader engine...";

    public async Task<BacktestResult> ExecuteAsync(
        string runId, BacktestConfig cfg, BacktestRunState state, CancellationToken ct)
    {
        var logLines = state.LogLines;
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
        var timeframe = RunRequestParser.ParseTimeframe(cfg.Period);

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

            if (_registry.Get(runId) is not { } runState) return;
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

        var strategyIds = RunRequestParser.ParseStrategyIds(cfg);
        var loadedConfig = await _configAssembler.BuildLoadedConfigFromDbAsync(cfg);
        var effectiveStrategyIds = strategyIds.Length > 0
            ? strategyIds
            : loadedConfig.StrategyConfigs.Where(s => s.Enabled).Select(s => s.Id).ToArray();
        var runPlan = RunRequestParser.BuildRunPlan(effectiveStrategyIds, cfg.Symbols, cfg.Periods);

        var accountCurrency = _marketContext.ResolveAccountCurrency();
        IReadOnlyDictionary<string, IReadOnlyList<CrossRatePoint>>? crossRateSeries;
        using (var rateScope = _scopeFactory.CreateScope())
        {
            crossRateSeries = await _marketContext.LoadCrossRateSeriesAsync(
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
            VenueSymbolSpecs = await _marketContext.LoadVenueSymbolSpecsAsync(ct),
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
            await EngineHostLifecycle.DisposeHostAsync(innerHost);
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

        _ = EngineHostLifecycle.StartEquityPollingAsync(innerHost, state, runId, cts.Token);

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
                AddTeardownWarning(state, "BAR_STREAM_TIMEOUT", "Bar stream did not complete within 30s — forced disconnect");
                try { await adapter.DisconnectAsync(CancellationToken.None); } catch { }
                try { await barDone; } catch { }
            }
            ctraderBarCount = state.BarCount;
            await SafeTeardownStepAsync(state, "FLUSH_PERSISTENCE", () => EngineHostLifecycle.FlushRunPersistenceAsync(innerHost));
            try { EngineHostLifecycle.CaptureFinalEquity(state, innerHost, runId); }
            catch (Exception ex) { AddTeardownWarning(state, "CAPTURE_EQUITY", ex.Message); }
            await SafeTeardownStepAsync(state, "HOST_STOP", () => innerHost.StopAsync(CancellationToken.None));
            await SafeTeardownStepAsync(state, "HOST_DISPOSE", () => EngineHostLifecycle.DisposeHostAsync(innerHost));
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
        WarnOnVenueCurrencyMismatch(state, finalReportPath, logLines);

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
    private void WarnOnVenueCurrencyMismatch(BacktestRunState state, string? reportPath, ConcurrentQueue<string> logLines)
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
                AddTeardownWarning(state, $"VENUE_PROTECTION_MISMATCH:{mismatches}",
                    $"{mismatches} position(s) carried a stop-loss/take-profit the venue did not hold at the price the engine asked for");
                EnqueueLog(state.RunId, logLines,
                    $"[{DateTime.UtcNow:HH:mm:ss}] WARNING: {mismatches} position(s) had venue protection that did not match the engine's intent");
            }

            var modelled = _marketContext.ResolveAccountCurrency();
            var venueCurrency = ccyEl.GetString();
            if (string.IsNullOrWhiteSpace(venueCurrency) ||
                string.Equals(venueCurrency, modelled, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AddTeardownWarning(state, $"VENUE_CURRENCY_MISMATCH:{venueCurrency}",
                $"venue account is denominated in {venueCurrency} but the engine models {modelled} — " +
                "every money figure from this run is scaled by an FX rate and is NOT comparable to a tape run");
            EnqueueLog(state.RunId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] WARNING: venue account currency is {venueCurrency}, engine models {modelled}");
        }
        catch (Exception ex)
        {
            AddTeardownWarning(state, "VENUE_LEDGER_UNREADABLE", ex.Message);
        }
    }


    // P0.2 (F5, Q5): record a teardown/persistence anomaly against the run without failing it.
    private void AddTeardownWarning(BacktestRunState state, string code, string detail)
    {
        state.Warnings.Enqueue(new RunWarning(code, detail, DateTime.UtcNow));
        _logger.LogWarning("RUN_WARNING|run={RunId}|code={Code}|detail={Detail}", state.RunId, code, detail);
    }

    // P0.2 (F5, Q5): run one teardown step in isolation. A fault after a complete engine result becomes
    // a warning, never a propagated exception (which the caller would turn into `failed`).
    private async Task SafeTeardownStepAsync(BacktestRunState state, string code, Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex) { AddTeardownWarning(state, code, ex.Message); }
    }

    private void EnqueueLog(string runId, ConcurrentQueue<string> queue, string msg)
    {
        _journal.Write(runId, "LOG", msg, queue);
    }
}
