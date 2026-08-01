using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation;

public sealed class BacktestReplayTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<Bar> MakeBars(int count, decimal startClose = 1.1000m)
    {
        var bars = new List<Bar>(count);
        for (var i = 0; i < count; i++)
        {
            bars.Add(new Bar(Eurusd, Timeframe.H1, T0.AddHours(i),
                startClose + 0.0002m, startClose + 0.0010m, startClose - 0.0010m, startClose, 1000));
        }
        return bars;
    }

    [Fact(Timeout = 60_000)]
    public async Task ReplayBacktest_FullPipeline_ProducesJournalEntries()
    {
        const int barCount = 50;
        var bars = MakeBars(barCount);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var harness = await ReplayTestHarness.CreateAsync(bars);

        await harness.RunAsync(cts.Token);

        using var scope = harness.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        // iter-36 K5: per-bar "why" + decisions now land on the single lossless StepRecord journal
        // (JournalEntries), not the deleted BarEvaluations table.
        var journalCount = await db.JournalEntries.CountAsync();
        journalCount.Should().BeGreaterThan(0,
            "bars should flow through the kernel pipeline and produce StepRecord journal entries");

        var trades = await db.Trades.ToListAsync();
        foreach (var t in trades)
        {
            t.EntryPrice.Should().BeGreaterThan(0, $"trade {t.Id} must have entry price");
            t.ExitPrice.Should().BeGreaterThan(0, $"trade {t.Id} must have exit price (BUG-03 check)");
            t.ExitReason.Should().NotBeNullOrEmpty($"trade {t.Id} must have exit reason");
        }

        var openPositions = await db.Positions.CountAsync(p => p.ClosedAtUtc == null);
        openPositions.Should().Be(0, "all positions should be closed by E2E test end");
    }
}
