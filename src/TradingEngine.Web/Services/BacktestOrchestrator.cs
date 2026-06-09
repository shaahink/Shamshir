using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.CTraderRunner;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Adapters;
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
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, BacktestRunState> _runs = new();

    private sealed record TradeStats(decimal NetProfit, decimal MaxDrawdownPct, int TotalTrades, int WinningTrades, double WinRatePct);

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
        public IReadOnlyList<string> GetLogs() => LogLines.ToArray();
    }

    public BacktestOrchestrator(
        IServiceScopeFactory scopeFactory,
        BacktestProgressStore progressStore,
        IConfiguration configuration,
        ILogger<BacktestOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _progressStore = progressStore;
        _configuration = configuration;
        _logger = logger;
    }

    private void PushProgress(string runId, string line)
    {
        var json = JsonSerializer.Serialize(new { line });
        _progressStore.GetWriter(runId).TryWrite(json);
    }

    private void PushProgressEvent(string runId, string eventType, string message)
    {
        var json = JsonSerializer.Serialize(new { eventType, message });
        _progressStore.GetWriter(runId).TryWrite(json);
    }

    private void EnqueueLog(string runId, ConcurrentQueue<string> queue, string msg)
    {
        queue.Enqueue(msg);
        PushProgress(runId, msg);
    }

    private static Timeframe ParseTimeframe(string period) => period.ToUpperInvariant() switch
    {
        "M1"  => Timeframe.M1,
        "M5"  => Timeframe.M5,
        "M15" => Timeframe.M15,
        "M30" => Timeframe.M30,
        "H1"  => Timeframe.H1,
        "H4"  => Timeframe.H4,
        "D1"  => Timeframe.D1,
        _     => Timeframe.H1,
    };

    public BacktestRunState Start(BacktestConfig cfg)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        cfg = cfg with { RunId = runId };
        var state = new BacktestRunState { RunId = runId, Symbol = cfg.Symbol, Period = cfg.Period };
        _runs[runId] = state;

        EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {runId}...");

        _ = RunAsync(runId, cfg);

        return state;
    }

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
            state.Status = "cancelled";
    }

    private async Task RunAsync(string runId, BacktestConfig cfg)
    {
        var state = _runs[runId];
        var startedAt = state.StartedAt;

        await WriteStartRecordAsync(runId, cfg, startedAt);

        try
        {
            state.Status = "running";
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {runId}...");

            BacktestResult result;

            var useCtader = _configuration.GetValue<bool>("CTrader:UseForBacktest");
            if (useCtader)
            {
                EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running via ctrader-cli...");
                using var scope = _scopeFactory.CreateScope();
                var runnerLogger = scope.ServiceProvider.GetRequiredService<ILogger<BacktestRunner>>();
                var runner = new BacktestRunner(_configuration, runnerLogger);
                result = await runner.RunAsync(cfg);
            }
            else
            {
                EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running engine replay...");
                result = await RunEngineReplayAsync(runId, cfg, state.LogLines);
            }

            var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);

            result = result with
            {
                NetProfit      = tradeStats.NetProfit,
                MaxDrawdownPct = tradeStats.MaxDrawdownPct,
                TotalTrades    = tradeStats.TotalTrades,
                WinningTrades  = tradeStats.WinningTrades,
                WinRatePct     = tradeStats.WinRatePct,
            };

            state.Result = result;
            state.Status = result.Success ? "completed" : "failed";
            state.Error = result.ErrorMessage;

            EnqueueLog(runId, state.LogLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Done. Trades={result.TotalTrades} PnL={result.NetProfit:N2} DD={result.MaxDrawdownPct:P1}");

            await WriteEndRecordAsync(runId, cfg, startedAt, result, tradeStats);
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.Error = ex.Message;
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Error: {ex.Message}");
            _logger.LogError(ex, "Backtest {RunId} failed", runId);

            await WriteEndRecordAsync(runId, cfg, startedAt,
                new BacktestResult { RunId = runId, ExitCode = 1, ErrorMessage = ex.Message },
                new(0, 0, 0, 0, 0));
        }
        finally
        {
            var doneJson = JsonSerializer.Serialize(
                new { done = true, status = state.Status, error = state.Error });
            _progressStore.GetWriter(runId).TryWrite(doneJson);
            _progressStore.Complete(runId);
        }
    }

    private async Task WriteStartRecordAsync(string runId, BacktestConfig cfg, DateTime startedAt)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.MinValue,
                cfg.Symbol, cfg.Period, cfg.Start, cfg.End,
                cfg.Balance, "", "{}",
                0, 0, 0, 0, 0, -1, null);
            await repo.SaveAsync(summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write start record for {RunId}", runId);
        }
    }

    private async Task WriteEndRecordAsync(
        string runId, BacktestConfig cfg, DateTime startedAt,
        BacktestResult result, TradeStats stats)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.UtcNow,
                cfg.Symbol, cfg.Period, cfg.Start, cfg.End,
                cfg.Balance, result.AlgoHash, "{}",
                stats.NetProfit, stats.MaxDrawdownPct,
                stats.TotalTrades, stats.WinningTrades, stats.WinRatePct,
                result.ExitCode, result.ErrorMessage);
            await repo.UpdateAsync(summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write end record for {RunId}", runId);
        }
    }

    private async Task<BacktestResult> RunEngineReplayAsync(
        string runId, BacktestConfig cfg, ConcurrentQueue<string> logLines)
    {
        var symbol    = Symbol.Parse(cfg.Symbol);
        var timeframe = ParseTimeframe(cfg.Period);
        var from      = cfg.Start;
        var to        = cfg.End;

        var dbPath = _configuration.GetValue<string>("Persistence:DbPath")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "data", "trading.db"));

        using var scope = _scopeFactory.CreateScope();
        var barRepo = scope.ServiceProvider.GetRequiredService<IBarRepository>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

        var innerHost = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices((ctx, services) =>
            {
                services.AddSingleton(new EngineRunContext(runId));
                services.AddSingleton<IBarRepository>(_ => barRepo);
                services.AddSingleton<IBrokerAdapter>(sp =>
                    new BacktestReplayAdapter(barRepo, symbol, timeframe, from, to,
                        cfg.Balance, sp.GetRequiredService<ILogger<BacktestReplayAdapter>>()));

                var symbolInfo = new SymbolInfo(symbol, SymbolCategory.Forex, "EUR", "USD",
                    0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);
                var symbolRegistry = new SymbolInfoRegistry();
                symbolRegistry.Register(symbolInfo);
                services.AddSingleton<ISymbolInfoRegistry>(_ => symbolRegistry);

                services.AddSingleton<Func<string, string, decimal>>(_ => (fromCur, toCur) =>
                {
                    if (fromCur == "JPY" && toCur == "USD") return 1m / 149.50m;
                    if (fromCur == "GBP" && toCur == "USD") return 1.2650m;
                    return 1m;
                });

                services.AddSingleton<DrawdownTracker>();
                services.AddSingleton<INewsFilter>(_ => new NewsFilter());
                services.AddSingleton<SessionFilter>();
                services.AddSingleton<RiskManager>();
                services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());

                var solutionRoot = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
                var configLoader = new ConfigLoader(solutionRoot);
                var loadedConfig = configLoader.Load();
                services.AddSingleton(loadedConfig);
                services.AddSingleton<IRiskProfileResolver>(sp =>
                    new RiskProfileResolver(
                        sp.GetRequiredService<LoadedConfig>().RiskProfiles));

                services.AddSingleton<IEngineClock, BrokerClock>();

                services.AddDbContext<TradingDbContext>(o =>
                    o.UseSqlite($"Data Source={dbPath}"));
                services.AddScoped<ITradeRepository, SqliteTradeRepository>();
                services.AddScoped<IEquityRepository, SqliteEquityRepository>();
                services.AddSingleton<PersistenceService>();

                services.AddSingleton<IPositionManager, PositionManager>();
                services.AddSingleton<IEventBus, TypedEventBus>();
                services.AddSingleton<EquityPersistenceHandler>();
                services.AddSingleton<TradePersistenceHandler>();
                services.AddSingleton<BarEvaluationHandler>();
                services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
                services.AddSingleton<OrderDispatcher>();
                services.AddSingleton<PositionTracker>();

                var progressCallback = new Progress<BacktestProgressEvent>(evt =>
                {
                    PushProgressEvent(runId, evt.EventType, evt.Message);
                });
                services.AddSingleton<IProgress<BacktestProgressEvent>>(_ => progressCallback);

                var registry = new StrategyRegistry();
                services.AddSingleton(registry);
                services.AddSingleton<IEnumerable<IStrategy>>(sp =>
                {
                    var reg = sp.GetRequiredService<StrategyRegistry>();
                    var loaded = sp.GetRequiredService<LoadedConfig>();
                    var activeIds = loaded.StrategyConfigs.Select(c => c.Id).ToArray();
                    return reg.CreateStrategies(activeIds, loaded, sp);
                });

                services.AddSingleton<EngineWorker>(sp => new EngineWorker(
                    sp.GetRequiredService<IBrokerAdapter>(),
                    sp.GetRequiredService<IRiskManager>(),
                    sp.GetRequiredService<DrawdownTracker>(),
                    sp.GetRequiredService<IEnumerable<IStrategy>>(),
                    sp.GetRequiredService<IIndicatorService>(),
                    sp.GetRequiredService<IEventBus>(),
                    sp.GetRequiredService<IEngineClock>(),
                    sp.GetRequiredService<ISymbolInfoRegistry>(),
                    sp.GetRequiredService<IRiskProfileResolver>(),
                    sp.GetRequiredService<Func<string, string, decimal>>(),
                    sp.GetRequiredService<PersistenceService>(),
                    sp.GetRequiredService<OrderDispatcher>(),
                    sp.GetRequiredService<PositionTracker>(),
                    sp.GetRequiredService<ILogger<EngineWorker>>(),
                    sp.GetRequiredService<EngineRunContext>(),
                    dataFeed: null,
                    progress: sp.GetRequiredService<IProgress<BacktestProgressEvent>>()));
                services.AddHostedService<EngineWorker>(sp =>
                    sp.GetRequiredService<EngineWorker>());
            })
            .Build();

        var eventBus = innerHost.Services.GetRequiredService<IEventBus>();
        eventBus.Subscribe<EquityUpdated>(
            innerHost.Services.GetRequiredService<EquityPersistenceHandler>());
        eventBus.Subscribe<TradeClosed>(
            innerHost.Services.GetRequiredService<TradePersistenceHandler>());
        eventBus.Subscribe<BarEvaluated>(
            innerHost.Services.GetRequiredService<BarEvaluationHandler>());

        var rm = innerHost.Services.GetRequiredService<RiskManager>();
        var loaded = innerHost.Services.GetRequiredService<LoadedConfig>();
        var activeRiskProfileId = loaded.StrategyConfigs
            .Select(c => c.RiskProfileId).FirstOrDefault() ?? "standard";
        var activeProfile = loaded.RiskProfiles.FirstOrDefault(r => r.Id == activeRiskProfileId);
        var activeRuleSetId = activeProfile?.PropFirmRuleSetId ?? "ftmo-standard";
        var ruleSet = loaded.PropFirms.FirstOrDefault(r => r.Id == activeRuleSetId);
        if (ruleSet is not null)
            rm.SetActiveRuleSet(ruleSet);

        await innerHost.StartAsync(cts.Token);
        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Engine started. Replaying bars...");

        var adapter = innerHost.Services.GetRequiredService<IBrokerAdapter>();
        await adapter.BarStream.Completion;
        await Task.Delay(5_000, cts.Token);

        var barHandler = innerHost.Services.GetRequiredService<BarEvaluationHandler>();
        await barHandler.FlushRemainingAsync();

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Engine replay complete.");
        await innerHost.StopAsync(CancellationToken.None);
        innerHost.Dispose();

        return new BacktestResult { RunId = runId, ExitCode = 0, AlgoHash = "" };
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

            if (trades.Count == 0) return new(0, 0, 0, 0, 0);

            var netPnL  = trades.Sum(t => t.NetPnLAmount);
            var wins    = trades.Count(t => t.NetPnLAmount > 0);
            var winRate = (double)wins / trades.Count;

            var equity = initialBalance;
            var peak   = initialBalance;
            var maxDd  = 0m;
            foreach (var t in trades)
            {
                equity += t.NetPnLAmount;
                if (equity > peak) peak = equity;
                if (peak > 0)
                {
                    var dd = (peak - equity) / peak;
                    if (dd > maxDd) maxDd = dd;
                }
            }

            return new(netPnL, maxDd, trades.Count, wins, winRate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query trade stats for {RunId}", runId);
            return new(0, 0, 0, 0, 0);
        }
    }
}
