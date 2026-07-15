using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Infrastructure.Transport.NetMq;
using TradingEngine.Infrastructure.Venues.CTrader;

namespace TradingEngine.Web.Services;

public sealed class CTraderListenService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunProgressBroadcaster _broadcaster;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CTraderListenService> _logger;
    private readonly BacktestOrchestrator _orchestrator;
    private readonly IRunDataCache? _runDataCache;

    private IHost? _host;
    private CancellationTokenSource? _hostCts;
    private ListenState _state { get; set; } = ListenState.Idle;
    private CtraderListenConfig? _config;
    private string? _activeRunId { get; set; }
    private DateTime? _sessionStartedAt;
    private decimal? _sessionBalance;
    private string[] _sessionSymbols { get; set; } = [];
    private string[] _sessionPeriods { get; set; } = [];

    public ListenState State => _state;
    public bool IsListening => _state == ListenState.Listening;
    public string? ActiveRunId => _activeRunId;
    public int DataPort { get; } = 15555;
    public int CommandPort { get; } = 15556;

    public CTraderListenService(
        IServiceScopeFactory scopeFactory,
        RunProgressBroadcaster broadcaster,
        IConfiguration configuration,
        ILogger<CTraderListenService> logger,
        BacktestOrchestrator orchestrator,
        IRunDataCache? runDataCache = null)
    {
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _configuration = configuration;
        _logger = logger;
        _orchestrator = orchestrator;
        _runDataCache = runDataCache;
    }

    public async Task StartListeningAsync(CtraderListenConfig config, CancellationToken ct = default)
    {
        if (_state != ListenState.Idle)
            throw new InvalidOperationException("Listener is already running or active.");

        _config = config;
        _state = ListenState.Listening;

        var runId = Guid.NewGuid().ToString("N")[..8];
        _activeRunId = runId;

        await WritePlaceholderRunAsync(runId, config);
        await WriteVenueSession(runId, "LISTEN|START", "Engine listening on ports 15555/15556");

        var host = BuildEngineHost(runId, config);
        _host = host;
        _hostCts = new CancellationTokenSource();

        await host.StartAsync(ct);

        _logger.LogInformation("CTRADER_LISTEN|STARTED|runId={RunId}|ports=15555/15556", runId);
    }

    public async Task StopListeningAsync(CancellationToken ct = default)
    {
        if (_host is null) return;

        _logger.LogInformation("CTRADER_LISTEN|STOPPING|runId={RunId}", _activeRunId);

        await FinalizeRunAsync();

        _hostCts?.Cancel();
        try { await _host.StopAsync(ct); } catch { }
        _host.Dispose();
        _host = null;
        _hostCts = null;
        _activeRunId = null;
        _sessionStartedAt = null;
        _sessionBalance = null;
        _sessionSymbols = [];
        _sessionPeriods = [];
        _state = ListenState.Idle;
    }

    public void Dispose()
    {
        _hostCts?.Cancel();
        _host?.Dispose();
    }

    private IHost BuildEngineHost(string runId, CtraderListenConfig config)
    {
        var dbPath = DbPathResolver.ResolveTradingDbPath(
            _configuration.GetValue<string>("Persistence:DbPath"));
        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var strategyIds = config.StrategyIds ?? [];

        var listenService = this;

        var innerHost = EngineHostFactory.Create(new EngineHostOptions
        {
            RunId = runId,
            Mode = EngineMode.Backtest,
            AdapterFactory = sp =>
            {
                var transport = new NetMqMessageTransport(
                    $"tcp://127.0.0.1:{DataPort}",
                    $"tcp://*:{CommandPort}",
                    sp.GetRequiredService<ILogger<NetMqMessageTransport>>());
                var adapter = new CTraderBrokerAdapter(transport,
                    sp.GetRequiredService<ILogger<CTraderBrokerAdapter>>());
                adapter.OnSymbolSpec = spec => sp.GetRequiredService<ISymbolInfoRegistry>().UpsertVenueSpec(spec);
                adapter.RegisterSessionStartedHandler(info => listenService.OnSessionStarted(info));
                adapter.OnStatusChange = (type, msg) =>
                {
                    _logger.LogInformation("CTRADER_LISTEN|STATUS|{Type}|{Msg}", type, msg);
                    Task.Run(async () =>
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                        db.VenueSessions.Add(new VenueSessionEntity
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
            SymbolNames = [],
            ActiveStrategyIds = strategyIds,
            DiagnosticsEnabled = _configuration.GetSection("Engine:Diagnostics").GetValue<bool>("Enabled"),
            RunDataCache = _runDataCache,
        });

        EngineHostFactory.WireEventHandlers(innerHost);
        EngineHostFactory.WireRiskRules(innerHost);

        return innerHost;
    }

    private void OnSessionStarted(SessionInfo info)
    {
        if (_state != ListenState.Listening) return;

        _state = ListenState.Active;
        _sessionStartedAt = DateTime.UtcNow;
        _sessionBalance = info.Balance;
        _sessionSymbols = info.Symbols;
        _sessionPeriods = info.Periods;

        _logger.LogInformation(
            "CTRADER_LISTEN|SESSION|runId={RunId}|mode={Mode}|symbols={Symbols}|periods={Periods}|balance={Balance}",
            _activeRunId, info.Mode, string.Join(",", info.Symbols), string.Join(",", info.Periods),
            info.Balance);

        Task.Run(async () =>
        {
            try
            {
                await UpdatePlaceholderRunAsync(_activeRunId!, info);
                await WriteVenueSession(_activeRunId!, "SESSION|CONNECTED",
                    $"Desktop cTrader connected — {info.Mode} {string.Join(",", info.Symbols)}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CTRADER_LISTEN|SESSION_UPDATE_FAILED|runId={RunId}", _activeRunId);
            }
        });
    }

    private async Task WritePlaceholderRunAsync(string runId, CtraderListenConfig config)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
        var summary = new BacktestRunSummary(
            runId, DateTime.UtcNow, DateTime.MinValue,
            "", "", "", "",
            DateTime.MinValue, DateTime.MinValue,
            config.InitialBalance ?? 100_000m, "", "{}", null,
            0, 0, 0, 0, 0, 0, 0, 0, -1, null,
            Venue: "ctrader-desktop",
            RiskProfileId: config.RiskProfileId,
            GovernorEnabled: config.GovernorEnabled,
            RegimeEnabled: !config.RegimeDisabled,
            CommissionPerMillion: config.CommissionPerMillion,
            SpreadPips: config.SpreadPips);
        await repo.SaveAsync(summary, CancellationToken.None);
    }

    private async Task UpdatePlaceholderRunAsync(string runId, SessionInfo info)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
        var existing = await repo.GetByIdAsync(runId, CancellationToken.None);
        if (existing is null) return;

        var updated = existing with
        {
            Symbol = info.Symbols.Length > 0 ? info.Symbols[0] : "",
            Period = info.Periods.Length > 0 ? info.Periods[0] : "",
            Symbols = string.Join(",", info.Symbols),
            Periods = string.Join(",", info.Periods),
            InitialBalance = info.Balance > 0 ? info.Balance : existing.InitialBalance,
        };
        await repo.UpdateAsync(updated, CancellationToken.None);
    }

    private async Task FinalizeRunAsync()
    {
        if (_activeRunId is null) return;

        _runDataCache?.MarkCompleted(_activeRunId);

        var wallElapsed = _sessionStartedAt.HasValue
            ? (long)(DateTime.UtcNow - _sessionStartedAt.Value).TotalMilliseconds
            : 0;

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
        var existing = await repo.GetByIdAsync(_activeRunId, CancellationToken.None);
        if (existing is null) return;

        var updated = existing with
        {
            CompletedAtUtc = DateTime.UtcNow,
            ExitCode = 0,
            WallElapsedMs = wallElapsed,
        };
        await repo.UpdateAsync(updated, CancellationToken.None);

        await WriteVenueSession(_activeRunId, "SESSION|END",
            $"Desktop session ended — wallMs={wallElapsed}");

        _logger.LogInformation("CTRADER_LISTEN|FINALIZED|runId={RunId}|wallMs={WallMs}", _activeRunId, wallElapsed);
    }

    private async Task WriteVenueSession(string runId, string eventType, string detail)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.VenueSessions.Add(new VenueSessionEntity
            {
                RunId = runId, Event = eventType, Detail = detail, OccurredAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write venue session {Event}", eventType);
        }
    }
}
