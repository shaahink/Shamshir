# Shamshir — Iteration 2: Code Assessment & Fix Plan

> Prepared: 2026-06-06
> Based on: full code review of all 159 source files from Iteration 1
> Scope: gaps, bugs, design violations, missing implementations
> Format: same as agent-implementation-guide.md and START.md — exact code fixes, validation gates, decision votes

---

## 1. Overall Verdict

The architecture and domain model are sound. The project structure, dependency rules, EF Core setup, risk calculation primitives, and test scaffolding are all correct. However, the engine **cannot produce a single trade in its current state**. Six critical issues block execution entirely, five serious issues make financial calculations wrong even if those blocks are cleared, and several design rules are violated.

This is normal for a first iteration — the skeleton is right, the connective tissue is missing.

---

## 2. Issue Register

Severity: **CRITICAL** = blocks engine from running · **SERIOUS** = silent financial error · **MODERATE** = design violation or missing feature · **MINOR** = polish

---

### CRITICAL-1 — Three services throw at DI resolution

**Location:** `src/TradingEngine.Host/Program.cs` lines 50–55

```csharp
builder.Services.AddSingleton<IPositionManager>(_ =>
    throw new NotSupportedException("PositionManager not yet implemented"));
builder.Services.AddSingleton<IEventBus>(_ =>
    throw new NotSupportedException("EventBus not yet implemented"));
builder.Services.AddSingleton<IIndicatorService>(_ =>
    throw new NotSupportedException("IndicatorService requires Skender in Infrastructure"));
```

`EngineWorker` takes all three as constructor parameters. The DI container explodes on `app.Build()` — the engine never starts.

**Fix:** Implement real versions and register them (see Phase 2A–2C below).

---

### CRITICAL-2 — EngineWorker passes empty bars and indicators to strategies

**Location:** `src/TradingEngine.Host/EngineWorker.cs` lines 88–93

```csharp
var context = new MarketContext(
    tick.Symbol, tick,
    new Dictionary<Timeframe, IReadOnlyList<Bar>>(),   // always empty
    new Dictionary<string, double>(),                   // always empty
    _clock.UtcNow);
```

`TrendBreakoutStrategy.Evaluate()` immediately returns `null` because `h1Bars is null || h1Bars.Count < RequiredBarCount`. Zero trades are ever generated.

`ProcessBarsAsync` reads bars and throws them away:
```csharp
await foreach (var _ in _broker.BarStream.ReadAllAsync(ct)) { }  // discarded
```

**Fix:** `EngineWorker` must maintain a `Dictionary<Symbol, Dictionary<Timeframe, List<Bar>>>` that is populated in `ProcessBarsAsync`, and rebuild `IndicatorValues` per strategy on each new closed bar.

---

### CRITICAL-3 — WarmUpIndicatorsAsync is a no-op

**Location:** `src/TradingEngine.Host/EngineWorker.cs` line 149

```csharp
private async Task WarmUpIndicatorsAsync(CancellationToken ct)
{
    await Task.CompletedTask;  // does nothing
}
```

Strategies will never see enough bars to satisfy `RequiredBarCount` unless the bar accumulation in CRITICAL-2 is also fixed.

---

### CRITICAL-4 — HandleExecutionEvent and HandleAccountUpdate are empty

**Location:** `src/TradingEngine.Host/EngineWorker.cs` lines 145–146

```csharp
private void HandleExecutionEvent(ExecutionEvent evt) { }
private void HandleAccountUpdate(AccountUpdate update) { }
```

When an order fills (once SimulatedBrokerAdapter is fixed), the engine does nothing with the fill — no position is opened, no TradeResult is created, no event is published.

---

### CRITICAL-5 — SimulatedBrokerAdapter has no fill simulation

**Location:** `src/TradingEngine.Infrastructure/Adapters/SimulatedBrokerAdapter.cs` lines 28–31

```csharp
public Task SubmitOrderAsync(OrderRequest request, CancellationToken ct) => Task.CompletedTask;
public Task ModifyOrderAsync(...) => Task.CompletedTask;
public Task CancelOrderAsync(...) => Task.CompletedTask;
public Task ClosePositionAsync(...) => Task.CompletedTask;
```

Orders are submitted and silently discarded. No `ExecutionEvent` is ever pushed to `ExecutionWriter`. The engine loop drains the execution channel on every tick and finds nothing.

---

### CRITICAL-6 — DataFeedService runs to completion before EngineWorker starts processing

**Location:** `src/TradingEngine.Host/EngineWorker.cs` lines 54–59

```csharp
if (_dataFeed is not null)
{
    await _dataFeed.FeedComplete;              // waits for ALL data to be written first
}
var tasks = new[] { ProcessTicksAsync(ct), ... };
await Task.WhenAll(tasks);
```

`DataFeedService` writes every tick from the CSV into the unbounded channels, then the engine starts reading. For a 2000-bar test file this means 8000 ticks are queued in memory before processing begins. For a full year it would queue ~35,000 ticks before the engine touches any of them.

More importantly: the engine never processes ticks **as they arrive** — the core value of a streaming architecture is lost.

**Fix:** Remove the `await _dataFeed.FeedComplete` wait. Start the four processing loops immediately. `DataFeedService` and `EngineWorker` run concurrently — this is what `Task.WhenAll` is for.

---

### SERIOUS-1 — RiskManager.CalculateLotSize hardcodes pip value and SL distance

**Location:** `src/TradingEngine.Risk/RiskManager.cs` lines 66–68

```csharp
const decimal pipValue = 10m;   // hardcoded — correct only for EURUSD USD account
var slPips = 20.0;              // hardcoded — ignores actual SL on the intent
```

`PositionSizer.Calculate()` is wired correctly but receives wrong inputs. For a USDJPY trade with a 70-pip SL (worth $6.69/lot not $10/lot), the engine calculates the wrong lot size, breaching the risk target silently.

**Fix:**
```csharp
public decimal CalculateLotSize(TradeIntent intent, EquitySnapshot equity, RiskProfile profile)
{
    var symbolInfo = _symbolRegistry.Get(intent.Symbol);
    var entryPrice = intent.LimitPrice ?? new Price(equity.Equity); // use limit or approximate
    var slPips = PipCalculator.Distance(entryPrice, intent.StopLoss, symbolInfo);
    var pipValue = PipCalculator.PipValuePerLot(symbolInfo, entryPrice.Value, _getCrossRate);

    var drawdownScale = DrawdownScaler.ComputeScaleFactor(...);

    return PositionSizer.Calculate(
        equity.Equity,
        RiskPercent.Parse(profile.RiskPerTradePercent),
        slPips,
        pipValue,
        (decimal)drawdownScale,
        (decimal)symbolInfo.MaxLots,
        symbolInfo.MinLots,
        symbolInfo.LotStep);
}
```

`RiskManager` needs `ISymbolInfoRegistry` injected.

---

### SERIOUS-2 — DrawdownTracker.GetDailyLossLimit uses DailyStartEquity

**Location:** `src/TradingEngine.Risk/DrawdownTracker.cs` line 57

```csharp
public decimal GetDailyLossLimit(decimal maxDailyLossPercent) =>
    DailyStartEquity * (1 - (decimal)maxDailyLossPercent);  // WRONG
```

Domain doc §11.2 explicitly states:
```
DailyLossLimit = InitialAccountBalance × (1 - MaxDailyLossPercent)
```

Using `DailyStartEquity` means the floor rises when the account grows (e.g. profitable day starts tomorrow at $102k, daily limit becomes $96.9k not $95k). This is not how FTMO works — the floor is always based on the original challenge balance.

**Fix:**
```csharp
public decimal GetDailyLossLimit(decimal maxDailyLossPercent) =>
    InitialAccountBalance * (1 - (decimal)maxDailyLossPercent);
```

---

### SERIOUS-3 — RiskManager.OnDailyReset clears all protection modes

**Location:** `src/TradingEngine.Risk/RiskManager.cs` lines 95–100

```csharp
if (CurrentState.InProtectionMode)
{
    CurrentState = CurrentState with { InProtectionMode = false, ... };
}
```

This clears protection regardless of the cause. If max drawdown was breached (permanent breach), the engine resumes trading after midnight. START.md §D16 says: "daily DD protection resets; max DD protection does not."

**Fix:** Track protection cause:

```csharp
private ProtectionCause _protectionCause = ProtectionCause.None;

public void EnterProtectionMode(string reason, ProtectionCause cause)
{
    _protectionCause = cause;
    CurrentState = CurrentState with { InProtectionMode = true, TradingAllowed = false, ... };
}

public void OnDailyReset(decimal currentEquity)
{
    drawdownTracker.OnDailyReset(currentEquity);
    // Only clear protection if caused by daily DD — not max DD
    if (CurrentState.InProtectionMode && _protectionCause == ProtectionCause.DailyDrawdown)
    {
        _protectionCause = ProtectionCause.None;
        CurrentState = CurrentState with { InProtectionMode = false, ProtectionReason = null, TradingAllowed = true };
    }
}

public enum ProtectionCause { None, DailyDrawdown, MaxDrawdown }
```

---

### SERIOUS-4 — RiskManager.Validate missing 5 of 8 checks

**Location:** `src/TradingEngine.Risk/RiskManager.cs` lines 33–56

Only checks 1, 2, 3 are implemented. Missing:

```csharp
// 4. Max concurrent positions
if (_openPositions.Count >= profile.MaxConcurrentPositions)
    violations.Add(new("MAX_POSITIONS", $"Max concurrent positions ({profile.MaxConcurrentPositions}) reached"));

// 5. Max exposure
var totalOpenRisk = _openPositions.Values.Sum(p => p.CurrentRisk);
var newPositionRisk = ComputePositionRisk(intent, equity);
if ((totalOpenRisk + newPositionRisk) / equity.Equity > (decimal)profile.MaxExposurePercent)
    violations.Add(new("MAX_EXPOSURE", "Max total exposure exceeded"));

// 6. News window (via INewsFilter)
if (_activeRuleSet?.AllowTradesDuringNews == false && _newsFilter.IsNewsWindowActive(intent.Symbol, _clock.UtcNow))
    violations.Add(new("NEWS_WINDOW", "High-impact news window is active"));

// 7. Outside trading hours (via SessionFilter)
if (!_sessionFilter.IsWithinAllowedHours(_clock.UtcNow))
    violations.Add(new("OUTSIDE_HOURS", "Outside allowed trading hours"));

// 8. Weekend holding restriction
if (_activeRuleSet?.AllowWeekendHolding == false && _sessionFilter.IsWeekendCloseApproaching(_clock.UtcNow))
    violations.Add(new("WEEKEND_RESTRICTION", "Weekend close approaching — no new positions"));
```

`RiskManager` needs `INewsFilter`, `SessionFilter`, `IEngineClock` injected, and must track `_openPositions` (positions opened since last reset, keyed by `Guid`).

---

### SERIOUS-5 — TrendBreakoutStrategy hardcodes SymbolInfo

**Location:** `src/TradingEngine.Strategies/TrendBreakout/TrendBreakoutStrategy.cs` lines 71–78

```csharp
var sl = SlTpHelpers.AtrBased(entryPrice, entryDirection.Value, atr, p.SlAtrMultiple,
    new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
```

For GBPUSD, USDJPY, or any other symbol in `config/strategies/trend-breakout.json`, the SL and TP are computed using EURUSD's pip size and contract size. This is wrong and silent.

**Fix:** Inject `ISymbolInfoRegistry` and look up the symbol at signal time:

```csharp
private readonly ISymbolInfoRegistry _symbolRegistry;

// In Evaluate():
var symbolInfo = _symbolRegistry.Get(context.Symbol);
var sl = SlTpHelpers.AtrBased(entryPrice, entryDirection.Value, atr, p.SlAtrMultiple, symbolInfo);
var tp = SlTpHelpers.RRMultiple(entryPrice, sl, entryDirection.Value, p.TpRrMultiple, symbolInfo);
```

---

### MODERATE-1 — TrendBreakoutStrategy calls IIndicatorService directly

**Location:** `src/TradingEngine.Strategies/TrendBreakout/TrendBreakoutStrategy.cs` lines 42–43

```csharp
var atr = _indicators.Atr(h1Bars, p.AtrPeriod);
var ema = _indicators.Ema(h1Bars, p.MaPeriod);
```

Design rule (guide §6, never-do #7): "Strategies must not call `IIndicatorService` directly — they read from `context.IndicatorValues`."

This is a chicken-and-egg problem: the engine doesn't populate `IndicatorValues` (CRITICAL-2), so the strategy had to call the service directly to get any indicators.

**Fix (both sides):**

Engine side (in `ProcessBarsAsync` after accumulating a new bar):
```csharp
foreach (var strategy in _strategies)
{
    foreach (var tf in strategy.RequiredTimeframes)
    {
        if (!_bars.TryGetValue(tick.Symbol, out var byTf)) continue;
        if (!byTf.TryGetValue(tf, out var bars) || bars.Count == 0) continue;

        var readOnly = (IReadOnlyList<Bar>)bars;
        var p = GetStrategyParams(strategy); // cast to TrendBreakoutConfig or similar
        _indicatorValues[$"ATR_{p.AtrPeriod}"] = _indicators.Atr(readOnly, p.AtrPeriod);
        _indicatorValues[$"EMA_{p.MaPeriod}"] = _indicators.Ema(readOnly, p.MaPeriod);
    }
}
```

Strategy side:
```csharp
var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");
var ema = context.IndicatorValues.GetValueOrDefault($"EMA_{p.MaPeriod}");
```

However, this requires the engine to know what indicators each strategy needs. The cleanest solution: add `IReadOnlyList<IndicatorRequest> RequiredIndicators { get; }` to `IStrategy`. See Decision D21 below.

---

### MODERATE-2 — IEventBus has no implementation

`IEventBus` and `IEventHandler<T>` are defined in Domain but `TypedEventBus` does not exist anywhere in the codebase. All domain events (`TradeOpened`, `TradeClosed`, `DrawdownBreached`, `ProtectionModeEntered`, `EquityUpdated`) go unpublished, meaning the web SSE stream has no data and the event log table stays empty.

**Required:** Implement `TypedEventBus` in `TradingEngine.Infrastructure/Events/TypedEventBus.cs`.

---

### MODERATE-3 — IPositionManager has no implementation

`IPositionManager` interface is defined but no concrete class exists. Trailing stops, breakeven, and partial close don't work. The engine loop calls `_positionManager.Evaluate()` on every tick, but it throws (CRITICAL-1 means it crashes before even reaching that code).

**Required:** Implement `PositionManager` in `TradingEngine.Services/PositionManager.cs` (or `TradingEngine.Infrastructure/`) with at minimum ATR trailing and breakeven support.

---

### MODERATE-4 — SimulatedBrokerAdapter tracks no positions and sends no fills

All order methods are no-ops (CRITICAL-5). Beyond registering orders, the adapter must:
1. Store pending orders keyed by `Guid`
2. On each tick (via a `ProcessTickAsync(Tick)` method called by the engine or data feed), check pending order fills and SL/TP triggers
3. Push `ExecutionEvent` to `ExecutionWriter`

---

### MODERATE-5 — EngineTestHarness produces fake financial results

**Location:** `tests/TradingEngine.Tests.Simulation/Harness/EngineTestHarness.cs` lines 121–130

```csharp
trades.Add(new TradeResult(
    ..., new Money(50, "USD"), ..., new Money(49, "USD"), ...  // always $49 PnL
```

And the `BacktestResult`:
```csharp
return new BacktestResult(
    trades.Sum(t => t.NetPnL.Amount),
    0.05m,   // MaxDrawdown always hardcoded to 5%
    ...
```

This means simulation tests can never verify financial correctness. `FtmoViolationDetection` tests would always pass even if the risk engine is broken.

---

### MODERATE-6 — DataFeedService duplicates tick synthesis

Tick synthesis logic (4 ticks per bar, OHLC ordering) exists in:
1. `HistoricalDataProvider.SynthesizeTicks()` (used by `StreamTicksAsync`)
2. `DataFeedService` inline (hardcoded bullish ordering only, ignores bar direction)
3. `EngineTestHarness` inline

Three copies that can diverge. DataFeedService's version always produces OHLC ordering regardless of bar direction — it does not check `bar.IsBullish`. For bearish bars it feeds High before Low, which is wrong per D10.

**Fix:** `DataFeedService` must call `marketData.StreamTicksAsync()` not `StreamBarsAsync()`, and then write the ticks directly to `TickWriter`. Delete the inline synthesis.

---

### MODERATE-7 — NamedPipeBrokerAdapter.ProcessMessage is empty

**Location:** `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs` line 90

```csharp
private void ProcessMessage(string json) { }
```

Pipe messages from the cBot (Tick, Bar, AccountUpdate, ExecutionEvent) are read from the pipe but silently discarded. The live adapter is structurally complete but non-functional.

---

### MODERATE-8 — HistoricalDataProvider CSV path points to config/ not tests/data/

**Location:** `src/TradingEngine.Host/Program.cs` line 35

```csharp
new HistoricalDataProvider(Path.Combine(AppContext.BaseDirectory, "config"))
```

CSV files are in `tests/data/`. This path resolves to the `config/` directory which has no `.csv` files. Backtest mode produces zero bars silently.

---

### MINOR-1 — Program.cs uses StubClock with frozen time

```csharp
builder.Services.AddSingleton<IEngineClock>(new StubClock(DateTime.UtcNow));
```

`StubClock` is for tests. In the host, `BrokerClock` should be used. `StubClock` freezes time at startup — all tick timestamps are from when the engine started, not broker time.

---

### MINOR-2 — DataFeedService hardcodes "EURUSD"

```csharp
var symbol = Symbol.Parse("EURUSD");
```

Should read active symbols from `EngineOptions` / loaded strategy configs.

---

### MINOR-3 — EngineTestHarness uses DateTime.UtcNow directly

Line 112: `_clock.UtcNow` is referenced but it's `DateTime.UtcNow` inline, not via an `IEngineClock`. Violates the clock abstraction rule.

---

### MINOR-4 — Tests missing for SERIOUS-2 and SERIOUS-3

`DrawdownTracker.GetDailyLossLimit` uses the wrong base — but no test covers it specifically. The existing `DrawdownTrackerTests` do not assert that the daily floor is computed from `InitialAccountBalance`. Adding a test would have caught SERIOUS-2.

`RiskManager.OnDailyReset` does not have a test asserting that max DD protection persists after a reset.

---

## 3. New Decisions Required

---

### D21 — How does the engine know which indicators to pre-compute for each strategy?

**Problem:** To populate `context.IndicatorValues` before calling `strategy.Evaluate()`, the engine must know what indicators and periods each strategy needs. Currently there's no contract for this.

**Options:**
- **A (recommended):** Add `IReadOnlyList<IndicatorRequest> RequiredIndicators { get; }` to `IStrategy`. Each strategy declares what it needs. The engine computes exactly those indicators after each bar.

```csharp
public record IndicatorRequest(string Key, IndicatorType Type, int Period, double StdDev = 2.0);
// Key must match the lookup in strategy: context.IndicatorValues["ATR_14"]
// Type: Atr, Ema, Sma, Rsi, BollingerBands, Macd
```

- **B:** Pass `IIndicatorService` into `MarketContext` directly. Strategies call it themselves but the results are still cached by `SkenderIndicatorService`.
- **C:** Keep calling `IIndicatorService` from strategies (current approach). Drop the design rule.

**Recommendation: A.** It keeps strategies declarative, the engine controls when Skender is called, and caching is authoritative.

---

### D22 — Where does PositionManager live?

**Problem:** `IPositionManager` is in Domain. The implementation needs `ITrailingStopService` (in Services) and `PipCalculator` (in Services). It does not need EF Core or Skender. Should it live in Services or a new project?

**Options:**
- **A (recommended):** `TradingEngine.Services/PositionManager.cs`. Services already depends on Domain and Application. No new project needed.
- **B:** `TradingEngine.Infrastructure/PositionManager.cs`. Infrastructure depends on everything — works but inflates Infrastructure.
- **C:** New `TradingEngine.PositionManagement` project.

**Recommendation: A.**

---

### D23 — Where does TypedEventBus live?

**Problem:** `IEventBus` is in Domain. The implementation uses `System.Threading.Channels`. No Skender or EF Core needed.

**Options:**
- **A (recommended):** `TradingEngine.Infrastructure/Events/TypedEventBus.cs`. Infrastructure is the correct home for framework-touching implementations. Register via `ServiceCollectionExtensions`.
- **B:** `TradingEngine.Application/Events/TypedEventBus.cs`. Application is currently an empty marker. This would give it its first real content.

**Recommendation: A.** Infrastructure is already where channels and concurrency concerns live.

---

### D24 — How should open positions be tracked for the exposure check?

**Problem:** `RiskManager.Validate()` check 5 (max exposure) needs to know the current open positions and their risk. `IRiskManager` currently has no concept of positions.

**Options:**
- **A (recommended):** Add `void RegisterPosition(Position position, Money riskAmount)` and `void DeregisterPosition(Guid positionId)` to `IRiskManager`. The engine loop calls these when trades open and close. `RiskManager` maintains an internal `Dictionary<Guid, decimal>` of open risk amounts.
- **B:** `IRiskManager.Validate()` receives the current open positions as a parameter.
- **C:** `RiskManager` subscribes to `TradeOpened`/`TradeClosed` events via `IEventBus`.

**Recommendation: A.** Synchronous and explicit. C requires EventBus to be running before validation can work.

---

## 4. Iteration 2 Phase Plan

Iteration 2 is split into three sub-phases. Each is one PR. Run the full test suite after each.

---

### Phase 2A — Engine Unblocking

**Branch:** `phase/2a-engine-unblock`
**Goal:** Engine starts and processes ticks. No real trades yet, but the loop runs.

**Changes:**

#### A1. Fix DataFeedService to use StreamTicksAsync
```csharp
// Replace inner loop:
await foreach (var tick in marketData.StreamTicksAsync(symbol, ct))
    await simulatedBroker.TickWriter.WriteAsync(tick, ct);
```
`StreamBarsAsync` call for bar accumulation is a separate write — keep that. Remove the inline tick synthesis.

#### A2. Fix EngineWorker — remove FeedComplete wait, accumulate bars
```csharp
// Remove these lines in ExecuteAsync:
// if (_dataFeed is not null) { await _dataFeed.FeedComplete; ... }

// Add bar store to EngineWorker:
private readonly ConcurrentDictionary<Symbol,
    ConcurrentDictionary<Timeframe, List<Bar>>> _bars = new();

// In ProcessBarsAsync — accumulate, do not discard:
private async Task ProcessBarsAsync(CancellationToken ct)
{
    await foreach (var bar in _broker.BarStream.ReadAllAsync(ct))
    {
        var byTf = _bars.GetOrAdd(bar.Symbol, _ => new());
        var list = byTf.GetOrAdd(bar.Timeframe, _ => new());
        lock (list) { list.Add(bar); }
        // Recompute indicators after new bar
        await RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe);
    }
}
```

#### A3. Fix EngineWorker — build MarketContext with actual bars and indicators
```csharp
// In ProcessTicksAsync:
var bars = BuildBarSnapshot(tick.Symbol);
var indicators = BuildIndicatorSnapshot(tick.Symbol);
var context = new MarketContext(tick.Symbol, tick, bars, indicators, _clock.UtcNow);
```

#### A4. Wire IIndicatorService (SkenderIndicatorService) in Program.cs
```csharp
// Replace the throwing registration:
builder.Services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
// SkenderIndicatorService is internal sealed — expose via InternalsVisibleTo
// or register using a factory in ServiceCollectionExtensions
```

Add to `TradingEngine.Infrastructure.csproj`:
```xml
<ItemGroup>
  <InternalsVisibleTo Include="TradingEngine.Host" />
</ItemGroup>
```
Or change `SkenderIndicatorService` from `internal` to `public sealed` — either is acceptable.

#### A5. Fix HistoricalDataProvider data path
```csharp
// In Program.cs, backtest registration:
var dataDir = builder.Configuration["DataDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "data");
builder.Services.AddSingleton<IMarketDataProvider>(_ =>
    new HistoricalDataProvider(Path.GetFullPath(dataDir)));
```

Add to `appsettings.Backtest.json`:
```json
"DataDirectory": "tests/data"
```

#### A6. Replace StubClock with BrokerClock in Program.cs (live/paper only)
```csharp
if (mode == EngineMode.Backtest)
    builder.Services.AddSingleton<IEngineClock>(new StubClock(DateTime.UtcNow));
else
    builder.Services.AddSingleton<IEngineClock, BrokerClock>();
```

#### A7. Generate test CSV data
```csharp
// Add to tests/TradingEngine.Tests.Simulation/Data/CsvDataGenerator.cs
// a console-executable or xUnit IClassFixture that writes the CSVs to tests/data/
// on first run if they don't exist. Commit the output.
```

**Phase 2A Validation Gate:**
```powershell
dotnet build --no-restore
# 0 errors

dotnet run --project src/TradingEngine.Host --
# Must start, log "Engine starting", log strategy count, log "Warm-up complete"
# Must NOT crash with NotSupportedException
# Ctrl+C to stop; must log "Engine stopped"
```

---

### Phase 2B — Financial Correctness

**Branch:** `phase/2b-financial-correctness`
**Goal:** All 8 risk checks wired, lot sizing uses real pip values, FTMO daily floor correct.

**Changes:**

#### B1. Add `ProtectionCause` enum and fix `RiskManager.OnDailyReset`
Implement as shown in SERIOUS-3 above. Add `ProtectionCause` enum to `TradingEngine.Risk`.

#### B2. Fix `DrawdownTracker.GetDailyLossLimit`
```csharp
public decimal GetDailyLossLimit(decimal maxDailyLossPercent) =>
    InitialAccountBalance * (1 - (decimal)maxDailyLossPercent);
```

#### B3. Fix `RiskManager.CalculateLotSize`
Inject `ISymbolInfoRegistry` into `RiskManager`. Implement as shown in SERIOUS-1.
Requires entry price — derive from `intent.LimitPrice ?? intent.StopLoss` as a conservative approximation, or require the engine to pass the current tick mid price via `EquitySnapshot`.

Simplest approach: add `decimal ApproximateEntryPrice` to the `EquitySnapshot` passed to `CalculateLotSize`. The engine sets this to `currentTick.Ask` for long intents and `currentTick.Bid` for shorts.

#### B4. Add D24 decisions — `RegisterPosition`/`DeregisterPosition` to `IRiskManager`
```csharp
// In TradingEngine.Domain/Interfaces/IRiskManager.cs
void RegisterPosition(Guid positionId, decimal openRiskAmount);
void DeregisterPosition(Guid positionId);
```

#### B5. Implement remaining 5 risk checks in `RiskManager.Validate()`
Implement checks 4–8 as shown in SERIOUS-4. Requires `INewsFilter`, `SessionFilter`, `IEngineClock` injected into `RiskManager`.

#### B6. Fix `TrendBreakoutStrategy` — inject `ISymbolInfoRegistry`
Remove hardcoded `SymbolInfo`. Look up from registry as shown in SERIOUS-5.

#### B7. Apply D21 — add `RequiredIndicators` to `IStrategy`
```csharp
// IStrategy.cs — add:
IReadOnlyList<IndicatorRequest> RequiredIndicators { get; }

// TrendBreakoutStrategy:
public IReadOnlyList<IndicatorRequest> RequiredIndicators => [
    new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod),
    new($"EMA_{_config.Parameters.MaPeriod}", IndicatorType.Ema, _config.Parameters.MaPeriod),
];
```

#### B8. Fix `TrendBreakoutStrategy.Evaluate` to use `context.IndicatorValues`
```csharp
var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");
var ema = context.IndicatorValues.GetValueOrDefault($"EMA_{p.MaPeriod}");
```
Remove `IIndicatorService` field from strategy.

**New unit tests required for Phase 2B:**

| Test | Asserts |
|---|---|
| `DrawdownTracker_DailyLossLimit_UsesInitialBalance_NotDailyStart` | Profit day → daily start rises → floor stays at InitialBalance |
| `RiskManager_OnDailyReset_ClearsDailyProtection_ButNotMaxDD` | Protection from max DD survives daily reset |
| `RiskManager_OnDailyReset_ClearsDailyDDProtection` | Protection from daily DD is cleared |
| `RiskManager_MaxConcurrentPositions_BlocksTrades` | 3 open positions with MaxConcurrentPositions=3 → violation |
| `RiskManager_MaxExposure_BlocksTrades` | Open risk > MaxExposurePercent → violation |
| `RiskManager_CalculateLotSize_UsesRealPipValue` | USDJPY at 149.50 → lot size uses $6.69 pip value not $10 |
| `TrendBreakoutStrategy_UsesSymbolRegistry_NotHardcoded` | GBPJPY signal → SL uses 0.01 pip size |

**Phase 2B Validation Gate:**
```powershell
dotnet test tests/TradingEngine.Tests.Unit --no-build --filter "Category=Risk|Category=Strategy"
# All pass including new tests
```

---

### Phase 2C — Working Engine Loop

**Branch:** `phase/2c-working-loop`
**Goal:** A backtest run produces real trades with real PnL. Simulation tests assert meaningful financials.

**Changes:**

#### C1. Implement TypedEventBus (D23 → A: Infrastructure)
```csharp
// TradingEngine.Infrastructure/Events/TypedEventBus.cs
public sealed class TypedEventBus : IEventBus
{
    private readonly Dictionary<Type, List<object>> _handlers = new();

    public void Subscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : EngineEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            _handlers[typeof(TEvent)] = list = new();
        list.Add(handler);
    }

    public async Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct)
        where TEvent : EngineEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers)) return;
        foreach (var h in handlers)
            await ((IEventHandler<TEvent>)h).HandleAsync(evt, ct);
    }
}
```

Register in `ServiceCollectionExtensions.AddInfrastructure()`:
```csharp
services.AddSingleton<IEventBus, TypedEventBus>();
```

#### C2. Implement PositionManager (D22 → A: Services)
Minimum viable implementation for iteration 2:
- `RegisterPosition(Position, PositionManagementConfig)`
- `DeregisterPosition(Guid)`
- `Evaluate(Position, Tick, IReadOnlyList<Bar>)` → check breakeven trigger, then ATR trail
- Delegate to `ITrailingStopService` methods

#### C3. Implement SimulatedBrokerAdapter fill simulation
```csharp
// Track pending orders and open simulated positions
private readonly Dictionary<Guid, OrderRequest> _pendingOrders = new();
private readonly Dictionary<Guid, SimPosition> _openPositions = new();

public Task SubmitOrderAsync(OrderRequest request, CancellationToken ct)
{
    _pendingOrders[request.OrderId] = request;
    return Task.CompletedTask;
}

// Called by DataFeedService after writing each tick:
public void ProcessTick(Tick tick)
{
    // Fill pending market orders on next tick
    foreach (var (id, order) in _pendingOrders.ToList())
    {
        var fillPrice = order.Direction == TradeDirection.Long
            ? tick.Ask + _slippagePips * _symbolInfo.PipSize
            : tick.Bid - _slippagePips * _symbolInfo.PipSize;

        _openPositions[id] = new SimPosition(order, fillPrice);
        _pendingOrders.Remove(id);

        _executionChannel.Writer.TryWrite(new ExecutionEvent(
            id, OrderState.Filled, new Price(fillPrice),
            order.RequestedLots, null, tick.TimestampUtc));
    }

    // Check SL/TP triggers on open positions
    foreach (var (id, pos) in _openPositions.ToList())
    {
        bool slHit = pos.Direction == TradeDirection.Long
            ? tick.Bid <= pos.StopLoss.Value
            : tick.Ask >= pos.StopLoss.Value;

        bool tpHit = pos.TakeProfit.HasValue && (
            pos.Direction == TradeDirection.Long
                ? tick.Bid >= pos.TakeProfit.Value.Value
                : tick.Ask <= pos.TakeProfit.Value.Value);

        if (slHit || tpHit)
        {
            _openPositions.Remove(id);
            _executionChannel.Writer.TryWrite(new ExecutionEvent(
                id, OrderState.Filled, slHit ? pos.StopLoss : pos.TakeProfit!,
                pos.Lots, null, tick.TimestampUtc));
        }
    }
}
```

`DataFeedService` calls `simulatedBroker.ProcessTick(tick)` after writing each tick.

#### C4. Implement EngineWorker.HandleExecutionEvent and HandleAccountUpdate
```csharp
private void HandleExecutionEvent(ExecutionEvent evt)
{
    if (evt.NewState == OrderState.Filled && evt.FillPrice is not null)
    {
        var order = _pendingOrdersMap[evt.OrderId];
        // For a new fill: open position
        if (!_openPositionsMap.ContainsKey(evt.OrderId))
        {
            var position = new Position(Guid.NewGuid(), evt.OrderId, order.Intent.Symbol,
                order.Intent.Direction, evt.FilledLots, evt.FillPrice,
                order.Intent.StopLoss, order.Intent.TakeProfit,
                _clock.UtcNow, order.Intent.StrategyId);

            _openPositionsMap[evt.OrderId] = position;
            _riskManager.RegisterPosition(position.Id, _latestRiskAmount);

            var posConfig = BuildPositionConfig(order.Intent);
            _positionManager.RegisterPosition(position, posConfig);

            _ = _eventBus.PublishAsync(new TradeOpened(position, _clock.UtcNow), CancellationToken.None);
            _logger.LogInformation("Trade opened. TradeId={Id} Symbol={Symbol} Direction={Dir} Lots={Lots} Entry={Entry}",
                position.Id, position.Symbol, position.Direction, position.Lots, position.EntryPrice.Value);
        }
        else
        {
            // Position closed (SL/TP hit)
            var position = _openPositionsMap[evt.OrderId];
            var symbolInfo = _symbolRegistry.Get(position.Symbol);
            var result = BuildTradeResult(position, evt.FillPrice, symbolInfo);

            _openPositionsMap.Remove(evt.OrderId);
            _riskManager.DeregisterPosition(position.Id);
            _positionManager.DeregisterPosition(position.Id);

            foreach (var strategy in _strategies.Where(s => s.Id == position.StrategyId))
                strategy.OnTradeResult(result);

            _ = _eventBus.PublishAsync(new TradeClosed(result, _clock.UtcNow), CancellationToken.None);
            _ = _dataProvider.Trades.SaveAsync(result, CancellationToken.None);

            _logger.LogInformation("Trade closed. TradeId={Id} Exit={Exit} NetPnL={PnL} R={R}",
                result.Id, result.ExitPrice.Value, result.NetPnL.Amount, result.RMultiple);
        }
    }
}
```

#### C5. Fix EngineTestHarness to compute real PnL
Remove hardcoded `Money(50, "USD")`. Instead:
- Track entry price from `ExecutionEvent.FillPrice` (after SimulatedBrokerAdapter is fixed)
- Track exit price from the closing `ExecutionEvent`
- Compute gross PnL via `PipCalculator.GrossPnL()`
- Compute max drawdown from equity curve (min equity / initial equity)

#### C6. Implement NamedPipeBrokerAdapter.ProcessMessage
```csharp
private void ProcessMessage(string json)
{
    var msg = JsonSerializer.Deserialize<PipeMessage>(json);
    switch (msg?.Type)
    {
        case "Tick":
            var tick = JsonSerializer.Deserialize<Tick>(msg.Payload);
            if (tick is not null) _tickChannel.Writer.TryWrite(tick);
            break;
        case "Bar":
            var bar = JsonSerializer.Deserialize<Bar>(msg.Payload);
            if (bar is not null) _barChannel.Writer.TryWrite(bar);
            break;
        case "AccountUpdate":
            var acct = JsonSerializer.Deserialize<AccountUpdate>(msg.Payload);
            if (acct is not null) _accountChannel.Writer.TryWrite(acct);
            break;
        case "ExecutionEvent":
            var exec = JsonSerializer.Deserialize<ExecutionEvent>(msg.Payload);
            if (exec is not null) _executionChannel.Writer.TryWrite(exec);
            break;
    }
}
```

**New simulation tests required for Phase 2C:**

| Test | Asserts |
|---|---|
| `Backtest_BullishMarket_ProducesRealPositivePnL` | NetPnL > 0, computed from real entry/exit prices |
| `Backtest_WhenSLHit_LossIsCorrectRAmount` | SL hit → NetPnL ≈ -RiskAmount (within spread tolerance) |
| `Backtest_DailyDDBreach_BlocksNewTrades` | Equity drops 5%+ → no new intents accepted after breach |
| `Backtest_MaxDDBreach_EntersProtectionMode` | Equity drops 10%+ → `InProtectionMode = true` in final state |
| `Backtest_EventLog_HasAllTradeEvents` | Every TradeOpened has matching TradeClosed in event log |
| `Backtest_IsReproducible` | Same config + data → identical trade count and total PnL on two runs |
| `Backtest_TrailingStop_MovesSlForward` | Profit trade: SL at entry-1ATR, then moves to entry+0.5ATR |

**Phase 2C Validation Gate:**
```powershell
dotnet build --no-restore
# 0 errors, 0 warnings

dotnet test --no-build
# All unit + integration + simulation tests pass

dotnet run --project src/TradingEngine.Host --
# Produces "Trade opened" and "Trade closed" log lines
# Exits with code 0 when data is exhausted
```

---

## 5. Architectural Validation — New Checks for Iteration 2

Add these to the existing checks in guide §4:

### 5.1 No hardcoded SymbolInfo in Strategies
```powershell
$hardcoded = Select-String -Path "src/TradingEngine.Strategies/**/*.cs" -Pattern "new SymbolInfo\(" -Recurse
if ($hardcoded) { Write-Error "VIOLATION: Hardcoded SymbolInfo in Strategies — use ISymbolInfoRegistry" }
```

### 5.2 No DateTime.UtcNow in Tests
```powershell
$dtNowInTests = Select-String -Path "tests/**/*.cs" -Pattern "DateTime\.UtcNow|DateTime\.Now" -Recurse
if ($dtNowInTests) { Write-Warning "REVIEW: DateTime.UtcNow in test — use StubClock for determinism" }
```

### 5.3 No IIndicatorService injection in Strategies
```powershell
$indicatorInStrategies = Select-String -Path "src/TradingEngine.Strategies/**/*.cs" -Pattern "IIndicatorService" -Recurse
if ($indicatorInStrategies) { Write-Error "VIOLATION: IIndicatorService used in Strategies — strategies must use context.IndicatorValues" }
```

### 5.4 No duplicate tick synthesis
```powershell
$synthesisDupes = Select-String -Path "src/**/*.cs","tests/**/*.cs" -Pattern "OpenTimeUtc \+ quarter" -Recurse
$count = ($synthesisDupes | Measure-Object).Count
if ($count -gt 1) { Write-Error "VIOLATION: Tick synthesis duplicated in $count places — must only be in HistoricalDataProvider.SynthesizeTicks" }
```

---

## 6. Updated Decision Registry

| ID | Decision | Status |
|---|---|---|
| D1–D20 | All from START.md | ✅ Resolved |
| D21 | Strategy indicator contract | ✅ **A** — `RequiredIndicators` property on `IStrategy` |
| D22 | PositionManager location | ✅ **A** — `TradingEngine.Services` |
| D23 | TypedEventBus location | ✅ **A** — `TradingEngine.Infrastructure/Events` |
| D24 | Open position tracking in RiskManager | ✅ **A** — `RegisterPosition`/`DeregisterPosition` on `IRiskManager` |

---

## 7. Document Reading Order at Start of Each Session

Load in this order:

1. `trading-domain-knowledge.md`
2. `trading-engine-design-v1.md`
3. `agent-implementation-guide.md`
4. `START.md`
5. `ITERATION-2.md` ← this file

---

## 8. Definition of Done for Iteration 2

- [ ] `dotnet build` — 0 errors, 0 warnings
- [ ] `dotnet test` — all tests pass including the 14 new tests listed above
- [ ] Architectural checks §5.1–5.4 produce 0 violations
- [ ] `dotnet run --project src/TradingEngine.Host` in Backtest mode produces at least one "Trade opened" and "Trade closed" log line and exits with code 0
- [ ] `BacktestResult.MaxDrawdown` is computed from actual equity curve, not hardcoded
- [ ] `BacktestResult.NetPnL` is derived from real entry/exit prices via `PipCalculator.GrossPnL`
- [ ] No `new SymbolInfo(...)` anywhere in `TradingEngine.Strategies`
- [ ] No `IIndicatorService` injected into any strategy class
- [ ] `DrawdownTracker.GetDailyLossLimit` uses `InitialAccountBalance` (verified by new test)
- [ ] `RiskManager.OnDailyReset` only clears daily-DD-caused protection (verified by new test)
- [ ] Event log contains at least one `TradeOpened` and `TradeClosed` entry after backtest run

---

*This document supersedes the Phase 6–7 placeholders in `agent-implementation-guide.md` for iteration 2 work. All other guide content remains in force.*
