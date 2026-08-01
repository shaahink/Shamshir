using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Web.Services;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.Journal;

/// <summary>
/// iter-37 Phase J / A3 (K-GAP-4) — the per-strategy "why" funnel now reads the StepRecord journal's
/// per-bar verdicts, not the (no-longer-written) BarEvaluations table. Seeds a journal with known verdicts
/// and asserts <see cref="BacktestQueryService.GetStrategyBreakdownAsync"/> aggregates them correctly.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class StrategyBreakdownFromJournalTests : IDisposable
{
    private const string Run = "run-fnl";
    private readonly SqliteInMemory _db = new();
    private readonly ServiceProvider _sp;

    public StrategyBreakdownFromJournalTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TradingDbContext>(o => o.UseSqlite(_db.Connection));
        _sp = services.BuildServiceProvider();

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        // 3 bars. Strategy A fires once then is RSI_NEUTRAL twice; strategy B never fires (NO_REGIME ×3).
        var bars = new[]
        {
            new[] { V("A", fired: true, "OK"), V("B", fired: false, "NO_REGIME") },
            new[] { V("A", fired: false, "RSI_NEUTRAL"), V("B", fired: false, "NO_REGIME") },
            new[] { V("A", fired: false, "RSI_NEUTRAL"), V("B", fired: false, "NO_REGIME") },
        };
        for (var i = 0; i < bars.Length; i++)
        {
            db.JournalEntries.Add(new JournalEntryEntity
            {
                RunId = Run,
                Seq = i + 1,
                SimTimeUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                EventKind = "BarClosed",
                VerdictsJson = JsonSerializer.Serialize(bars[i].ToList(), opts),
            });
        }
        db.SaveChanges();
    }

    private static StrategyVerdict V(string id, bool fired, string reason) =>
        new(id, HadEnoughBars: true, SignalFired: fired, Direction: null, Reason: reason, Indicators: null);

    public void Dispose()
    {
        _sp.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task GetStrategyBreakdown_AggregatesPerBarVerdictsFromJournal()
    {
        var service = new BacktestQueryService(_sp.GetRequiredService<IServiceScopeFactory>());

        var breakdown = await service.GetStrategyBreakdownAsync(Run, CancellationToken.None);

        var a = breakdown.Single(s => s.StrategyId == "A");
        a.TotalBarsEvaluated.Should().Be(3);
        a.SignalsFired.Should().Be(1, "strategy A fired on one bar");
        a.TopRejections.Should().ContainEquivalentOf(new NoSignalReason("RSI_NEUTRAL", 2), "its no-signal reason + count");

        var b = breakdown.Single(s => s.StrategyId == "B");
        b.SignalsFired.Should().Be(0, "strategy B never fired");
        b.TopRejections.Should().ContainEquivalentOf(new NoSignalReason("NO_REGIME", 3));
    }
}
