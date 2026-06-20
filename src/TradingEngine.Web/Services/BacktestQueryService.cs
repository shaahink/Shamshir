using System.Text.Json;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Web.Services;

public sealed class BacktestQueryService : IBacktestQueryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public BacktestQueryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<BacktestRunView>> GetAllRunsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
        var summaries = await repo.GetAllAsync(ct);

        // Summaries are self-healed in the repository, so trade counts/PnL are already correct for
        // interrupted runs. Derive status from the (healed) ExitCode/ErrorMessage instead of forcing
        // every persisted run to "completed".
        return summaries.Select(s => new BacktestRunView(
            s.RunId, s.StartedAtUtc, StatusOf(s),
            s.Symbol, s.Period, s.BacktestFrom, s.BacktestTo,
            s.InitialBalance, s.NetProfit, s.MaxDrawdownPct,
            s.TotalTrades, s.WinningTrades, s.WinRatePct,
            s.AlgoHash, s.ErrorMessage, s.EffectiveConfigJson)).ToList();
    }

    public async Task<BacktestRunView?> GetRunAsync(string runId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
        var s = await repo.GetByIdAsync(runId, ct);
        if (s is null) return null;

        return new BacktestRunView(
            s.RunId, s.StartedAtUtc, StatusOf(s),
            s.Symbol, s.Period, s.BacktestFrom, s.BacktestTo,
            s.InitialBalance, s.NetProfit, s.MaxDrawdownPct,
            s.TotalTrades, s.WinningTrades, s.WinRatePct,
            s.AlgoHash, s.ErrorMessage, s.EffectiveConfigJson);
    }

    private static string StatusOf(BacktestRunSummary s) =>
        s.CompletedAtUtc == default ? "running"
        : s.ErrorMessage is not null ? "failed"
        : "completed";

    public async Task<IReadOnlyList<StrategyPerformance>> GetStrategyBreakdownAsync(
        string runId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        // iter-37 K-GAP-4 / F2: the per-strategy "why" funnel now reads the StepRecord journal's per-bar
        // verdicts. The old BarEvaluations table is no longer written after the iter-36 cutover, so the
        // breakdown was empty; the BarClosed StepRecords carry one StrategyVerdict per active strategy.
        var verdictJsons = await db.JournalEntries
            .Where(e => e.RunId == runId && e.EventKind == "BarClosed")
            .Select(e => e.VerdictsJson)
            .ToListAsync(ct);

        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var perStrategy = new Dictionary<string, (int Total, int Signals, Dictionary<string, int> NoSignal)>();
        foreach (var json in verdictJsons)
        {
            var verdicts = JsonSerializer.Deserialize<List<StrategyVerdict>>(json, opts) ?? [];
            foreach (var v in verdicts)
            {
                if (!perStrategy.TryGetValue(v.StrategyId, out var agg))
                    agg = (0, 0, new Dictionary<string, int>());
                agg.Total++;
                if (v.SignalFired)
                {
                    agg.Signals++;
                }
                else
                {
                    var reason = string.IsNullOrEmpty(v.Reason) ? "unknown" : v.Reason;
                    agg.NoSignal[reason] = agg.NoSignal.GetValueOrDefault(reason) + 1;
                }
                perStrategy[v.StrategyId] = agg;
            }
        }

        var trades = await db.Trades
            .Where(t => t.RunId == runId)
            .GroupBy(t => t.StrategyId)
            .Select(g => new
            {
                StrategyId = g.Key,
                Total = g.Count(),
                Wins = g.Count(t => t.NetPnLAmount > 0)
            })
            .ToListAsync(ct);
        var tradeIndex = trades.ToDictionary(t => t.StrategyId);

        return perStrategy.Select(kv =>
        {
            var (total, signals, noSignal) = kv.Value;
            var topRejections = noSignal
                .OrderByDescending(x => x.Value)
                .Take(5)
                .Select(x => new NoSignalReason(x.Key, x.Value))
                .ToList();

            var t = tradeIndex.GetValueOrDefault(kv.Key);
            var wins = t?.Wins ?? 0;
            var opened = t?.Total ?? 0;
            var losses = opened - wins;
            var wr = opened > 0 ? (double)wins / opened : 0d;

            return new StrategyPerformance(kv.Key, total, signals, opened, wins, losses, wr,
                topRejections.AsReadOnly());
        }).ToList();
    }

    public async Task<IReadOnlyList<EquityPoint>> GetEquityAsync(
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var query = db.EquitySnapshots.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.TimestampUtc >= from.Value);
        if (to.HasValue) query = query.Where(e => e.TimestampUtc <= to.Value);

        try
        {
            return await query
                .OrderBy(e => e.TimestampUtc)
                .Select(e => new EquityPoint(
                    e.TimestampUtc,
                    e.Equity,
                    e.Balance))
                .ToListAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }
}
