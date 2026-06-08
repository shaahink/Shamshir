using System.Collections.Concurrent;
using TradingEngine.CTraderRunner;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Web.Services;

public sealed class BacktestOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BacktestOrchestrator> _logger;
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

    public BacktestOrchestrator(IServiceScopeFactory scopeFactory, ILogger<BacktestOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private async Task StampTradesWithRunIdAsync(string runId, DateTime from, DateTime to)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Trades
                .Where(t => t.ClosedAtUtc >= from && t.ClosedAtUtc <= to && t.RunId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RunId, runId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stamp trades with RunId");
        }
    }

    private async Task<TradeStats> GetTradeStatsAsync(string runId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var trades = await db.Trades
                .Where(t => t.RunId == runId)
                .ToListAsync();

            if (trades.Count == 0) return new(0, 0, 0, 0, 0);

            var netPnL = trades.Sum(t => t.NetPnLAmount);
            var wins = trades.Count(t => t.NetPnLAmount > 0);
            var winRate = (double)wins / trades.Count;
            var maxDd = trades.Count > 0
                ? Math.Abs(trades.Min(t => t.NetPnLAmount)) / 100_000m
                : 0m;

            return new(netPnL, maxDd, trades.Count, wins, winRate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query trade stats");
            return new(0, 0, 0, 0, 0);
        }
    }

    public BacktestRunState Start(BacktestConfig cfg)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var state = new BacktestRunState { RunId = runId, Symbol = cfg.Symbol, Period = cfg.Period };
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

            await StampTradesWithRunIdAsync(runId, cfg.Start, cfg.End);
            var tradeStats = await GetTradeStatsAsync(runId);

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
            state.LogLines.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] ctrader-cli exit code: {result.ExitCode}");
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                state.LogLines.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] Error: {result.ErrorMessage}");
            if (result.TotalTrades > 0)
                state.LogLines.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] Result: {result.TotalTrades} trades, PnL={result.NetProfit:N2}, DD={result.MaxDrawdownPct:P1}");

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
            throw;
        }
    }
}
