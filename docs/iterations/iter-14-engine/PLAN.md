# Iteration 14-Engine — Core Engine Fixes + Code Quality

**Branch**: `iter/14-engine`
**Based on**: `phase/8b-bar-tracing` (includes iter-11 through iter-13-verify)
**Blazor UI (original iter-14)**: deferred until this iteration is verified

---

## Read first

- `docs/agents/HOW-TO-WORK.md`
- `docs/iterations/iter-13-verify/HANDOVER.md` — the three open issues this plan fixes
- `src/TradingEngine.Host/EngineWorker.cs` — Phases A, E
- `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` — Phase A (understand channels)
- `src/TradingEngine.Host/BarEvaluationHandler.cs` — Phase B
- `src/TradingEngine.CTraderRunner/BacktestRunner.cs` — Phase B
- `src/TradingEngine.Services/PositionTracker.cs` — Phases C, E
- `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs` — Phase D
- `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` — Phase A (SetActiveRuleSet)
- `src/TradingEngine.Host/Program.cs` lines 180–195 — SetActiveRuleSet reference pattern

---

## Design principle: no mode-specific hacks in the engine

The engine receives an `IBrokerAdapter` and must not know which concrete adapter is plugged in.
It already stores `_engineMode` (set from broker type at construction, line 78-79). Use `_engineMode`
everywhere to branch behaviour. Never write `if (_broker is BacktestReplayAdapter)` or
`if (_broker is SimulatedBrokerAdapter)` inside engine processing paths — those break the
abstraction and hide bugs by making one mode silently different from another.

The existing `_engineMode` field is the correct discriminator: it expresses *intent*
(backtest vs live), not *implementation* (which class). Any future backtest adapter
registered with `EngineMode.Backtest` will automatically get the right behaviour.

---

## Phase A — Fix 0 trades in replay (NEW-01) + risk parity (design) ← do this first

Two root causes for 0 trades, plus one risk-behaviour divergence. Fix all three.

### A1 — Redesign ExecuteAsync to branch by _engineMode

**File**: `src/TradingEngine.Host/EngineWorker.cs`

The current `Task.WhenAll(4 tasks)` model has two timing races in backtest mode:

**Race 1**: `ProcessExecutionEventsAsync` calls `ReadAllAsync` on `_broker.ExecutionStream`.
`ProcessBarsAsync` calls `TryRead` on the same reader. `ReadAllAsync` registers a channel waiter
and wins every time. Fills land in `_executionEventChannel` and sit there until the next tick
— which arrives one bar later. Positions never open in the same bar that generated the signal.

**Race 2**: `_currentEquity.Balance == 0` on bar 1. `FeedBarsAsync` writes bar → tick →
accountUpdate in that order. `ProcessBarsAsync` evaluates strategies before `ProcessTicksAsync`
has processed that bar's tick and applied the accountUpdate. Balance is 0 → DISPATCH_SKIP.

**Fix**: In `ExecuteAsync`, branch on `_engineMode` after `WarmUpIndicatorsAsync`:

```csharp
if (_engineMode == EngineMode.Backtest)
    await RunBacktestLoopAsync(ct);
else
    await Task.WhenAll(
        ProcessTicksAsync(ct),
        ProcessBarsAsync(ct),
        ProcessAccountUpdatesAsync(ct),
        ProcessExecutionEventsAsync(ct));
```

`_engineMode` is already set at line 78-79 — no new type checks required.

### A2 — Implement RunBacktestLoopAsync

**File**: `src/TradingEngine.Host/EngineWorker.cs`

New private method. Runs a single-threaded sequential loop: one bar at a time, all events
processed inline before the next bar begins. No concurrent tasks, no channel races.

Skeleton (copy matching log/progress lines from ProcessBarsAsync for the relevant steps):

```csharp
private async Task RunBacktestLoopAsync(CancellationToken ct)
{
    // Drain the initial AccountUpdate written by ConnectAsync (always present before bars start).
    // This guarantees _currentEquity.Balance > 0 before the first bar is evaluated.
    var initAcct = await _broker.AccountStream.ReadAsync(ct);
    HandleAccountUpdate(initAcct);

    try
    {
        await foreach (var bar in _broker.BarStream.ReadAllAsync(ct))
        {
            try
            {
                // [Bar history + indicator recompute — identical to ProcessBarsAsync]
                Interlocked.Increment(ref _barCount);
                var byTf = _bars.GetOrAdd(bar.Symbol, _ => new());
                var list = byTf.GetOrAdd(bar.Timeframe, _ => new());
                int barCount;
                lock (list)
                {
                    list.Add(bar);
                    if (list.Count > MaxBarHistory) list.RemoveAt(0);
                    barCount = list.Count;
                }
                await RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe);
                // [BAR_EVAL log + _progress.Report — copy from ProcessBarsAsync]

                var halfSpread = ResolveHalfSpread(bar.Symbol);
                var closeTick = new Tick(bar.Symbol, bar.Close,
                    bar.Close + halfSpread, bar.OpenTimeUtc + GetBarDuration(bar.Timeframe));
                var barSnapshot = BuildBarSnapshot(bar.Symbol);
                if (barSnapshot is null) continue;
                BuildIndicatorSnapshot(bar.Symbol);

                // [Strategy loop — identical to ProcessBarsAsync strategy loop]
                foreach (var strategy in _strategies)
                {
                    // ... same NEED_BARS check, Evaluate, BarEvaluated publish,
                    //     DISPATCH_SKIP guard, DispatchAsync, TrackOrder, ORDER log
                }

                // Drain fills from order submission directly — no intermediate channel,
                // no race with ProcessExecutionEventsAsync (it is not running in this mode)
                DrainExecutionStream();

                // Per-bar SL/TP evaluation: backtest broker does not manage orders server-side
                foreach (var (orderId, pos) in _positionTracker.OpenPositions.ToList())
                {
                    if (pos.Symbol != bar.Symbol) continue;
                    bool exit = false;
                    if (pos.Direction == TradeDirection.Long)
                    {
                        if (bar.Low <= pos.CurrentStopLoss.Value) exit = true;
                        else if (pos.TakeProfit is not null && bar.High >= pos.TakeProfit.Value.Value) exit = true;
                    }
                    else
                    {
                        if (bar.High >= pos.CurrentStopLoss.Value) exit = true;
                        else if (pos.TakeProfit is not null && bar.Low <= pos.TakeProfit.Value.Value) exit = true;
                    }
                    if (exit)
                    {
                        _logger.LogInformation("BAR_EXIT|{Id}|{Symbol}|sl={SL:F5}|tp={TP}|low={Low:F5}|high={High:F5}",
                            orderId, pos.Symbol, pos.CurrentStopLoss.Value,
                            pos.TakeProfit?.Value ?? 0, bar.Low, bar.High);
                        await _broker.ClosePositionAsync(orderId, ct);
                    }
                }

                // Drain fills from SL/TP closures
                DrainExecutionStream();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BAR_PROC_ERR|{Symbol}|{OpenTime}", bar.Symbol, bar.OpenTimeUtc);
            }
        }
    }
    catch (OperationCanceledException) { }

    _logger.LogDebug("Backtest loop stopped");
}

private void DrainExecutionStream()
{
    while (_broker.ExecutionStream.TryRead(out var execEvent))
    {
        _positionTracker.OnExecution(execEvent, _strategies);
        _logger.LogInformation("EXEC|{OrderId}|{State}|fill={Fill}|lots={Lots}",
            execEvent.OrderId, execEvent.NewState,
            execEvent.FillPrice?.Value.ToString("F5") ?? "none",
            execEvent.FilledLots);
    }
}
```

Implementation note on BacktestReplayAdapter channels (verified by reading the file):
- `_tickChannel`: unbounded — ticks accumulate harmlessly; no need to drain in backtest loop
- `_accountChannel`: bounded(500) DropOldest — won't block FeedBarsAsync; no drain needed  
- `_executionChannel`: bounded(1000) Wait — the inline `DrainExecutionStream` keeps it clear

### A3 — Remove per-bar SL/TP block from ProcessBarsAsync

**File**: `src/TradingEngine.Host/EngineWorker.cs` lines 291–314

These lines simulate broker-side SL/TP management. The real broker (NetMQ/cTrader) manages
orders server-side — `ProcessBarsAsync` must not second-guess it. With the mode branch in A1,
`ProcessBarsAsync` only runs in live mode. Remove the entire block:

```csharp
// REMOVE this entire block from ProcessBarsAsync:
foreach (var (orderId, pos) in _positionTracker.OpenPositions.ToList())
{
    if (pos.Symbol != bar.Symbol) continue;
    bool exit = false;
    ...
    if (exit)
    {
        ...
        await _broker.ClosePositionAsync(orderId, ct);
    }
}
```

The identical logic now lives only in `RunBacktestLoopAsync` where it belongs.

### A4 — Add SetActiveRuleSet to BacktestOrchestrator inner host

**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`

The inner host registers a real `RiskManager` (line 268) but never calls `SetActiveRuleSet`.
`Host/Program.cs` (lines 180–195) does call it — making risk behaviour diverge between the
replay path and the cTrader subprocess path. Backtest via Web UI has no prop firm risk gates.

After `innerHost` is built and event subscriptions are wired (after line ~342), but before
`await innerHost.StartAsync(...)`, add:

```csharp
var rm = innerHost.Services.GetRequiredService<RiskManager>();
var loaded = innerHost.Services.GetRequiredService<LoadedConfig>();
var activeRiskProfileId = loaded.StrategyConfigs
    .Select(c => c.RiskProfileId).FirstOrDefault() ?? "standard";
var activeProfile = loaded.RiskProfiles.FirstOrDefault(r => r.Id == activeRiskProfileId);
var activeRuleSetId = activeProfile?.PropFirmRuleSetId ?? "ftmo-standard";
var ruleSet = loaded.PropFirms.FirstOrDefault(r => r.Id == activeRuleSetId);
if (ruleSet is not null)
    rm.SetActiveRuleSet(ruleSet);
```

This is a direct copy of the pattern from `Host/Program.cs` lines 181–191. Verify field names
match (check `LoadedConfig` for `PropFirms`, `RiskProfiles`, `StrategyConfigs` property names).

### A5 — Verify trades appear

```powershell
dotnet build --no-incremental   # 0 errors before continuing

dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"
# Must pass

sqlite3 data\trading.db "SELECT COUNT(*) FROM Bars;"  # must be > 0 (seed if needed)
```

Start the Web app, trigger a replay backtest (`UseForBacktest=false`), wait for completion, then:

```powershell
sqlite3 data\trading.db "SELECT COUNT(*) FROM TradeResults ORDER BY Id DESC LIMIT 10;"
# Must show rows — if still 0, check DISPATCH_SKIP in logs and stop before Phase B
```

**Do not proceed to Phase B until at least one trade is in the DB.**

---

## Phase B — DB path + BarEvaluationHandler disposal (NEW-02, NEW-03)

### B1 — Pass Persistence__DbPath to engine subprocess (NEW-02)

**File**: `src/TradingEngine.CTraderRunner/BacktestRunner.cs`

`StartEngine` creates a `ProcessStartInfo` with an `Environment` dictionary (around line 147).
`Persistence__DbPath` is not in it. The engine subprocess resolves its own default path,
which differs from the Web app's resolved path. Trades from cTrader backtests go to the
wrong DB file.

In the `Environment` dict of `StartEngine`, add:

```csharp
["Persistence__DbPath"] = _config["Persistence:DbPath"]
    ?? Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "trading.db")),
```

This reads the same config key used by `Web/Program.cs` (line 11) and `Host/Program.cs`
(line 114). If `Persistence:DbPath` is configured, both processes use it. If not, both
fall back to the same formula from the same base directory.

### B2 — Fix BarEvaluationHandler remaining-drain on host dispose (NEW-03)

**File**: `src/TradingEngine.Host/BarEvaluationHandler.cs`  
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`

`BarEvaluationHandler.DisposeAsync` tries `_scopeFactory.CreateAsyncScope()` while the root
DI container is already being disposed (called from `innerHost.Dispose()`). The scope factory
throws `ObjectDisposedException` — caught by the existing `catch (Exception ex)`, logged as
a warning, and the remaining events are silently dropped.

**Fix — call the drain explicitly before host disposal, while the container is still live:**

Add a public method to `BarEvaluationHandler.cs` (after the existing `FlushLoopAsync`):

```csharp
public async Task FlushRemainingAsync()
{
    var remaining = new List<BarEvaluated>(1_000);
    while (_channel.Reader.TryRead(out var evt))
        remaining.Add(evt);

    if (remaining.Count == 0) return;

    try
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        foreach (var evt in remaining)
        {
            db.BarEvaluations.Add(new BarEvaluationEntity
            {
                Id                  = Guid.NewGuid(),
                RunId               = evt.RunId,
                Symbol              = evt.Symbol.Value,
                Timeframe           = evt.Timeframe.ToString(),
                BarOpenTimeUtc      = evt.BarOpenTimeUtc,
                StrategyId          = evt.StrategyId,
                IndicatorValuesJson = JsonSerializer.Serialize(evt.IndicatorValues),
                SignalFired         = evt.SignalFired,
                SignalDirection     = evt.SignalDirection?.ToString(),
                Reason              = evt.Reason,
                OccurredAtUtc       = evt.OccurredAtUtc,
            });
        }
        await db.SaveChangesAsync(CancellationToken.None);
        _logger.LogDebug("BarEvaluationHandler: explicit pre-dispose flush {Count}", remaining.Count);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "BarEvaluationHandler: pre-dispose flush failed");
    }
}
```

In `DisposeAsync`, remove the entire `if (remaining.Count > 0)` block that does the scope
creation — it is now replaced by `FlushRemainingAsync`. Keep the channel complete + `_cts.Cancel()
+ await _flushTask + _cts.Dispose()` unchanged. DisposeAsync becomes:

```csharp
public async ValueTask DisposeAsync()
{
    _channel.Writer.Complete();
    _cts.Cancel();
    try { await _flushTask; } catch { }
    _cts.Dispose();
}
```

In `BacktestOrchestrator.RunEngineReplayAsync`, insert the explicit flush between the delay
and `StopAsync`. Find the lines:

```csharp
await Task.Delay(5_000, cts.Token);
// ...
await innerHost.StopAsync(CancellationToken.None);
```

Change to:

```csharp
await Task.Delay(5_000, cts.Token);

var barHandler = innerHost.Services.GetRequiredService<BarEvaluationHandler>();
await barHandler.FlushRemainingAsync();

await innerHost.StopAsync(CancellationToken.None);
innerHost.Dispose();
```

---

## Phase C — Await TradeClosed publish (DESIGN-01)

**File**: `src/TradingEngine.Services/PositionTracker.cs`

`ClosePosition` (line 115) publishes `TradeClosed` fire-and-forget:
```csharp
_ = eventBus.PublishAsync(new TradeClosed(...), CancellationToken.None);
```

`TradePersistenceHandler` saves trades via this event. If the process shuts down before
the task is scheduled, the trade is lost. This is the critical financial event.

**Change `ClosePosition` to async and await the publish:**

```csharp
// BEFORE
private void ClosePosition(ExecutionEvent evt, decimal fillPrice, IEnumerable<IStrategy> strategies)

// AFTER
private async Task ClosePositionAsync(ExecutionEvent evt, decimal fillPrice, IEnumerable<IStrategy> strategies)
```

Change the publish line:
```csharp
// BEFORE
_ = eventBus.PublishAsync(new TradeClosed(...), CancellationToken.None);

// AFTER
await eventBus.PublishAsync(new TradeClosed(tradeResult, runContext.RunId, clock.UtcNow), CancellationToken.None);
```

`ClosePosition` is called from `OnExecution`. Change `OnExecution` signature:

```csharp
// BEFORE
public void OnExecution(ExecutionEvent evt, IEnumerable<IStrategy> strategies)

// AFTER
public async Task OnExecutionAsync(ExecutionEvent evt, IEnumerable<IStrategy> strategies)
```

Update the two call sites of `ClosePosition` inside `OnExecutionAsync`:
```csharp
// BEFORE
ClosePosition(evt, fillPrice, strategies);

// AFTER
await ClosePositionAsync(evt, fillPrice, strategies);
```

Update all callers of `OnExecution` throughout the codebase:

```powershell
Select-String -Path "src\**\*.cs" -Pattern "\.OnExecution\(" -Recurse | Select-Object Path, LineNumber, Line
```

Expected callers: `EngineWorker.DrainExecutionStream`, `EngineWorker.ProcessTicksAsync` finally block,
and `EngineWorker.ProcessTicksAsync` tick loop. All must be changed to `await OnExecutionAsync(...)`,
and `DrainExecutionStream` must become `async Task DrainExecutionStreamAsync()`.

**If this change causes compile errors in test projects** that mock `OnExecution`, update the
mock signatures. Run `dotnet build --no-incremental` after every sub-step.

---

## Phase D — Quick wins from code standards

### D1 — STD-02: decimal arithmetic in MeanReversionStrategy

**File**: `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs` lines 55–56

```csharp
// BEFORE
var nearLow  = (double)(latestBar.Close - latestBar.Low)  / (double)latestBar.Close < 0.002;
var nearHigh = (double)(latestBar.High  - latestBar.Close) / (double)latestBar.Close < 0.002;

// AFTER
var nearLow  = (latestBar.Close - latestBar.Low)  / latestBar.Close < 0.002m;
var nearHigh = (latestBar.High  - latestBar.Close) / latestBar.Close < 0.002m;
```

### D2 — STD-04: log bare catch in ResolveHalfSpread

**File**: `src/TradingEngine.Host/EngineWorker.cs`

Find `ResolveHalfSpread`. It has a `catch { return 0.00005m; }`. Change to:

```csharp
catch (Exception ex)
{
    _logger.LogWarning(ex, "ResolveHalfSpread failed for {Symbol} — using fallback 0.5pip", symbol);
    return 0.00005m;
}
```

### D3 — MIN-01: WinRateLast20 / AvgRLast20 never updated

**File**: `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs`

Find `OnTradeResult`. It receives a `TradeResult` but does not update `WinRateLast20` or
`AvgRLast20`. Add a rolling window of the last 20 results:

```csharp
private readonly Queue<TradeResult> _recentTrades = new();

public override void OnTradeResult(TradeResult result)
{
    _recentTrades.Enqueue(result);
    if (_recentTrades.Count > 20) _recentTrades.Dequeue();

    var wins = _recentTrades.Count(t => t.NetPnL.Amount > 0);
    WinRateLast20 = _recentTrades.Count > 0 ? (double)wins / _recentTrades.Count : 0;

    var rMultiples = _recentTrades
        .Where(t => t.RiskAmount.Amount > 0)
        .Select(t => (double)(t.NetPnL.Amount / t.RiskAmount.Amount));
    AvgRLast20 = rMultiples.Any() ? rMultiples.Average() : 0;
}
```

Check the actual field names (`WinRateLast20`, `AvgRLast20`) by reading the class before editing.

### D4 — Startup assertion for empty Bars table

**File**: `src/TradingEngine.Web/Program.cs`

After the `ctx.Database.EnsureCreated()` block (around line 29), add:

```csharp
{
    var useReplay = !builder.Configuration.GetValue<bool>("CTrader:UseForBacktest");
    if (useReplay && !ctx.Bars.Any())
        throw new InvalidOperationException(
            "Bars table is empty. Run scripts/seed-bars.ps1 before starting with CTrader:UseForBacktest=false.");
}
```

---

## Phase E — Code quality (iter-15 items, excluding EF migration)

Run `dotnet build --no-incremental` after each sub-phase.

### E1 — STD-01: Remove `await Task.CompletedTask` and dead `async`

**File**: `src/TradingEngine.Host/EngineWorker.cs`

`RecomputeIndicatorsAsync` is `async Task` with `await Task.CompletedTask` at the end
(lines ~390–417). Remove `async`, change the last line to `return Task.CompletedTask`:

```csharp
// BEFORE
private async Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf)
{
    ...
    await Task.CompletedTask;
}

// AFTER
private Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf)
{
    ...
    return Task.CompletedTask;
}
```

Same for `WarmUpIndicatorsAsync` if it has the same pattern.

`BarEvaluationHandler.HandleAsync`:
```csharp
// BEFORE
public async Task HandleAsync(BarEvaluated evt, CancellationToken ct)
{
    _channel.Writer.TryWrite(evt);
    await Task.CompletedTask;
}

// AFTER
public Task HandleAsync(BarEvaluated evt, CancellationToken ct)
{
    _channel.Writer.TryWrite(evt);
    return Task.CompletedTask;
}
```

### E2 — MIN-06: Add CancellationToken to RecomputeIndicatorsAsync

After E1, update the signature:

```csharp
private Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf, CancellationToken ct)
```

Update the single call site in `RunBacktestLoopAsync` and `ProcessBarsAsync`:

```csharp
await RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe, ct);
```

### E3 — STD-05: Materialise strategies to IReadOnlyList

**File**: `src/TradingEngine.Host/EngineWorker.cs`

```csharp
// BEFORE field
private readonly IEnumerable<IStrategy> _strategies;

// AFTER
private readonly IReadOnlyList<IStrategy> _strategies;
```

```csharp
// BEFORE constructor
_strategies = strategies;

// AFTER
_strategies = strategies.ToList();
```

Change the two `_strategies.Count()` method calls (grep for them) to `_strategies.Count`.

### E4 — DESIGN-04: Prune _processedExecutionIds after position close

**File**: `src/TradingEngine.Services/PositionTracker.cs`

In `ClosePositionAsync` (renamed in Phase C), immediately after `_openPositions.Remove(evt.OrderId)`:

```csharp
_openPositions.Remove(evt.OrderId);
_processedExecutionIds.Remove(evt.OrderId);   // ← add this
```

### E5 — MIN-05: Move EngineRunContext to Services layer

**File to delete**: `src/TradingEngine.Domain/EngineRunContext.cs`

**File to create**: `src/TradingEngine.Services/EngineRunContext.cs`

```csharp
namespace TradingEngine.Services;

public sealed record EngineRunContext(string RunId);
```

Find all callers:

```powershell
Select-String -Path "src\**\*.cs" -Pattern "EngineRunContext" -Recurse | Select-Object Path
```

For each file: if it `using TradingEngine.Domain;` only to get `EngineRunContext`, add
`using TradingEngine.Services;` (or change the existing using if Domain is otherwise unused
in that file).

Confirm Domain has no remaining reference:

```powershell
Select-String -Path "src\TradingEngine.Domain\**\*.cs" -Pattern "EngineRunContext" -Recurse
# Must return nothing
```

---

## EF Migration (AGENT-02) — NOT in this plan

The EF migration baseline (replacing raw SQL patches with `dotnet ef migrations add InitialSchema`)
is high-risk: a wrong baseline generates a destructive migration (DROP TABLE). It is documented
in `docs/iterations/iter-15/PLAN.md Phase A` with the exact 8-step procedure.

**Do not attempt it in this iteration.** Leave the raw SQL in place. It will be done in a
dedicated follow-up iteration with the 8-step procedure and a full rollback plan.

---

## Verification

Run after each phase, not just at the end:

```powershell
dotnet build --no-incremental   # 0 errors required before continuing
```

After Phase A:

```powershell
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"
# Gate test must pass

# Seed bars if needed
sqlite3 data\trading.db "SELECT COUNT(*) FROM Bars;"   # must be > 0
# Run replay backtest via API, then:
sqlite3 data\trading.db "SELECT COUNT(*) FROM TradeResults ORDER BY Id DESC LIMIT 10;"
# Must show rows — if still 0, stop and document in HANDOVER before continuing
```

After all phases:

```powershell
dotnet test tests/TradingEngine.Tests.Unit      --no-build    # 87/87
dotnet test tests/TradingEngine.Tests.Integration --no-build  # 15/15
dotnet test tests/TradingEngine.Tests.Simulation --no-build   # gate pass

# App smoke test
dotnet run --project src/TradingEngine.Web --environment Development
# Navigate to /Backtests/Run, trigger replay backtest (UseForBacktest=false)
# Confirm trades > 0 in result and in Detail page
```

---

## Forbidden

- Do not add `if (_broker is BacktestReplayAdapter)` or `if (_broker is SimulatedBrokerAdapter)` in engine processing paths — use `_engineMode` instead
- Do not touch `BacktestReplayAdapter._barChannel` or `_tickChannel` (already unbounded, correct)
- Do not change `BarStream.Completion + 5s + StopAsync` shutdown pattern
- Do not attempt EF migration
- Do not start Blazor UI work (original iter-14)
- Do not change ctrader-cli subprocess argument building

---

## Known limitation (not in scope)

`ReplayTestHarness` stubs `IRiskManager` — the gate test verifies the processing pipeline
(bar → indicator → signal → order → fill → trade), not risk rule enforcement. A separate
integration test that runs the real `RiskManager` and verifies violations are enforced is
a future TODO. Do not change `ReplayTestHarness` in this iteration.

---

## Handover notes

_(Implementing agent fills this section)_

### Verification results

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | |
| Unit tests (87) | |
| Integration tests (15) | |
| Gate test `ReplayBacktest` | |
| Trades in DB after replay backtest | |
| `OnExecution` → `OnExecutionAsync` compiles everywhere | |
| `EngineRunContext` removed from Domain | |
| `_processedExecutionIds.Remove` in ClosePositionAsync | |
| Startup assertion throws on empty Bars table | |
| `SetActiveRuleSet` called in BacktestOrchestrator | |
| Per-bar SL/TP removed from ProcessBarsAsync | |

### Issues closed

| ID | Status |
|----|--------|
| NEW-01 (0 trades — replay timing race) | |
| NEW-02 (cTrader engine wrong DB) | |
| NEW-03 (BarEvaluationHandler dispose scope) | |
| DESIGN-01 (TradeClosed fire-and-forget) | |
| DESIGN-04 (_processedExecutionIds unbounded) | |
| STD-01 (await Task.CompletedTask noise) | |
| STD-02 (double in MeanReversion) | |
| STD-04 (bare catch) | |
| STD-05 (IEnumerable materialise) | |
| STD-06 (CT on RecomputeIndicatorsAsync) | |
| MIN-01 (WinRateLast20 not updated) | |
| MIN-05 (EngineRunContext wrong layer) | |

### Deviations from the plan

_(Any constructor signature differences, property name corrections, etc.)_
