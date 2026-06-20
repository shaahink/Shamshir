using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Caching;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.Bars;

/// <summary>
/// iter-37 K-GAP-3 — the kernel path persists per-run bars so the chart renders for LIVE + non-catalog
/// runs (backtest-over-catalog already has bars). EngineRunner.ReportBar now publishes a BarIngested per
/// bar; this proves the persistence path it feeds (BarPersistenceHandler → BufferedBarWriter →
/// IBarRepository) writes RunId-keyed bars that read back by runId.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class PerRunBarPersistenceTests : IDisposable
{
    private readonly SqliteInMemory _db = new();
    private readonly ServiceProvider _sp;

    public PerRunBarPersistenceTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TradingDbContext>(o => o.UseSqlite(_db.Connection));
        services.AddScoped<IBarRepository, SqliteBarRepository>();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() { _sp.Dispose(); _db.Dispose(); }

    [Fact]
    public async Task BarIngested_PersistsPerRunBars()
    {
        const string runId = "run-bars";
        var sym = Symbol.Parse("EURUSD");
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var writer = new BufferedBarWriter(_sp.GetRequiredService<IServiceScopeFactory>());
        var handler = new BarPersistenceHandler(writer);

        for (var i = 0; i < 5; i++)
        {
            await handler.HandleAsync(
                new BarIngested(runId, new Bar(sym, Timeframe.H1, t0.AddHours(i), 1.10m, 1.11m, 1.09m, 1.105m, 1000)),
                CancellationToken.None);
        }

        await writer.FlushAsync();

        await using var scope = _sp.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBarRepository>();
        var bars = await repo.GetAsync(runId, sym, Timeframe.H1, t0, t0.AddHours(10), CancellationToken.None);

        bars.Should().HaveCount(5, "the kernel path's per-bar BarIngested persists RunId-keyed bars for the chart");
    }
}
