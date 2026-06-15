using System.Text.Json;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Web.Pages.Trades;

public sealed class TradeDetailModel(ReportingDbContext db) : PageModel
{
    public TradeResultEntity? Trade { get; private set; }
    public string? ChartBarsJson { get; private set; }

    public async Task OnGet(Guid id)
    {
        Trade = await db.Trades.FindAsync(id);

        if (Trade is not null)
        {
            var entryPrice = Trade.EntryPrice;
            var exitPrice = Trade.ExitPrice;
            var delta = 0.0010m;

            ChartBarsJson = JsonSerializer.Serialize(new[]
            {
                new { time = ((DateTimeOffset)Trade.OpenedAtUtc.AddHours(-2)).ToUnixTimeSeconds(), open = entryPrice - delta, high = entryPrice + 0.0005m, low = entryPrice - delta - 0.0005m, close = entryPrice },
                new { time = ((DateTimeOffset)Trade.OpenedAtUtc.AddHours(-1)).ToUnixTimeSeconds(), open = entryPrice, high = entryPrice + delta, low = entryPrice - delta, close = entryPrice },
                new { time = ((DateTimeOffset)Trade.OpenedAtUtc).ToUnixTimeSeconds(), open = entryPrice, high = entryPrice + 0.0020m, low = entryPrice - 0.0020m, close = entryPrice + 0.0005m },
                new { time = ((DateTimeOffset)Trade.ClosedAtUtc.AddHours(-1)).ToUnixTimeSeconds(), open = entryPrice + 0.0005m, high = exitPrice + delta, low = exitPrice - delta, close = exitPrice - 0.0005m },
                new { time = ((DateTimeOffset)Trade.ClosedAtUtc).ToUnixTimeSeconds(), open = exitPrice - 0.0005m, high = exitPrice + 0.0005m, low = exitPrice - delta, close = exitPrice },
                new { time = ((DateTimeOffset)Trade.ClosedAtUtc.AddHours(1)).ToUnixTimeSeconds(), open = exitPrice, high = exitPrice + delta, low = exitPrice - delta, close = exitPrice + 0.0005m },
            });
        }
    }
}
