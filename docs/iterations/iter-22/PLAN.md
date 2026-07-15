# Iter-22 — Finish the consolidation: complete the kernel, dissolve the god class, modularize wiring, fast tests

**Audience**: the implementing agent (OpenCode + DeepSeek v4 Pro). Self-contained.
**Branch**: `iter/22-consolidation` off `iter/20-engine-core` (commit after iter-20's `34e7b81`).
**One commit per phase**, prefix `refactor(iter22-cN):` / `feat(iter22-cN):` / `fix(iter22-cN):`.
**Why this exists**: iter-20 built the right *skeleton* (pure `TradingEngine.Engine`, `DecisionRecord`, venue/transport split) but **left the consolidation half-done** — and the original wiring debt (god-class `EngineWorker`, manual `Program.cs`, four overlapping harnesses, slow/cTrader-bound tests) was never in scope. This iteration finishes it.

---

## 0. What iter-20 left undone (verified against the code, 2026-06-14)

1. **`PositionLifecycle` FSM is half-built.** `PositionState` has no trailing fields; the 5 trailing/breakeven methods still live in `PositionManager`; the **`PositionLifecycleState` enum still exists alongside `PositionPhase`** — two state models, the exact duplication P4 was meant to remove.
2. **`EngineReducer` is a routing shell.** It only calls `PositionLifecycle` + `DrawdownReducer`. It has **a real bug**: `HandleBarClosed`/`HandleTickReceived` call `PositionLifecycle.Apply` but **discard `nextPos`** (EngineReducer.cs:162–163, 174–175) — trailing-SL state changes are computed and thrown away. It does *not* handle governor, signal-gate, risk register/deregister, SL/TP exit detection, force-close-on-breach, PnL-on-close, trade-result publishing, or account resets.
3. **`PositionTracker` double-books.** It delegates phase transitions to the FSM *and* keeps its 4 dictionaries (`_pendingOrders`, `_openPositions`, `_pendingRisk`, `_processedExecutionIds`) as "the external contract." State lives in two places.
4. **`EngineWorker` is a 792-line god class** with 25+ injected dependencies and ~20 methods, including a ~177-line `RunBacktestLoopAsync`, all market-data ingestion, indicator management, cross-rate/spread, and inline SL/TP checks. The plan's "thin pump" never happened.
5. **Composition is not consolidated.** Host `Program.cs` has **54 manual `AddSingleton`/`AddScoped` calls**; `EngineHostFactory` adds 223 more lines. No per-module extension methods (only Infrastructure has 2), no assembly scanning. Strategies, event handlers, and size modifiers are hand-listed.
6. **Tests are slow and wiring-coupled.** Four overlapping harnesses (`CtraderTestHarness` 281, `EngineTestHarness` 189, `ReplayTestHarness` 214, `DrawdownTestHarness` 77) each re-derive engine setup that differs from production. The simulation suite takes ~6 min; `NetMQBridgeTest` times out because it needs a live cTrader.

**Prime directive (unchanged): the 7 PositionLifecycle characterization goldens are the behavioral contract. Green at every commit. If a step reddens them, stop and fix — do not push red.**

---

## 1. Hard rules (carry from iter-20)

- `decimal` for money/price. `IEngineClock` in engine code (no `DateTime.UtcNow`). `TradingEngine.Domain` zero infra deps. **`TradingEngine.Engine` pure** (no `ILogger`/EF/adapters/`EngineMode`) — enforced by the architecture tests; keep them green.
- cBot (`TradingEngine.Adapters.CTrader`) net6.0/C#10 — untouched.
- `CancellationToken` on every async method. EF migrations only.
- **Test-first** on every phase: write the failing test, confirm it fails for the stated reason, implement, confirm green.

```
dotnet test tests/TradingEngine.Tests.Architecture     # must stay 3/3
dotnet test tests/TradingEngine.Tests.Unit
dotnet test --filter "FullyQualifiedName~PositionLifecycleGoldenTests"   # the 7 goldens
```

---

## Phase C1 — Complete the PositionLifecycle FSM (finish iter-20 HIGH item 1)

**Goal**: trailing/breakeven logic becomes pure FSM behavior; the second state model dies.

**Do**:
1. Add trailing fields to `PositionState`: `decimal HighWater`, `decimal LowWater`, `bool BreakevenApplied`, `decimal InitialSlDistance` (defaults preserve current ctor call sites).
2. Port the 5 `TrailingHelpers` methods (`StepPips`, `AtrMultiple`, `Structure`, `SteppedR`, `BreakevenThenTrail`) to operate on `PositionState` as pure functions returning an optional new SL.
3. In `PositionLifecycle`, handle `BarClosed`/`TickReceived` for `Open`/`Reducing` phases: update HighWater/LowWater, compute trailing/breakeven, **return the updated `PositionState`** and emit `ModifyStopLoss` effects.
4. **Fix the discarded-`nextPos` bug**: `EngineReducer.HandleBarClosed`/`HandleTickReceived` must write `nextPos` back into `state.Positions` (today they only collect effects).
5. Delete the `PositionLifecycleState` enum; reduce `PositionManager` to nothing (or delete it) once `PositionTracker` no longer needs it (C2).

**Failing test first**: unit tests per trailing method — breakeven fires at the configured R-multiple; step-trail moves SL only on favorable moves; ATR-trail respects HighWater. They fail today (FSM has no trailing). Then the 7 goldens must stay green.

**Commit**: `feat(iter22-c1): port trailing/breakeven into PositionLifecycle; fix reducer state-drop; delete PositionLifecycleState`

---

## Phase C2 — Single source of truth for position state

**Goal**: `EngineState.Positions` is the only position store; `PositionTracker` becomes a translator.

**Do**:
1. Remove `PositionTracker`'s 4 dictionaries. Rewrite it as: translate `ExecutionEvent`/`Tick`/`Bar` → `EngineEvent`, call `EngineReducer.Apply`, hold the resulting `EngineState`, execute returned effects.
2. Anything that reads "open positions" (e.g. `EngineWorker`, risk checks, UI projections) reads `EngineState.Positions`.

**Failing test first**: an integration test asserting position count/SL after a partial-fill-then-close sequence reads consistent values from `EngineState` only. Then goldens green.

**Commit**: `refactor(iter22-c2): EngineState.Positions is the single position store; PositionTracker is a thin translator`

---

## Phase C3 — Complete EngineReducer (absorb the engine's decision capabilities)

**Goal**: every decision the engine makes flows through `EngineReducer.Apply` as event-in/effects-out. This is what lets C4 make `EngineWorker` thin.

**Do** — extend `EngineState` with `GovernorState`, `RiskState`, `ExposureState` sub-records, and move these into the pure kernel as composable reducers/machines:
1. **`GovernorMachine`** in `Engine`: port `TradingGovernorService` to pure `(GovernorState, EngineEvent) → (GovernorState, effects)`. Keep the old service as a shim until green, then delete.
2. **SL/TP exit detection** (currently inline in `EngineWorker.RunBacktestLoopAsync`): on `BarClosed`/`TickReceived`, if price crosses SL/TP, emit `CloseOpenPosition` with reason.
3. **Force-close on breach** + **RiskGate** already exists — call it in the submit path; on breach emit close effects.
4. **Risk register/deregister**, **PnL-on-close**, **trade-result publishing** (`TradeClosed`), **account resets** (daily/weekly/monthly via `DayRolled`/`WeekRolled` events): all as effects (`RegisterRisk`, `PublishTradeClosed`, `ResetAccount`, …).
5. Every transition appends a `RecordDecisionEvent` effect so journaling stays structural.

**Failing test first**: event-script tests (feed `EngineEvent[]`, assert `EngineEffect[]`) for: SL hit → close + PnL + TradeClosed; consecutive losses → governor SoftStop; day boundary → reset. Fail today (reducer lacks these).

**Commit**: `feat(iter22-c3): EngineReducer absorbs governor, SL/TP exit, risk, PnL, trade publishing, resets as effects`

---

## Phase C4 — Dissolve the EngineWorker god class

**Goal**: `EngineWorker` becomes a thin pump (< ~150 lines). All I/O lives in one effect executor.

**Do**:
1. Extract `IEffectExecutor` (Host/Infrastructure): the *only* place effects touch the world — `SubmitOrder`→broker, `RecordDecisionEvent`→`IDecisionJournal`, `PublishTradeClosed`→`IEventBus`, equity→`IEquitySink`. The kernel never does I/O.
2. Extract `MarketEventSource`: translate broker tick/bar/account/execution streams into `EngineEvent`s (the current `ProcessTicksAsync`/`ProcessBarsAsync`/`ProcessAccountUpdatesAsync`/`ProcessExecutionEventsAsync`).
3. Extract `IndicatorSnapshotService` (the indicator dictionaries + `RecomputeIndicatorsAsync`/`BuildIndicatorSnapshot`) and `BacktestDriver` (the ~177-line `RunBacktestLoopAsync` clock/replay loop). Cross-rate/spread helpers move to small services.
4. `EngineWorker` reduces to: `ExecuteAsync` → loop `event = source.Next()` → `decision = EngineReducer.Apply(state, event)` → `state = decision.State` → `executor.Execute(decision.Effects)`.

**Failing test first**: an architecture/size test asserting `EngineWorker` has ≤ ~6 injected dependencies and no `RunBacktestLoopAsync`/indicator logic. Then full simulation suite green.

**Commit**: `refactor(iter22-c4): EngineWorker → thin pump; extract EffectExecutor, MarketEventSource, IndicatorSnapshotService, BacktestDriver`

---

## Phase C5 — Modular composition: extension methods + auto-discovery + one root

**Goal**: kill the 54-line manual `Program.cs` and the duplicate roots.

**Do**:
1. One `AddXxx(this IServiceCollection)` per module, each in its owning project: `AddEngineKernel()`, `AddRisk()`, `AddStrategies()`, `AddVenues(EngineMode)`, `AddPersistence(conn)`, `AddMarketData(EngineMode)`, `AddEventPipeline()`.
2. **Assembly scanning** (Scrutor, or a small reflection helper) for the open-ended sets: `IStrategy`, `IEventHandler<>`, `ISizeModifier`. New strategies/handlers/modifiers register by existing, not by editing `Program.cs`.
3. Collapse Host `Program.cs` + `EngineHostFactory` into **one** composition root that selects `EngineMode` and calls the `AddXxx` methods. Target `Program.cs` ≤ ~40 lines. `Web/Program.cs` calls the same extensions (no parallel wiring).

**Failing test first**: a DI smoke test that builds the root `ServiceProvider` and resolves `EngineWorker` + all hosted services with **zero manual registrations in `Program.cs`** beyond the `AddXxx` calls. Add a count assertion (`Program.cs` registration lines ≤ N).

**Commit**: `refactor(iter22-c5): per-module AddXxx extensions + assembly scanning; single ≤40-line composition root`

---

## Phase C6 — One test harness, built on the production wiring

**Goal**: tests wire the engine the *same way production does*, via the C5 extensions — no bespoke setup drift.

**Do**:
1. Build `EngineHarnessBuilder` (fluent): `.WithMode().WithSymbol().WithStrategy().WithBars(...).WithBalance().Build()` — internally calls the C5 `AddXxx` extensions against an in-memory config, swaps the venue for `SimulatedBrokerAdapter`/`FakeMessageTransport`.
2. Migrate `CtraderTestHarness`, `EngineTestHarness`, `ReplayTestHarness`, `DrawdownTestHarness` onto it; delete the four once their tests pass through the builder.

**Failing test first**: re-point one existing simulation test at `EngineHarnessBuilder` and confirm identical results. Then migrate the rest.

**Commit**: `refactor(iter22-c6): unified EngineHarnessBuilder over production AddXxx; retire 4 overlapping harnesses`

---

## Phase C7 — Fast tests + fast cTrader verification

**Goal**: default `dotnet test` is fast and needs no live cTrader; cTrader is still verifiable.

**Do**:
1. **Traits**: tag tests `[Trait("Speed","Fast")]` (pure kernel: reducer/FSM/governor/drawdown/risk — no I/O), `"Sim"` (full pipeline via harness), `"RequiresCTrader"`. Default CI runs `--filter Speed=Fast`; Sim runs on demand/nightly.
2. **Push coverage down**: now that C1–C4 made the decision logic pure, most behavior is testable as fast event-script/unit tests. Trim the simulation suite to a small end-to-end smoke set (a few representative runs), not 36 heavyweight ones.
3. **cTrader verification without a live instance**: replace the timing-out `NetMQBridgeTest` with (a) a **transport contract test** using `FakeMessageTransport` that asserts the cTrader wire protocol (order frame out, execution frame in) — fast, no process; and (b) **one** opt-in real-cTrader smoke test tagged `[Trait("RequiresCTrader")]`, gated behind an env var, run manually only.

**Acceptance**: `dotnet test --filter Speed=Fast` completes in well under a minute; no green-by-default test needs cTrader; the transport contract test proves venue/wire correctness; the full Sim smoke set still passes.

**Commit**: `feat(iter22-c7): test speed traits, kernel-level coverage, FakeTransport cTrader contract test, opt-in live smoke`

---

## 2. Definition of Done

- [ ] One position state model (`PositionPhase`/`PositionState`); `PositionLifecycleState` and `PositionManager`'s trailing logic gone; trailing is pure FSM behavior; reducer persists `nextPos` (bug fixed).
- [ ] `EngineState` is the single store; `PositionTracker` is a translator with no dictionaries.
- [ ] `EngineReducer.Apply` handles governor, SL/TP exit, risk, PnL, trade publishing, resets — event-in/effects-out; journaling structural.
- [ ] `EngineWorker` ≤ ~150 lines / ≤ ~6 deps; all I/O in `IEffectExecutor`; backtest loop + indicators + market ingestion extracted.
- [ ] Per-module `AddXxx` extensions + assembly scanning; one composition root; `Program.cs` ≤ ~40 lines; Web reuses the same wiring.
- [ ] Single `EngineHarnessBuilder` over the production extensions; 4 old harnesses deleted.
- [ ] `dotnet test --filter Speed=Fast` < ~1 min; no default test needs cTrader; FakeTransport contract test + opt-in live smoke.
- [ ] Architecture tests stay 3/3; 7 characterization goldens green at every commit.

## 3. Sequencing notes

- **Strict order C1 → C2 → C3 → C4** — each depends on the prior (you can't make `EngineWorker` thin until the reducer is complete; you can't complete the reducer cleanly until the FSM and single state store exist).
- **C5 can start in parallel** with C1–C3 (composition is largely independent), but C6 needs C5 (the harness reuses the extensions), and C7 needs C6.
- Run the 7 goldens after every change in C1–C4; run the full Sim smoke before declaring any phase done.
- Convert relative dates to absolute. Today is 2026-06-14.
```
