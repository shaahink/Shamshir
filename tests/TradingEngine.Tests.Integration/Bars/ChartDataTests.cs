using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Web.Api;
using TradingEngine.Web.Dtos.Trades;
using TradingEngine.Web.Services;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.Bars;

/// <summary>
/// iter-37 Phase B (K-GAP-3 / K-GAP-5) — the per-trade chart's data path:
///   B1 — catalog + per-run bars at the same timestamp collapse to one (the lightweight-charts guard).
///   B2 — the trade-detail API exposes a real timeframe so the chart fetches the right bars without the
///        SPA's <c>|| 'H1'</c> fallback (sourced from the run's period; per-trade entity column deferred).
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class ChartDataTests : IDisposable
{
    private readonly SqliteInMemory _db = new();

    private TradingDbContext NewContext() => _db.NewContext();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Bars_DedupByTimestamp()
    {
        var t1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddHours(1);
        await using (var db = NewContext())
        {
            // Same timestamp from BOTH the catalog (RunId="") and a run's own bars → must collapse to one.
            db.Bars.Add(Bar("", t1, 1.10m));
            db.Bars.Add(Bar("run-1", t1, 1.10m));
            db.Bars.Add(Bar("", t2, 1.11m));
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        var bars = await new BarQueryService(read).GetBarsAsync("EURUSD", "H1", null, null, CancellationToken.None);

        bars.Should().HaveCount(2, "duplicate timestamps collapse to one bar per timestamp");
        bars.Select(b => b.Time).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task TradeDetail_ExposesRunTimeframe_ForChartFetch()
    {
        var tradeId = Guid.NewGuid();
        await using (var db = NewContext())
        {
            db.BacktestRuns.Add(new BacktestRunEntity { RunId = "run-tf", Period = "m15" });
            db.Trades.Add(new TradeResultEntity { Id = tradeId, RunId = "run-tf", Symbol = "EURUSD", Direction = "Long" });
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        var result = await new TradesController(read, new TradingEngine.Web.Services.BarQueryService(read), Substitute.For<IExcursionRepository>()).Get(tradeId, CancellationToken.None);

        var detail = (result as OkObjectResult)!.Value as TradeDetailResponse;
        detail.Should().NotBeNull();
        detail!.Timeframe.Should().Be("M15", "the chart fetches bars at the run's timeframe (no || 'H1' fallback needed)");
    }

    private static BarEntity Bar(string runId, DateTime t, decimal price) => new()
    {
        Id = Guid.NewGuid(),
        RunId = runId,
        Symbol = "EURUSD",
        Timeframe = "H1",
        OpenTimeUtc = t,
        Open = price,
        High = price + 0.001m,
        Low = price - 0.001m,
        Close = price,
        Volume = 1,
    };
}
