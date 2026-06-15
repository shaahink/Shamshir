using System.Text.Json;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Pages.Runs;

public sealed class ReportModel : Microsoft.AspNetCore.Mvc.RazorPages.PageModel
{
    private readonly RunProjection _projection;

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

    public ReportModel(RunProjection projection)
    {
        _projection = projection;
    }

    public async Task OnGetAsync(string runId)
    {
        RunId = runId;

        var view = await _projection.GetRunAsync(runId, CancellationToken.None);
        if (view is null) return;

        var timeline = view.Timeline;
        var equityCurve = view.EquityCurve;

        FunnelRows = timeline
            .GroupBy(d => d.StrategyId ?? "unknown")
            .Select(g => new FunnelRow(
                g.Key,
                g.Count(d => d.Event == "SIGNAL"),
                g.Count(d => d.Event == "OrderSubmitted"),
                g.Count(d => d.Event == "FILL" || d.Event == "Filled"),
                g.Count(d => d.Event == "CLOSE"),
                g.Count(d => d.Event == "CLOSE") > 0
                    ? (double)g.Count(d => d.Event == "CLOSE" && d.Reason?.Contains("TP") == true) / g.Count(d => d.Event == "CLOSE") * 100
                    : 0
            )).ToList();

        TotalTrades = timeline.Count(d => d.Event == "CLOSE");

        HasBreaches = timeline.Any(d => d.Event == "BreachDetected");
        if (HasBreaches)
        {
            var breach = timeline.First(d => d.Event == "BreachDetected");
            BreachSummary = breach.Reason ?? "Breach detected";
        }

        TradeRows = timeline
            .Where(d => d.Event == "CLOSE")
            .Select(d => new TradeRow(
                d.Symbol ?? "EURUSD", "Long", 0.01m, 0, 0, 0, d.Reason ?? "FORCE"))
            .ToList();

        if (equityCurve.Count > 0)
        {
            var points = equityCurve.Select(s => new
            {
                time = ((DateTimeOffset)s.SimTimeUtc).ToUnixTimeSeconds(),
                equity = s.Equity
            }).ToList();
            EquityCurveJson = JsonSerializer.Serialize(points);
            NetPnL = equityCurve[^1].Equity - (equityCurve.Count > 0 ? equityCurve[0].Balance : 0);
            if (equityCurve[0].Balance > 0)
                ReturnPct = NetPnL / equityCurve[0].Balance;
            MaxDdPct = equityCurve.Max(s => s.MaxDrawdown);
        }
    }
}
