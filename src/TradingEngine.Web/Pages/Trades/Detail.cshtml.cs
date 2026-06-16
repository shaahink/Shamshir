using System.Text.Json;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Web.Pages.Trades;

public sealed class TradeDetailModel(ReportingDbContext db, TradingDbContext tradingDb, IBarRepository barRepo) : PageModel
{
    public TradeResultEntity? Trade { get; private set; }
    public string? ChartBarsJson { get; private set; }
    public bool HasRealBars { get; private set; }
    public decimal BalanceBefore { get; private set; }
    public decimal BalanceAfter { get; private set; }
    public decimal EquityBefore { get; private set; }
    public decimal EquityAfter { get; private set; }
    public bool HasAccountContext { get; private set; }

    public async Task OnGet(Guid id)
    {
        Trade = await db.Trades.FindAsync(id);

        if (Trade is not null)
        {
            var symbol = Domain.Symbol.Parse(Trade.Symbol);

            var tf = Timeframe.H1;
            if (!string.IsNullOrEmpty(Trade.RunId))
            {
                var run = await tradingDb.BacktestRuns.FirstOrDefaultAsync(r => r.RunId == Trade.RunId);
                if (run is not null && !string.IsNullOrEmpty(run.Period))
                    tf = Enum.Parse<Timeframe>(run.Period, true);
            }

            var padding = TimeSpan.FromHours(20);
            var opened = Trade.OpenedAtUtc > DateTime.MinValue.AddDays(1) ? Trade.OpenedAtUtc : Trade.ClosedAtUtc.AddHours(-1);
            var from = opened - padding;
            var to = Trade.ClosedAtUtc + padding;

            IReadOnlyList<Domain.Bar> bars;
            if (!string.IsNullOrEmpty(Trade.RunId))
            {
                bars = await barRepo.GetAsync(Trade.RunId, symbol, tf, from, to, HttpContext.RequestAborted);
            }
            else
            {
                bars = await barRepo.GetAsync(symbol, tf, from, to, HttpContext.RequestAborted);
            }

            if (bars.Count > 0)
            {
                HasRealBars = true;
                ChartBarsJson = JsonSerializer.Serialize(bars.Select(b => new
                {
                    time = new DateTimeOffset(b.OpenTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
                    open = b.Open,
                    high = b.High,
                    low = b.Low,
                    close = b.Close,
                }));
            }

            await ComputeAccountContext();
        }
    }

    private async Task ComputeAccountContext()
    {
        if (Trade is null) return;

        var tradesInRun = !string.IsNullOrEmpty(Trade.RunId)
            ? await db.Trades.Where(t => t.RunId == Trade.RunId).OrderBy(t => t.ClosedAtUtc).ToListAsync()
            : new List<TradeResultEntity> { Trade };

        decimal runningBalance = 0;
        var idx = tradesInRun.FindIndex(t => t.Id == Trade.Id);
        if (idx < 0) return;

        for (int i = 0; i < tradesInRun.Count; i++)
        {
            if (i == idx)
            {
                BalanceBefore = runningBalance;
                runningBalance += tradesInRun[i].NetPnLAmount;
                BalanceAfter = runningBalance;
            }
            else
            {
                runningBalance += tradesInRun[i].NetPnLAmount;
            }
        }

        EquityBefore = BalanceBefore;
        EquityAfter = BalanceAfter;
        HasAccountContext = tradesInRun.Count > 0;
    }
}
