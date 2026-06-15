using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Ftmo;

/// <summary>
/// RED anchor for iter-24 Phase 1. Proves the backtest path actually opens and closes
/// trades. Today it produces ZERO trades because <c>BacktestDriver._currentEquity</c> is
/// a readonly Balance=0 field, so the "equity not initialized" guard skips every dispatch.
/// This test must go GREEN once the live + backtest loops are unified.
/// </summary>
public sealed class BacktestActuallyTradesTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // A steady down-leg so an AlwaysSignal Long is stopped out (SL = entry - 0.0050),
    // producing repeated closed trades that move equity.
    private static IReadOnlyList<Bar> MakeDownLeg(int count)
    {
        var bars = new List<Bar>(count);
        var close = 1.1000m;
        for (var i = 0; i < count; i++)
        {
            close -= 0.0010m;
            bars.Add(new Bar(Eurusd, Timeframe.H1, T0.AddHours(i),
                close + 0.0010m, close + 0.0005m, close - 0.0005m, close, 1000));
        }
        return bars;
    }

    // NOTE: This documents the iter-24 Phase 1 RED→GREEN target but is Skipped because the
    // IHost-based ReplayTestHarness has a ~60s floor (5s drain + host shutdown) and mocks the
    // RiskManager, so it cannot deterministically assert trading/constraint behaviour. Phase 1
    // replaces it with a fast EngineHarnessBuilder (real RiskManager, deterministic stop); this
    // test moves there and un-skips. The zero-trades bug it targets is proven by static analysis
    // in SYSTEM-MODEL.md §3.1 and fixed by the shared ProcessSingleBarAsync loop.
    [Fact(Skip = "Needs the Phase 1 deterministic FTMO harness; see iter-24 PLAN.md", Timeout = 60_000)]
    public async Task Backtest_OnDownLeg_OpensAndClosesTrades()
    {
        var bars = MakeDownLeg(15);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await ReplayTestHarness.CreateAsync(bars);

        await harness.RunAsync(cts.Token);

        using var scope = harness.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var evalCount = await db.BarEvaluations.CountAsync();
        var posCount = await db.Positions.CountAsync();
        var tradeCount = await db.Trades.CountAsync();

        var diag = $"evals={evalCount} positions={posCount} trades={tradeCount}";
        posCount.Should().BeGreaterThan(0, $"positions should open. {diag}");
        tradeCount.Should().BeGreaterThan(0,
            $"a backtest over a clear down-leg with an always-firing strategy must open and close trades. {diag}");
    }
}
