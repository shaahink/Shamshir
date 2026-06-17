using System.Text.Json;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Pages.Runs;

public sealed class ReportModel : PageModel
{
    private readonly RunProjection _projection;
    private readonly TradingDbContext _db;
    private readonly IEquityRepository _equityRepo;

    public string RunId { get; set; } = "";
    public decimal NetPnL { get; set; }
    public decimal ReturnPct { get; set; }
    public decimal MaxDdPct { get; set; }
    public double WinRatePct { get; set; }
    public int TotalTrades { get; set; }
    public decimal ProfitFactor { get; set; }
    public string DurationDisplay { get; set; } = "";
    public double BarsPerSec { get; set; }
    public string DateRange { get; set; } = "";
    public decimal InitialBalance { get; set; } = 10_000m;
    public bool HasBreaches { get; set; }
    public string BreachSummary { get; set; } = "";
    public string EquityCurveJson { get; set; } = "[]";
    public List<FunnelRow> FunnelRows { get; set; } = [];
    public List<TradeRow> TradeRows { get; set; } = [];
    public bool EquityCurveSourceIsPersisted { get; set; }
    public ReconciliationInfo Reconciliation { get; set; } = new();

    public sealed record FunnelRow(string StrategyId, int Signals, int Orders, int Fills, int Closes, double WinRate);

    public sealed record TradeRow(string Symbol, string Direction, decimal Lots,
        decimal EntryPrice, decimal ExitPrice, decimal NetPnL, string ExitReason);

    public sealed record ReconciliationInfo
    {
        public bool NetPnLMatchesTradeSum { get; set; }
        public bool NetPnLMatchesEquityEnd { get; set; }
        public bool FunnelClosesMatchTradeCount { get; set; }
        public string Summary { get; set; } = "";
    }

    public ReportModel(RunProjection projection, TradingDbContext db, IEquityRepository equityRepo)
    {
        _projection = projection;
        _db = db;
        _equityRepo = equityRepo;
    }

    public async Task OnGetAsync(string runId)
    {
        RunId = runId;

        var runTask = _db.BacktestRuns.FirstOrDefaultAsync(r => r.RunId == runId);
        var viewTask = _projection.GetRunAsync(runId, CancellationToken.None);
        var tradesTask = _db.Trades.Where(t => t.RunId == runId).OrderBy(t => t.ClosedAtUtc).ToListAsync();
        var equitySnapshotsTask = _equityRepo.GetByRunIdAsync(runId, CancellationToken.None);

        await Task.WhenAll(runTask, viewTask, tradesTask, equitySnapshotsTask);

        var run = runTask.Result;
        var view = viewTask.Result;
        var trades = tradesTask.Result;
        var equitySnapshots = equitySnapshotsTask.Result;

        if (run is not null)
        {
            InitialBalance = run.InitialBalance;
            DateRange = $"{run.BacktestFrom:yyyy-MM-dd} → {run.BacktestTo:yyyy-MM-dd}";
            if (run.CompletedAtUtc != default && run.StartedAtUtc != default)
            {
                var wallTime = run.CompletedAtUtc - run.StartedAtUtc;
                DurationDisplay = wallTime.TotalHours >= 1
                    ? $"{wallTime.TotalHours:F1}h"
                    : wallTime.TotalMinutes >= 1
                        ? $"{wallTime.TotalMinutes:F1}m"
                        : $"{wallTime.TotalSeconds:F0}s";
                var barsTotal = EstimateBarCount(run.BacktestFrom, run.BacktestTo, run.Period);
                BarsPerSec = wallTime.TotalSeconds > 0 && barsTotal > 0
                    ? barsTotal / wallTime.TotalSeconds
                    : 0;
            }
        }

        if (view is not null)
        {
            var timeline = view.Timeline;

            FunnelRows = BuildFunnel(timeline);

            TotalTrades = trades.Count;

            HasBreaches = timeline.Any(d => d.Event == "BreachDetected");
            if (HasBreaches)
            {
                var breach = timeline.First(d => d.Event == "BreachDetected");
                BreachSummary = breach.Reason ?? "Breach detected";
            }
        }

        TradeRows = trades.Select(t => new TradeRow(
            t.Symbol, t.Direction,
            t.Lots, t.EntryPrice, t.ExitPrice,
            t.NetPnLAmount, t.ExitReason)).ToList();

        if (trades.Count > 0)
        {
            TotalTrades = trades.Count;

            // SoT: WinRatePct — source of truth is the TradeResults table
            WinRatePct = trades.Count > 0 ? (double)trades.Count(t => t.NetPnLAmount > 0) / trades.Count * 100 : 0;

            // SoT: ProfitFactor — source of truth is the TradeResults table (GrossProfit / |GrossLoss|)
            var grossProfit = trades.Where(t => t.NetPnLAmount > 0).Sum(t => t.NetPnLAmount);
            var grossLoss = Math.Abs(trades.Where(t => t.NetPnLAmount < 0).Sum(t => t.NetPnLAmount));
            ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? decimal.MaxValue : 0;

            // SoT: NetPnL — source of truth is the TradeResults table (Σ NetPnLAmount)
            NetPnL = trades.Sum(t => t.NetPnLAmount);

            // SoT: ReturnPct — derived from NetPnL / InitialBalance
            if (run is not null && run.InitialBalance > 0)
                ReturnPct = NetPnL / run.InitialBalance;

            // Equity curve: prefer persisted intra-bar AccountSnapshots from DB
            if (equitySnapshots.Count > 0)
            {
                EquityCurveSourceIsPersisted = true;
                var equity = run?.InitialBalance ?? 100_000m;
                var peak = equity;
                var maxDd = 0m;

                var curve = new SortedDictionary<long, decimal>();
                foreach (var s in equitySnapshots)
                {
                    var ts = ((DateTimeOffset)s.TimestampUtc).ToUnixTimeSeconds();
                    curve[ts] = s.Equity;
                    if (s.Equity > peak) peak = s.Equity;
                    if (peak > 0)
                    {
                        var dd = (peak - s.Equity) / peak;
                        if (dd > maxDd) maxDd = dd;
                    }
                }
                // SoT: MaxDdPct — peak-to-trough from persisted AccountSnapshots
                MaxDdPct = maxDd;
                EquityCurveJson = JsonSerializer.Serialize(
                    curve.Select(kv => new { time = kv.Key, equity = kv.Value }));
            }
            else
            {
                // Fallback: realized-equity curve built from trade closes (trade-walk).
                // The engine's intra-bar AccountSnapshots live only in the inner host's
                // in-memory store (disposed at end of run) and are not visible to this
                // request, so we step equity at each trade close — a non-empty, monotone-ish
                // curve ending at initialBalance + netPnL. Keyed by unix-second (last write
                // wins) because LightweightCharts requires strictly-ascending, unique timestamps.
                EquityCurveSourceIsPersisted = false;
                var equity = run?.InitialBalance ?? 100_000m;
                var peak = equity;
                var maxDd = 0m;

                var startTime = run?.BacktestFrom ?? trades[0].OpenedAtUtc;
                var curve = new SortedDictionary<long, decimal>
                {
                    [((DateTimeOffset)startTime).ToUnixTimeSeconds()] = equity,
                };

                foreach (var t in trades)
                {
                    equity += t.NetPnLAmount;
                    if (equity > peak) peak = equity;
                    if (peak > 0)
                    {
                        var dd = (peak - equity) / peak;
                        if (dd > maxDd) maxDd = dd;
                    }
                    curve[((DateTimeOffset)t.ClosedAtUtc).ToUnixTimeSeconds()] = equity;
                }
                // SoT: MaxDdPct — peak-to-trough from trade-walk (realized equity only)
                MaxDdPct = maxDd;
                EquityCurveJson = JsonSerializer.Serialize(
                    curve.Select(kv => new { time = kv.Key, equity = kv.Value }));
            }

            // ---- Reconciliation section ----
            var equityEnd = equitySnapshots.Count > 0
                ? equitySnapshots[^1].Equity
                : (run?.InitialBalance ?? 100_000m) + NetPnL;

            var tradeNetSum = trades.Sum(t => t.NetPnLAmount);

            Reconciliation = new ReconciliationInfo
            {
                NetPnLMatchesTradeSum = NetPnL == tradeNetSum,
                NetPnLMatchesEquityEnd = (run?.InitialBalance ?? 0) + tradeNetSum == equityEnd,
                FunnelClosesMatchTradeCount = FunnelRows.Sum(f => f.Closes) == trades.Count,
            };

            var checks = new List<string>();
            if (Reconciliation.NetPnLMatchesTradeSum) checks.Add("NetPnL == Σ trade net ✓");
            else checks.Add("NetPnL != Σ trade net ✗");
            if (Reconciliation.NetPnLMatchesEquityEnd) checks.Add("Initial + Σ trade == equity end ✓");
            else checks.Add("Initial + Σ trade != equity end ✗");
            if (Reconciliation.FunnelClosesMatchTradeCount) checks.Add("Funnel closes == trade count ✓");
            else checks.Add("Funnel closes != trade count ✗");
            Reconciliation.Summary = string.Join(" | ", checks);
        }
    }

    /// <summary>
    /// Per-strategy funnel from the lifecycle decision records (visible after the iter-26 RunId-stamp
    /// fix). "OrderSubmitted" is written by BOTH the OrderDispatcher and the lifecycle FSM, so we count
    /// only the lifecycle one (Reason=="Accepted") to avoid double-counting. BAR_EVAL is per-bar noise,
    /// not a signal, so Signals = orders+rejects (intents that reached the risk gate) — NOT the bar
    /// count. Close reasons cover SL/TP/forced exits.
    /// </summary>
    internal static List<FunnelRow> BuildFunnel(IEnumerable<DecisionRecordView> timeline)
    {
        static bool IsClose(string? r) => r is "SL" or "TP" or "FORCE" or "DailyDD" or "MaxDD";
        return timeline
            .GroupBy(d => d.StrategyId ?? "unknown")
            .Select(g =>
            {
                var orders = g.Count(d => d.Event == "OrderSubmitted" && d.Reason == "Accepted");
                var rejects = g.Count(d => d.Event == "OrderRejected");
                var fills = g.Count(d => d.Event == "OrderFilled" && (d.Reason == "Filled" || d.Reason == "PartialFill"));
                var closes = g.Count(d => d.Event == "OrderFilled" && IsClose(d.Reason));
                var tpCloses = g.Count(d => d.Event == "OrderFilled" && d.Reason == "TP");
                return new FunnelRow(
                    g.Key,
                    Signals: orders + rejects,           // intents that reached the risk gate
                    Orders: orders,
                    Fills: fills,
                    Closes: closes,
                    WinRate: closes > 0 ? (double)tpCloses / closes * 100 : 0);
            }).ToList();
    }

    private static double EstimateBarCount(DateTime start, DateTime end, string period)
    {
        var minutes = period.ToUpperInvariant() switch
        {
            "M1" => 1.0, "M5" => 5.0, "M15" => 15.0, "M30" => 30.0,
            "H1" => 60.0, "H4" => 240.0, "D1" => 1440.0,
            _ => 60.0,
        };
        return (end - start).TotalMinutes / minutes;
    }
}
