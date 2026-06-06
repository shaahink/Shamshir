using System.Collections.Concurrent;
using TradingEngine.CTraderRunner;

namespace TradingEngine.Web.Services;

public sealed class BacktestOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BacktestOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, BacktestRunState> _runs = new();

    public sealed record BacktestRunState
    {
        public required string RunId { get; init; }
        public DateTime StartedAt { get; init; } = DateTime.UtcNow;
        public string Status { get; set; } = "starting";
        public BacktestResult? Result { get; set; }
        public string? Error { get; set; }
        public ConcurrentQueue<string> LogLines { get; init; } = new();
        public IReadOnlyList<string> GetLogs() => LogLines.ToArray();
    }

    public BacktestOrchestrator(IServiceScopeFactory scopeFactory, ILogger<BacktestOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public BacktestRunState Start(BacktestConfig cfg)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var state = new BacktestRunState { RunId = runId };
        _runs[runId] = state;

        state.LogLines.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {runId}...");

        _ = RunAsync(runId, cfg);

        return state;
    }

    public BacktestRunState? GetState(string runId) =>
        _runs.TryGetValue(runId, out var state) ? state : null;

    public IReadOnlyList<BacktestRunState> GetAll() => _runs.Values.ToList();

    private async Task RunAsync(string runId, BacktestConfig cfg)
    {
        var state = _runs[runId];

        try
        {
            state.Status = "running";
            state.LogLines.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] Locating ctrader-cli...");

            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var config = sp.GetRequiredService<IConfiguration>();
            var runnerLogger = sp.GetRequiredService<ILogger<BacktestRunner>>();
            var runner = new BacktestRunner(config, runnerLogger);

            state.LogLines.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] Running backtest {cfg.Symbol} {cfg.Period}...");

            var result = await runner.RunAsync(cfg);

            state.Result = result;
            state.Status = result.Success ? "completed" : "failed";
            state.Error = result.ErrorMessage;
            state.LogLines.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] Done. Exit code: {result.ExitCode}");

            if (result.Success)
            {
                try
                {
                    var repo = sp.GetRequiredService<IBacktestRunRepository>();
                    var summary = new BacktestRunSummary(
                        result.RunId, state.StartedAt, DateTime.UtcNow,
                        cfg.Symbol, result.NetProfit, result.MaxDrawdownPct,
                        result.TotalTrades, result.WinningTrades, result.WinRatePct,
                        result.ExitCode, null);
                    await repo.SaveAsync(summary, CancellationToken.None);
                    state.LogLines.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] Saved to database.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save backtest result to DB");
                    state.LogLines.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] Warning: DB save failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.Error = ex.Message;
            state.LogLines.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] Error: {ex.Message}");
            _logger.LogError(ex, "Backtest {RunId} failed", runId);
        }
    }
}
