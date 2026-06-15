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
/// DECISION (Q1, iter-24): take the **direct-TradingLoop** path, NOT IHost. Build the collaborators
/// in-process — REAL <see cref="RiskManager"/> (+ active prop-firm rule set via WireRiskRules), real
/// OrderDispatcher / PositionTracker / IndicatorSnapshotService (reuse the TradingLoopDirectTests
/// wiring) + a minimal in-memory fake venue — and drive bars SYNCHRONOUSLY: per bar, push an
/// AccountUpdate through AccountProcessor, call <c>TradingLoop.ProcessBarAsync(bar)</c>, drain the
/// fake venue's fills into PositionTracker, run SL/TP exits. No ServiceCollection, no IHost, no async
/// stream pumps — so it's ~1s and deterministic, and <see cref="EngineHarness.WaitForQuiescenceAsync"/>
/// is unnecessary (you know when the bar loop ends). Persist to a temp SQLite via the real handlers if
/// the test asserts on the DB, else read <c>RiskManager.Drawdown</c> / the journal directly.
///
/// AGENT TODO (Phase 0a, see iter-24 PLAN.md + the Decisions section):
///   - Implement <see cref="BuildAsync"/> per the above; add a tiny in-memory <c>IBarRepository</c> (D1)
///     and a minimal <c>FakeVenue : IBrokerAdapter</c> (D2: SubmitOrder→enqueue fill, ClosePosition→
///     enqueue close; harness drains after each bar). Do NOT reuse BacktestReplayAdapter's async feed.
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
        // AGENT TODO (Q1: DIRECT drive, not IHost). Shape:
        //   var risk = new RiskManager(...); risk.SetActiveRuleSet(ruleSet); risk.SetSizePipeline(...);
        //   var fakeVenue = new FakeVenue(_symbol);            // minimal IBrokerAdapter (D2)
        //   var dispatcher = new OrderDispatcher(risk, ...); var tracker = new PositionTracker(... risk ...);
        //   var loop = new TradingLoop(fakeVenue, indicators, dispatcher, tracker, bank, regime, gate, ...);
        //   var acct = new AccountProcessor(risk, tracker, ...);
        //   foreach (bar in _bars) { acct.HandleAsync(SynthAcctUpdate(balance)); await loop.ProcessBarAsync(bar);
        //                            DrainFills(fakeVenue, tracker); SimulateExits(bar, fakeVenue, tracker); }
        //   return new EngineHarness(risk, tracker, dbOrNull);   // assert on risk.Drawdown / tracker / db
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
