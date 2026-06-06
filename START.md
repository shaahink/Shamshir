# Shamshir — Decision Votes, Filled Information & Start Confirmation

> Owner review of DECISIONS.md — all open items addressed below.
> After reading this file, the agent has everything needed to begin Phase 0 (repo setup).
> Do not ask for further clarification on any item marked **VOTED** or **INFO PROVIDED**.

---

## Votes on All Open Decisions

---

### D1 — Skender Placement
**VOTED: A — Infrastructure.**

`SkenderIndicatorService`, `SkenderQuote`, and `IndicatorCache` live in
`TradingEngine.Infrastructure/Indicators/`. The architectural check (guide §4.2) stays unchanged.

**Guide amendment required:**
Phase 4 (Services) loses these three files. Move them to Phase 3 (Infrastructure).
Phase 4 now contains only: `PipCalculator`, `SlTpCalculator`, `TrailingStopService`, `ExcursionTracker`, `SlParameters`, `TpParameters`.
Update the Phase 3 file checklist in the guide to add:

```
src/TradingEngine.Infrastructure/
  Indicators/
    SkenderIndicatorService.cs   # internal sealed, implements IIndicatorService
    SkenderQuote.cs              # internal sealed
    IndicatorCache.cs            # internal sealed
```

`IIndicatorService` stays in `TradingEngine.Domain/Interfaces/` — unchanged.
The Skender NuGet package reference goes in `TradingEngine.Infrastructure.csproj` only.

---

### D2 — Backtest Data Path
**VOTED: A — DataFeedService.**

A dedicated `DataFeedService : BackgroundService` in `TradingEngine.Host` reads from
`IMarketDataProvider` and writes to `SimulatedBrokerAdapter.TickWriter` / `.BarWriter`.
This preserves the clean `IBrokerAdapter` contract — the engine loop never knows it is reading
from a file.

The service must complete feeding all data before signalling done. Use a `TaskCompletionSource`
that `DataFeedService` completes when the data stream ends. `EngineWorker` awaits this before
flushing and exiting in backtest mode.

---

### D3 — Concurrency Model
**VOTED: A — Single-threaded tick processor with execution event drain.**

Architecture:
- One `Channel<ExecutionEvent>` sits between `ProcessExecutionEventsAsync` and the tick processor.
  Use `BoundedChannelFullMode.Wait` — execution events must never be dropped.
- `ProcessExecutionEventsAsync` reads the broker's `ExecutionStream` and re-queues to this internal channel.
- At the **top** of each tick cycle in `ProcessTicksAsync`, drain all pending execution events before
  evaluating strategies or positions.

```
ProcessTicksAsync (primary loop):
  1. Drain pending ExecutionEvent queue (all that arrived since last tick)
  2. Update equity snapshot from last AccountUpdate
  3. Check risk state
  4. Evaluate strategies → collect intents
  5. Validate + size + submit each intent
  6. Evaluate position manager for each open position

ProcessExecutionEventsAsync (secondary, concurrent):
  → reads broker.ExecutionStream
  → writes to internal Channel<ExecutionEvent>
  → nothing else

ProcessAccountUpdatesAsync (secondary, concurrent):
  → reads broker.AccountStream
  → writes latest AccountUpdate to a single Interlocked-swapped field (no channel needed)
  → the tick processor reads this field in step 2
```

No locks required. No shared mutable state between concurrent loops.

---

### D5 — Config Loading Strategy
**VOTED: A — ConfigLoader service.**

`ConfigLoader` is a singleton registered in DI. It:
1. Resolves paths relative to `AppContext.BaseDirectory` — not `Directory.GetCurrentDirectory()`
2. Loads all JSON files from `config/prop-firms/`, `config/risk-profiles/`, `config/strategies/`
3. Deserialises each with `JsonUnmappedMemberHandling.Disallow` — unknown keys throw on startup
4. Validates cross-references: every `riskProfileId` in strategy config must exist in loaded profiles;
   every `propFirmRuleSetId` in risk profiles must exist in loaded prop firm configs
5. Returns a `LoadedConfig` record holding all three typed collections

`ConfigLoader` is called from `Program.cs` before any service is started. If it throws, the
host exits immediately with a clear error message — fail fast.

---

### D6 — Strategy ID → Type Resolution
**VOTED: A — `[StrategyId]` attribute + `StrategyRegistry`.**

```csharp
// In TradingEngine.Domain (or a shared Attributes file)
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StrategyIdAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

// Applied to each strategy:
[StrategyId("trend-breakout")]
public sealed class TrendBreakoutStrategy : IStrategy { ... }
```

`StrategyRegistry` (in `TradingEngine.Host`) scans `TradingEngine.Strategies` assembly at startup,
finds all `IStrategy` implementors with `[StrategyId]`, builds `Dictionary<string, Type>`.
DI registers only strategies whose IDs appear in `EngineOptions.ActiveStrategyIds`.

If an `ActiveStrategyId` from config has no matching `[StrategyId]` class, throw on startup.

---

### D8 — LiveMarketDataProvider Behavior
**VOTED: A — Throw `NotSupportedException` with a clear message.**

```csharp
public sealed class LiveMarketDataProvider : IMarketDataProvider
{
    public IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct)
        => throw new NotSupportedException(
            "LiveMarketDataProvider is not implemented until Phase 9. " +
            "Use EngineMode.Backtest or EngineMode.Paper with SimulatedBrokerAdapter.");

    // Same for StreamBarsAsync and SeekAsync
}
```

This prevents silent failures where the engine appears to run but processes no data.

---

### D9 — NewsFilter Implementation
**VOTED: A — Stub returning false (no news window active).**

```csharp
public sealed class NewsFilter : INewsFilter
{
    // Always returns false until a real calendar feed is integrated.
    public bool IsNewsWindowActive(Symbol symbol, DateTime utcNow) => false;
}
```

`INewsFilter` is an interface in `TradingEngine.Risk` so the real implementation can be swapped
in without changing the risk manager. The stub is the Phase 2 implementation.
Real news feed integration is a separate future phase — do not attempt it now.

---

### D10 — Tick Synthesis from Bars
**VOTED: A — 4 ticks at 0%, 25%, 50%, 75% of bar duration.**

Tick ordering:
- **Bullish bar** (Close ≥ Open): Open → High → Low → Close
- **Bearish bar** (Close < Open): Open → Low → High → Close

This ordering produces realistic SL simulation. On a bullish bar the Low comes 3rd — a long
position entered at Open would see the worst adverse excursion on tick 3, which reflects
realistic intra-bar price action.

Timestamps:
```
tick0: OpenTimeUtc + 0%  of bar duration → price = Open
tick1: OpenTimeUtc + 25% of bar duration → price = High (bullish) or Low (bearish)
tick2: OpenTimeUtc + 50% of bar duration → price = Low  (bullish) or High (bearish)
tick3: OpenTimeUtc + 75% of bar duration → price = Close
```

Bar duration is derived from `Timeframe` (M1 = 60s, H1 = 3600s, D1 = 86400s, etc.).
`Bid = price`, `Ask = price + symbol.TypicalSpread` on every synthesised tick.

---

### D11 — Slippage Determinism
**VOTED: A — Fixed offset, no randomness.**

`SlippagePips` from `SimulationOptions` is added to long fills (at Ask) and subtracted from
short fills (at Bid). Same value on every fill. Zero randomness.

If the user later wants stochastic slippage, add `SimulationOptions.SlippageSeed` and use a
`Random(seed)` instance — but default remains fixed and deterministic.

---

### D15 — MaxExposurePercent Metric
**VOTED: A — Sum of open risk / equity.**

Open risk per position:
```
PositionRisk = SlDistancePips × PipValuePerLot(symbol, currentPrice, getCrossRate) × Lots
```

Total exposure check:
```
TotalOpenRisk = Σ PositionRisk across all open positions
ExposureFraction = TotalOpenRisk / CurrentEquity
```

Block new intent if `ExposureFraction + NewPositionRisk / CurrentEquity > MaxExposurePercent`.

`PipValuePerLot` must be called with the current market price at the time of the check —
not cached from entry.

---

### D16 — Daily Reset Timer at Startup
**VOTED: A — Fire immediately if engine starts after today's reset time.**

`DailyResetService` startup logic:
```
resetTimeToday = today's date + resetTimeUtc (22:00)
if clock.UtcNow > resetTimeToday:
    riskManager.OnDailyReset(currentEquity)   ← fire now
    nextReset = tomorrow's date + resetTimeUtc
else:
    nextReset = resetTimeToday

Schedule recurring timer at nextReset, then every 24h
```

This guarantees that if the engine restarts at 23:30 after a breach-and-shutdown at 22:15,
the daily DD counter is correctly zeroed before the engine resumes trading.

---

## Information Provided

---

### D17 — cTrader API Details

**INFO PROVIDED — no further input needed from you.**

**cTrader runtime facts:**
- cBots are .NET Framework assemblies loaded by the cTrader desktop application
- Base class: `cAlgo.API.Robot` — override `OnStart()`, `OnStop()`, `OnTick()`, `OnBar(BarType)`
- `OnTick()` fires on every bid/ask update for all subscribed symbols
- `OnBar(BarType barType)` fires when a new bar closes; `barType.TimeFrame` identifies which TF
- `cAlgo.API` namespace is **not a NuGet package** — it is injected by the cTrader runtime
  from dlls in the cTrader installation directory. The cBot csproj must reference these as
  `<Reference>` pointing to a local path, or rely on cTrader's own build system
- Everything runs on a single thread in cTrader — no async/await, no Task, no Task.Run
- `Positions` collection: live positions accessible from `OnTick()`
- Order execution: `PlaceMarketOrder()`, `PlaceLimitOrder()`, `ModifyPosition()`, `ClosePosition()` —
  all synchronous, immediate, blocking

**Named pipe roles:**
- **Engine = Pipe Server** (`System.IO.Pipes.NamedPipeServerStream`)
- **cBot = Pipe Client** (`System.IO.Pipes.NamedPipeClientStream`)
- The engine must be running before the cBot connects
- Pipe name: from `BrokerOptions.PipeName` config (default: `"trading-engine"`)

**cBot message flow:**
```
OnTick()  → serialise Tick → write to pipe → engine reads, pushes to TickChannel
OnBar()   → serialise Bar  → write to pipe → engine reads, pushes to BarChannel
           → also send AccountUpdate on each tick (balance, equity, floating PnL)

Engine    → write OrderCommand to pipe → cBot reads in background thread
cBot      → executes via cAlgo.API (PlaceMarketOrder etc.)
           → on fill/rejection: send ExecutionEvent back over pipe
```

**Background read thread in cBot:**
cTrader is single-threaded for callbacks, but you CAN create a `System.Threading.Thread`
for reading the pipe (order commands from engine). Start it in `OnStart()`, stop in `OnStop()`.
Keep it simple — just read and execute. No async.

**cTrader build system:** cBots are typically compiled inside cTrader's "Build" button which
uses its own MSBuild invocation. Alternatively, use `dotnet build` targeting `net48` — the
cAlgo.API references must be present locally (copy from cTrader installation folder).

**What the agent needs to stub for Phase 9:**
- Reference `cAlgo.API.dll` as a local file reference (path configurable in csproj)
- If the DLL is not present on CI, exclude the cBot project from the CI build and note it
  in the workflow yaml. The cBot is only ever built on a machine with cTrader installed.
- Add to `.gitignore`: `*.algo` (compiled cBot artifacts)

---

### D18 — Test Data Source

**INFO PROVIDED — generate synthetic data.**

Use `CsvDataGenerator` to produce deterministic synthetic OHLCV CSV in the correct format.
Commit generated files to `tests/data/`. Do not depend on any external data source.

Generate these files at minimum:

| Filename | Bars | Scenario | Purpose |
|---|---|---|---|
| `eurusd-h1-bull-2024.csv` | 2000 | Steady uptrend | Normal backtest, positive PnL |
| `eurusd-h1-bear-2024.csv` | 2000 | Steady downtrend | Short strategy validation |
| `eurusd-h1-ranging-2024.csv` | 2000 | Price oscillates in a range | Low signal count test |
| `eurusd-h1-ddcrash-2024.csv` | 500 | Sharp 6% drop in 1 day | Daily DD breach test |
| `eurusd-h1-maxdd-2024.csv` | 500 | 11% drawdown over 2 weeks | Max DD breach test |
| `usdjpy-h1-bull-2024.csv` | 2000 | Uptrend | JPY pair pip value calculation test |

`CsvDataGenerator` parameters per scenario:

```csharp
// Trend: each bar's close = previous close + (drift ± noise)
// drift: positive for bull, negative for bear
// noise: random but seeded (use Random(42) for all generators — determinism)
// Range bars: drift = 0, noise amplitude = typical ATR

record GeneratorConfig(
    Symbol Symbol,
    decimal StartPrice,
    decimal DriftPerBar,    // e.g. 0.00005 for slight uptrend
    decimal NoiseAmplitude, // e.g. 0.0003 for ATR ~30 pips
    int BarCount,
    Timeframe Timeframe,
    DateTime StartTime,
    int Seed = 42);
```

For the crash scenarios, override a specific bar range to produce a large negative candle.

---

### D19 — Number of Phases
**VOTED: 10 phases stands.**

With D1 resolved (Skender moves to Phase 3), the phase split is clean:
- Phase 3 = Infrastructure + Skender wrapper (larger PR but cohesive)
- Phase 4 = Services: PipCalculator, SL/TP, Trailing Stop (focused, no Skender)

Do not merge or split any phases.

---

## Guide Amendments Required

The following changes to `agent-implementation-guide.md` must be applied by the agent
at the start of Phase 0 before any code is written:

| # | Location | Change |
|---|---|---|
| 1 | Phase 4 file list | Remove `SkenderIndicatorService.cs`, `SkenderQuote.cs`, `IndicatorCache.cs` from Services |
| 2 | Phase 3 file list | Add `src/TradingEngine.Infrastructure/Indicators/SkenderIndicatorService.cs`, `SkenderQuote.cs`, `IndicatorCache.cs` |
| 3 | Phase 4 validation tests | Remove Skender-related tests from Phase 4; they belong in Phase 3 integration tests |
| 4 | Phase 7 file list | Add `DataFeedService.cs`, `DailyResetService.cs`, `StrategyRegistry.cs`, `ConfigLoader.cs` (already in DECISIONS.md Phase 7 scope — confirm inclusion) |
| 5 | §2.7 (Engine loop) | Add internal `Channel<ExecutionEvent>` for D3 concurrency model; document drain-first pattern |
| 6 | §2.9 (EngineTestHarness) | Add that harness must wire `DataFeedService` internally to feed `SimulatedBrokerAdapter` |
| 7 | §4.2 (Arch check) | No change needed — already scans Infrastructure, which is now correct |

---

## One Additional Architectural Decision Not in DECISIONS.md

### D20 — `SymbolInfo` Registry

**Problem:** `PipCalculator.PipValuePerLot()` and every SL/TP/trailing calculation requires
`SymbolInfo` for the traded symbol. Where does this come from at runtime?

**Decision:**

Create `ISymbolInfoRegistry` in `TradingEngine.Domain/Interfaces/`:

```csharp
public interface ISymbolInfoRegistry
{
    SymbolInfo Get(Symbol symbol);
    void Register(SymbolInfo info);
    bool TryGet(Symbol symbol, out SymbolInfo info);
}
```

Implementation (`SymbolInfoRegistry`) in `TradingEngine.Infrastructure`, backed by an
in-memory `Dictionary<Symbol, SymbolInfo>` populated at startup:

**In backtest mode:** load defaults from domain doc §14 (the reference table).
`DefaultSymbolInfoProvider` reads these from a committed JSON file:
`config/symbols/defaults.json`. The agent must create this file in Phase 0.

**In live mode:** populated from the broker on connect. `NamedPipeBrokerAdapter` sends
a `"SymbolInfoResponse"` message when the cBot connects, providing broker-supplied
`PipSize`, `ContractSize`, `MinLots`, `MaxLots`, `LotStep`, `MarginRate` values.
These override the defaults.

**Config file:** `config/symbols/defaults.json` — create in Phase 0 using the table from
domain doc §14. Schema:

```json
[
  {
    "symbol": "EURUSD",
    "category": "Forex",
    "baseCurrency": "EUR",
    "quoteCurrency": "USD",
    "pipSize": 0.0001,
    "tickSize": 0.00001,
    "contractSize": 100000,
    "minLots": 0.01,
    "maxLots": 100.0,
    "lotStep": 0.01,
    "marginRate": 0.03333,
    "typicalSpread": 0.0001
  }
]
```

`ISymbolInfoRegistry` is injected into `PositionSizer`, `RiskManager`, `PipCalculator`
wrappers, `TrailingStopService`, and `SimulatedBrokerAdapter`.

**Scope:** Add `ISymbolInfoRegistry` to Domain in Phase 1. Add `SymbolInfoRegistry` and
`config/symbols/defaults.json` to Infrastructure in Phase 3.

---

## Full Decision Registry — Final State

| ID | Decision | Vote / Status |
|---|---|---|
| D1 | Skender placement | ✅ A — Infrastructure |
| D2 | Backtest data path | ✅ A — DataFeedService (IHostedService in Host) |
| D3 | Concurrency model | ✅ A — Single-threaded tick processor, drain-first |
| D4 | PipCalculator location | ✅ Services (pre-resolved by agent) |
| D5 | Config loading | ✅ A — ConfigLoader service, AppContext.BaseDirectory |
| D6 | Strategy resolution | ✅ A — [StrategyId] attribute + StrategyRegistry |
| D7 | Application project | ✅ Assembly marker only (pre-resolved by agent) |
| D8 | LiveMarketDataProvider | ✅ A — throw NotSupportedException |
| D9 | NewsFilter | ✅ A — stub, always false |
| D10 | Tick synthesis | ✅ A — 4 ticks at 0/25/50/75%, OHLC ordering by direction |
| D11 | Slippage determinism | ✅ A — fixed offset |
| D12 | FTMO daily reset time | ✅ 22:00 UTC (pre-resolved by agent) |
| D13 | SlMethod enum | ✅ 3 values (pre-resolved by agent) |
| D14 | DurationSeconds | ✅ Add to TradeResult (pre-resolved by agent) |
| D15 | MaxExposurePercent | ✅ A — sum of open risk / equity |
| D16 | Daily reset on late start | ✅ A — fire immediately if past reset time |
| D17 | cTrader API | ✅ Info provided above |
| D18 | Test data source | ✅ Synthetic via CsvDataGenerator, committed to tests/data/ |
| D19 | Number of phases | ✅ 10 phases, unchanged |
| D20 | SymbolInfo registry | ✅ ISymbolInfoRegistry in Domain, loaded from defaults.json |

---

## Confirmation to Start

**All decisions resolved. The agent is cleared to begin implementation.**

### Start with: Pre-Phase — Repository Setup (`chore/init-repo`)

Before writing any C# code, the agent must:

1. Apply the 7 guide amendments listed above to `agent-implementation-guide.md`
2. Create `config/symbols/defaults.json` using the table from domain doc §14
3. Add `ISymbolInfoRegistry` to the Phase 1 domain file checklist
4. Run the solution scaffolding commands from guide §2.3
5. Create `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`
6. Create all `config/` skeleton directories with one example `.json` each
7. Create `tests/data/` directory (empty, with `.gitkeep`)
8. Verify `dotnet build` across all empty projects passes before Phase 1

### Document reading order at the start of each session

The agent must load these three documents at the start of every session, in this order:

1. `trading-domain-knowledge.md` — financial rules and calculation reference
2. `trading-engine-design-v1.md` — architecture and code standards
3. `agent-implementation-guide.md` — task breakdown, gaps filled, validation gates

Then load `DECISIONS.md` and `START.md` to confirm current state.

### What "done" means for each PR

A phase PR is mergeable when:
- The phase's validation gate command(s) from the guide pass with 0 errors
- All required unit/integration/simulation tests for that phase are written and pass
- All architectural validation scripts (guide §4) pass with 0 violations
- Every new public type has an XML doc comment (`///`)
- `dotnet format --verify-no-changes` passes

---

*All open questions answered. No further clarification required. Begin with `chore/init-repo`.*
