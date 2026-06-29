using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TradingEngine.CTraderRunner;
using TradingEngine.Domain;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Transport.NetMq;
using TradingEngine.Infrastructure.Venues.CTrader;
using TradingEngine.Services;

namespace TradingEngine.Tests.Simulation.Harness;

public sealed class CtraderE2EHarness : IAsyncDisposable
{
    public RunArtifacts Artifacts { get; }
    private readonly List<string> _diagLog = new();

    private string _symbol = "EURUSD";
    private string _period = "H1";
    private DateTime _start = new(2024, 1, 15);
    private DateTime _end = new(2024, 1, 18);
    private decimal _balance = 100_000m;
    private decimal _commissionPerMillion = 30m;
    private readonly decimal _spreadPips = 1m;
    private readonly string _dataMode = "m1";
    private IReadOnlyList<string> _symbols = new[] { "EURUSD" };
    private IReadOnlyList<string> _periods = new[] { "H1" };
    private IReadOnlyList<string> _activeStrategyIds = [];

    private int _dataPort;
    private int _commandPort;
    private IHost? _host;
    private BacktestCliResult? _cliResult;
    private NetMqMessageTransport? _transport;
    private string? _snapshotPath;
    private SnapshotRecorderSession? _recorder;
    private DateTime _ctraderStartUtc;

    public string RunId => Artifacts.RunId;
    public ITransportStatusSource? TransportStatus => _transport;

    // ── Snapshot configuration ────────────────────────────────────────

    public CtraderE2EHarness WithSnapshotRecording(string path)
    {
        _snapshotPath = path;
        return this;
    }

    public CtraderE2EHarness(string testName)
    {
        Artifacts = RunArtifacts.Create(testName);
    }

    // ── Fluent configuration ─────────────────────────────────────────

    public CtraderE2EHarness WithSymbol(string symbol, string period)
    {
        _symbol = symbol;
        _period = period;
        _symbols = new[] { symbol };
        _periods = new[] { period.ToUpperInvariant() };
        return this;
    }

    public CtraderE2EHarness WithDateRange(DateTime start, DateTime end)
    {
        _start = start;
        _end = end;
        return this;
    }

    public CtraderE2EHarness WithBalance(decimal balance)
    {
        _balance = balance;
        return this;
    }

    public CtraderE2EHarness WithCommission(decimal perMillion)
    {
        _commissionPerMillion = perMillion;
        return this;
    }

    public CtraderE2EHarness WithStrategyIds(params string[] ids)
    {
        _activeStrategyIds = ids;
        return this;
    }

    // ── Phased execution ─────────────────────────────────────────────

    public Task StartEngineAsync(CancellationToken ct = default)
    {
        AllocatePorts();

        var ctid = CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId");
        var pwdFile = CtraderTestHelpers.ResolveCredential("PwdFile", "CTrader__PwdFile");
        var account = CtraderTestHelpers.ResolveCredential("Account", "CTrader__Account");
        if (string.IsNullOrEmpty(ctid))
            throw new InvalidOperationException("No cTrader credentials configured.");

        var solutionRoot = CtraderTestHelpers.SolutionRoot;
        var algoPath = CtraderTestHelpers.ResolveAlgo();
        var preloadedConfig = BuildConfig(_symbol, _period);

        _transport = new NetMqMessageTransport(
            $"tcp://127.0.0.1:{_dataPort}",
            $"tcp://*:{_commandPort}",
            Substitute.For<ILogger<NetMqMessageTransport>>());

        var adapterLogger = Substitute.For<ILogger<CTraderBrokerAdapter>>();
        var adapter = new CTraderBrokerAdapter(_transport, adapterLogger);

        _host = EngineHostFactory.Create(new EngineHostOptions
        {
            RunId = Artifacts.RunId,
            Mode = EngineMode.Backtest,
            AdapterFactory = _ => adapter,
            DbPath = Artifacts.DbPath,
            SolutionRoot = solutionRoot,
            SymbolNames = _symbols,
            ActiveStrategyIds = _activeStrategyIds,
            MinLogLevel = LogLevel.Warning,
            PreloadedConfig = preloadedConfig,
        });
        EngineHostFactory.WireEventHandlers(_host);
        EngineHostFactory.WireRiskRules(_host);

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.Database.EnsureCreated();
        }

        try
        {
            _host.StartAsync(ct).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _host.Dispose();
            throw new E2EEngineStartException(Artifacts.RunId, ex);
        }

        return Task.CompletedTask;
    }

    public async Task StartCtraderAsync(CancellationToken ct = default)
    {
        if (_transport is null) throw new InvalidOperationException("Engine not started.");

        // Start snapshot recording BEFORE CLI launch (captures hello/bar messages)
        if (_snapshotPath is not null)
        {
            _recorder = new SnapshotRecorderSession(_snapshotPath, _transport);
            await _recorder.StartAsync(_symbol, _period, Artifacts.RunId);
        }

        var ctid = CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId");
        var pwdFile = CtraderTestHelpers.ResolveCredential("PwdFile", "CTrader__PwdFile");
        var account = CtraderTestHelpers.ResolveCredential("Account", "CTrader__Account");
        var algoPath = CtraderTestHelpers.ResolveAlgo();

        var request = new BacktestCliRequest
        {
            AlgoPath = algoPath,
            Symbol = _symbol,
            Period = _period,
            Start = _start,
            End = _end,
            CtId = ctid,
            PwdFile = pwdFile,
            Account = account,
            DataPort = _dataPort,
            CommandPort = _commandPort,
            Balance = _balance,
            CommissionPerMillion = _commissionPerMillion,
            SpreadPips = _spreadPips,
            DataMode = _dataMode,
            Symbols = _symbols,
            Periods = _periods,
            FullAccess = true,
            ReportDir = Artifacts.CbotReportDir,
        };

        _ctraderStartUtc = DateTime.UtcNow;
        _cliResult = await BacktestCli.InvokeAsync(request, ct);

        // Primary: the cBot's OWN report.json + events.json (ShamshirTradeLogger) — always written on
        // stop, independent of cTrader-cli's crash-prone report-saving.
        CollectCbotReports();

        // Secondary (best-effort): harvest cTrader's native report.html for human viewing, and fall
        // back to its events.json/embedded summary if the cBot logger produced nothing.
        CtraderReportHarvester.Harvest(
            algoPath, _ctraderStartUtc,
            Artifacts.ReportHtmlPath,
            File.Exists(Artifacts.EventsJsonPath) ? Artifacts.EventsJsonPath + ".ctrader" : Artifacts.EventsJsonPath,
            File.Exists(Artifacts.ReportJsonPath) ? Artifacts.ReportJsonPath + ".ctrader" : Artifacts.ReportJsonPath);

        // Write CLI stdout/stderr to artifact files
        await File.WriteAllTextAsync(Artifacts.CtraderLogPath,
            $"EXIT CODE: {_cliResult.ExitCode}\n\nSTDOUT:\n{_cliResult.StdOut}\n\nSTDERR:\n{_cliResult.StdErr}", ct);

        if (_cliResult.ExitCode != 0 && !_cliResult.IsKnownCrash && _cliResult.CbotLines.Count == 0)
        {
            throw new E2ECliException(Artifacts.RunId, _cliResult.ExitCode, _cliResult.StdErr,
                "CLI exited with error and no CBOT output. Check " + Artifacts.CtraderLogPath);
        }

        // Stop recording after CLI finishes
        if (_recorder is not null)
        {
            await _recorder.DisposeAsync();
        }
    }

    public async Task WaitForHandshakeAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_transport is null) throw new InvalidOperationException("Engine not started.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.IsCancellationRequested)
        {
            var status = _transport.Current;
            if (status.Phase >= TransportPhase.HandshakeReceived)
                return;

            await Task.Delay(100, ct);
        }

        var currentStatus = _transport.Current;
        throw new E2EHandshakeException(Artifacts.RunId, currentStatus,
            $"Handshake not completed within {timeout.TotalSeconds:F0}s. Current phase: {currentStatus.Phase}");
    }

    public async Task WaitForCompletionAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_host is null) throw new InvalidOperationException("Engine not started.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var engineWorker = _host.Services.GetRequiredService<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>()
            .OfType<EngineWorker>().FirstOrDefault();

        while (!cts.IsCancellationRequested)
        {
            if (_host is IAsyncDisposable)
            {
                // For backtest mode, the engine stops when all bars processed
                // Poll for completion via DB trade count stabilization
                await Task.Delay(2000, ct);

                using var scope = _host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                // iter-36 K5: completion is detected from the single StepRecord journal (JournalEntries) —
                // the old BarEvaluations table is no longer written by the kernel engine.
                var journalCount = await db.JournalEntries.CountAsync(e => e.RunId == Artifacts.RunId, ct);
                if (journalCount > 0)
                {
                    await Task.Delay(2000, ct); // let final persistence settle
                    return;
                }
            }
        }

        throw new E2ECompletionException(Artifacts.RunId,
            $"Backtest did not complete within {timeout.TotalSeconds:F0}s.");
    }

    // ── Combined convenience ─────────────────────────────────────────

    public async Task<E2EResult> RunAsync(CancellationToken ct = default)
    {
        await StartEngineAsync(ct);
        await StartCtraderAsync(ct);
        await WaitForHandshakeAsync(TimeSpan.FromSeconds(30), ct);
        await WaitForCompletionAsync(TimeSpan.FromMinutes(5), ct);

        // Settle — let persistence handlers flush
        await Task.Delay(2000, ct);

        if (_host is not null)
        {
            try { await _host.StopAsync(CancellationToken.None); } catch { }
        }

        return CollectResult();
    }

    // ── Result collection ────────────────────────────────────────────

    public E2EResult CollectResult()
    {
        int trades = 0, barEvals = 0, signals = 0, orders = 0, execs = 0;
        var tradeRows = new List<E2ETradeRow>();

        if (_host is not null)
        {
            try
            {
                using var scope = _host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                trades = db.Trades.Count(t => t.RunId == Artifacts.RunId);
                // iter-36 K5: "bars flowed" is now measured by StepRecord journal entries (the single
                // journal); the BarEvaluations table is no longer written.
                barEvals = db.JournalEntries.Count(e => e.RunId == Artifacts.RunId);

                var dbTrades = db.Trades.Where(t => t.RunId == Artifacts.RunId)
                    .OrderBy(t => t.ClosedAtUtc).ToList();
                foreach (var t in dbTrades)
                {
                    tradeRows.Add(new E2ETradeRow(t.Symbol, t.Direction, t.Lots,
                        t.EntryPrice, t.ExitPrice, t.GrossPnLAmount, t.CommissionAmount, t.SwapAmount,
                        t.NetPnLAmount, t.PnLPips, t.RMultiple, t.ExitReason, t.StrategyId ?? ""));
                }
            }
            catch (Exception ex)
            {
                Diag($"Failed to collect DB stats: {ex.Message}");
            }
        }

        TransportStatusRecord? transportStatus = null;
        if (_transport is not null)
        {
            var ts = _transport.Current;
            transportStatus = new TransportStatusRecord(ts.Phase.ToString(), ts.ConnectedAtUtc,
                ts.LastMessageAtUtc, ts.BarsReceived, ts.CommandsSent, ts.ExecutionsReceived, ts.LastError);
        }

        var reportPath = File.Exists(Artifacts.ReportJsonPath) ? Artifacts.ReportJsonPath
            : File.Exists(Artifacts.EventsJsonPath) ? Artifacts.EventsJsonPath : null;

        return new E2EResult(Artifacts.RunId, trades, barEvals, signals, orders, execs,
            _cliResult?.ExitCode ?? -1, _cliResult?.StdErr ?? "",
            reportPath, tradeRows, transportStatus);
    }

    // ── Internal helpers ─────────────────────────────────────────────

    private void AllocatePorts()
    {
        using var a = new TcpListener(System.Net.IPAddress.Loopback, 0);
        using var b = new TcpListener(System.Net.IPAddress.Loopback, 0);
        a.Start(); b.Start();
        _dataPort = ((System.Net.IPEndPoint)a.LocalEndpoint).Port;
        _commandPort = ((System.Net.IPEndPoint)b.LocalEndpoint).Port;
        a.Stop(); b.Stop();
    }

    // Copy the cBot's own report.json + events.json (written by ShamshirTradeLogger into CbotReportDir)
    // to the canonical artifact paths. These are the primary, crash-resilient venue ledger.
    private void CollectCbotReports()
    {
        try
        {
            var cbotReport = Path.Combine(Artifacts.CbotReportDir, CtraderReportHarvester.CbotReportFileName);
            var cbotEvents = Path.Combine(Artifacts.CbotReportDir, CtraderReportHarvester.CbotEventsFileName);
            if (File.Exists(cbotReport) && new FileInfo(cbotReport).Length > 0)
                File.Copy(cbotReport, Artifacts.ReportJsonPath, overwrite: true);
            if (File.Exists(cbotEvents) && new FileInfo(cbotEvents).Length > 0)
                File.Copy(cbotEvents, Artifacts.EventsJsonPath, overwrite: true);
        }
        catch { /* best-effort */ }
    }

    private static LoadedConfig BuildConfig(string symbol, string period)
    {
        var solutionRoot = CtraderTestHelpers.SolutionRoot;
        var baseConfig = new ConfigLoader(solutionRoot).Load();
        var adapted = baseConfig.StrategyConfigs.Select(s => new StrategyConfigEntry(
            s.Id, s.DisplayName, s.Enabled, s.RiskProfileId, s.Parameters)
        {
            RegimeFilter = s.RegimeFilter,
            OrderEntry = s.OrderEntry,
            PositionManagement = s.PositionManagement,
            Reentry = s.Reentry,
        }).ToList();

        return new LoadedConfig(baseConfig.PropFirms, baseConfig.RiskProfiles)
        {
            StrategyConfigs = adapted,
            NewsWindows = baseConfig.NewsWindows,
            StrategyRotation = baseConfig.StrategyRotation,
            Governor = baseConfig.Governor,
            SizingPolicy = baseConfig.SizingPolicy,
        };
    }

    private void Diag(string msg) => _diagLog.Add($"[{Artifacts.RunId}] {msg}");

    public async ValueTask DisposeAsync()
    {
        if (_recorder is not null)
            await _recorder.DisposeAsync();
        if (_host is not null)
        {
            try { await _host.StopAsync(CancellationToken.None); } catch { }
            _host.Dispose();
        }
        await Artifacts.DisposeAsync();
    }
}

// ── E2E Exception types ──────────────────────────────────────────

public sealed class E2EEngineStartException(string runId, Exception inner)
    : Exception($"Engine start failed for run {runId}. Check artifacts at {runId}.", inner);

public sealed class E2ECliException(string runId, int exitCode, string stderr, string message)
    : Exception($"cTrader CLI failed for run {runId} (exit {exitCode}): {message}. Stderr: {stderr}");

public sealed class E2EHandshakeException(string runId, TransportStatus status, string message)
    : Exception($"Handshake failed for run {runId}. Phase={status.Phase}. {message}");

public sealed class E2ECompletionException(string runId, string message)
    : Exception($"Backtest completion failed for run {runId}: {message}");
