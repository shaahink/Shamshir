using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Risk;

namespace TradingEngine.Tests.Simulation.Harness;

/// <summary>
/// SKELETON (iter-24 Phase 0). The fast, deterministic replacement for <see cref="ReplayTestHarness"/>.
///
/// Why it exists — the current harness can't validate trading/constraint behaviour:
///   1. It mocks <c>IRiskManager</c>, so it can never assert real drawdown / FTMO limits.
///   2. It blocks on <c>BarStream.Completion</c> (which ignores the CT) plus a hardcoded
///      <c>Task.Delay(5_000)</c> and full host shutdown, giving a ~60s floor that tips any
///      real-work test over its xUnit timeout.
///
/// This builder fixes both: it wires the REAL <see cref="RiskManager"/> (+ active prop-firm
/// rule set) and a deterministic in-memory broker, and it stops on QUIESCENCE — when the engine
/// has consumed every fed bar and processing has settled — never on a wall-clock delay.
///
/// AGENT TODO (Phase 0, see iter-24 PLAN.md):
///   - Implement <see cref="BuildAsync"/>: compose via the AddRisk / AddPersistence / AddStrategies
///     / AddEventInfrastructure / AddEngineWorker extensions, swap in a deterministic fake broker
///     (a real in-memory <c>IBarRepository</c> feeding <c>BacktestReplayAdapter</c> is fine), call
///     WireRiskRules so the rule set is active, and subscribe the persistence handlers.
///   - Have the fake broker expose a count of bars fed so <see cref="EngineHarness"/> can wait on
///     quiescence via <see cref="EngineHarness.WaitForQuiescenceAsync"/> instead of a fixed delay.
///   - Then move <c>BacktestActuallyTradesTests</c> here, un-skip it, and add the FTMO constraint
///     suite (daily-DD halt, max-DD halt, flatten-on-breach, lot-size == risk%).
/// </summary>
public sealed class EngineHarnessBuilder
{
    private Symbol _symbol = Symbol.Parse("EURUSD");
    private decimal _initialBalance = 10_000m;
    private string _ruleSetId = "ftmo-standard";
    private string _runId = "harness-run";
    private IReadOnlyList<Bar> _bars = [];
    private readonly List<IStrategy> _strategies = [];

    public EngineHarnessBuilder WithSymbol(Symbol symbol) { _symbol = symbol; return this; }
    public EngineHarnessBuilder WithInitialBalance(decimal balance) { _initialBalance = balance; return this; }
    public EngineHarnessBuilder WithRuleSet(string ruleSetId) { _ruleSetId = ruleSetId; return this; }
    public EngineHarnessBuilder WithRunId(string runId) { _runId = runId; return this; }
    public EngineHarnessBuilder WithBars(IReadOnlyList<Bar> bars) { _bars = bars; return this; }
    public EngineHarnessBuilder WithStrategy(IStrategy strategy) { _strategies.Add(strategy); return this; }

    public Task<EngineHarness> BuildAsync()
    {
        // AGENT TODO: real composition. The shape:
        //   var services = new ServiceCollection();
        //   services.AddSingleton(new EngineRunContext(_runId));
        //   services.AddRisk(solutionRoot);              // real RiskManager + governor + sizing
        //   services.AddPersistence(tmpDbPath);
        //   services.AddEventInfrastructure(EngineMode.Backtest);
        //   // override broker with a deterministic in-memory replay adapter fed from _bars
        //   // register _strategies (or a StrategyBank that returns them for any regime)
        //   services.AddEngineWorker(EngineMode.Backtest);
        //   var provider = services.BuildServiceProvider();
        //   provider.GetRequiredService<RiskManager>() ... WireRiskRules(_ruleSetId, _initialBalance);
        //   return new EngineHarness(provider, fakeBroker);
        _ = (_symbol, _initialBalance, _ruleSetId, _bars, _strategies);
        throw new NotImplementedException(
            "EngineHarnessBuilder.BuildAsync is the iter-24 Phase 0 deliverable — see class remarks and PLAN.md.");
    }
}

/// <summary>
/// Runtime side of <see cref="EngineHarnessBuilder"/>. Owns the engine + a deterministic broker and
/// exposes the REAL services for assertions (SQLite db, <see cref="RiskManager"/>, position state).
/// </summary>
public sealed class EngineHarness(IServiceProvider services, Func<int> barsConsumed, Func<int> barsFed)
    : IAsyncDisposable
{
    public IServiceProvider Services => services;
    public RiskManager Risk => services.GetRequiredService<RiskManager>();
    public TradingDbContext NewDbContext() => services.GetRequiredService<TradingDbContext>();

    /// <summary>
    /// Deterministic stop: wait until every fed bar has been consumed AND the consumed-count has
    /// stopped advancing for <paramref name="quietWindow"/>. No fixed wall-clock delay — fast when
    /// the run is fast, and it never under-waits a slow run. Replaces ReplayTestHarness's Task.Delay(5s).
    /// </summary>
    public async Task WaitForQuiescenceAsync(
        TimeSpan? quietWindow = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var quiet = quietWindow ?? TimeSpan.FromMilliseconds(250);
        var hardTimeout = timeout ?? TimeSpan.FromSeconds(20);
        var start = DateTime.UtcNow;
        var lastProgress = DateTime.UtcNow;
        var lastConsumed = -1;

        while (DateTime.UtcNow - start < hardTimeout)
        {
            ct.ThrowIfCancellationRequested();
            var consumed = barsConsumed();
            if (consumed != lastConsumed)
            {
                lastConsumed = consumed;
                lastProgress = DateTime.UtcNow;
            }

            var allFedConsumed = consumed >= barsFed() && barsFed() > 0;
            var settled = DateTime.UtcNow - lastProgress >= quiet;
            if (allFedConsumed && settled) return;

            await Task.Delay(25, ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        // AGENT TODO: stop the host, dispose the provider, delete the temp db file.
        return ValueTask.CompletedTask;
    }
}
