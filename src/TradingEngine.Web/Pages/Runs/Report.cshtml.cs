using System.Text.Json;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Pages.Runs;

public sealed class ReportModel : PageModel
{
    private readonly RunProjection _projection;
    private readonly ReportingDbContext _db;

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

    public sealed record FunnelRow(string StrategyId, int Signals, int Orders, int Fills, int Closes, double WinRate);

    public sealed record TradeRow(string Symbol, string Direction, decimal Lots,
        decimal EntryPrice, decimal ExitPrice, decimal NetPnL, string ExitReason);

    public ReportModel(RunProjection projection, ReportingDbContext db)
    {
        _projection = projection;
        _db = db;
    }

    public async Task OnGetAsync(string runId)
    {
        RunId = runId;

        var viewTask = _projection.GetRunAsync(runId, CancellationToken.None);
        var tradesTask = _db.Trades.Where(t => t.RunId == runId).OrderBy(t => t.ClosedAtUtc).ToListAsync();

        await Task.WhenAll(viewTask, tradesTask);

        var view = viewTask.Result;
        var trades = tradesTask.Result;

        if (view is not null)
        {
            var timeline = view.Timeline;
            var equityCurve = view.EquityCurve;

            FunnelRows = timeline
                .GroupBy(d => d.StrategyId ?? "unknown")
                .Select(g => new FunnelRow(
                    g.Key,
                    g.Count(d => d.Event == "SIGNAL" || d.Event == "BAR_EVAL"),
                    g.Count(d => d.Event == "OrderSubmitted"),
                    g.Count(d => d.Event == "FILL" || d.Event == "Filled"),
                    g.Count(d => d.Event == "CLOSE"),
                    g.Count(d => d.Event == "CLOSE") > 0
                        ? (double)g.Count(d => d.Event == "CLOSE" && d.Reason?.Contains("TP") == true) / g.Count(d => d.Event == "CLOSE") * 100
                        : 0
                )).ToList();

            TotalTrades = trades.Count;

            HasBreaches = timeline.Any(d => d.Event == "BreachDetected");
            if (HasBreaches)
            {
                var breach = timeline.First(d => d.Event == "BreachDetected");
                BreachSummary = breach.Reason ?? "Breach detected";
            }

            if (equityCurve.Count > 0)
            {
                var points = equityCurve.Select(s => new
                {
                    time = ((DateTimeOffset)s.SimTimeUtc).ToUnixTimeSeconds(),
                    equity = s.Equity
                }).ToList();
                EquityCurveJson = JsonSerializer.Serialize(points);
                NetPnL = equityCurve[^1].Equity - equityCurve[0].Balance;
                if (equityCurve[0].Balance > 0)
                    ReturnPct = NetPnL / equityCurve[0].Balance;
                MaxDdPct = equityCurve.Max(s => s.MaxDrawdown);
            }
        }

        TradeRows = trades.Select(t => new TradeRow(
            t.Symbol, t.Direction,
            t.Lots, t.EntryPrice, t.ExitPrice,
            t.NetPnLAmount, t.ExitReason)).ToList();

        if (trades.Count > 0)
        {
            TotalTrades = trades.Count;
            WinRatePct = trades.Count > 0 ? (double)trades.Count(t => t.NetPnLAmount > 0) / trades.Count * 100 : 0;
            var grossProfit = trades.Where(t => t.NetPnLAmount > 0).Sum(t => t.NetPnLAmount);
            var grossLoss = Math.Abs(trades.Where(t => t.NetPnLAmount < 0).Sum(t => t.NetPnLAmount));
            ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? decimal.MaxValue : 0;
        }
    }
}
