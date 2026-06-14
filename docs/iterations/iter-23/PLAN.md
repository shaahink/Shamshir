# Iter-23 — Close the gap: finish the extraction, fix indicator correctness, harden the tick path, consolidate risk

**Audience**: the implementing agent (OpenCode + DeepSeek v4 Pro). Self-contained.
**Branch**: `iter/23-close-gap` off the iter-22 tip.
**One commit per phase**, prefix `refactor(iter23-N):` / `feat(iter23-N):` / `fix(iter23-N):`.

**Why this exists**: iter-22 was attempted but stalled mid-way. Verified state on 2026-06-15: `EngineWorker` is still 635 lines / 25 deps (target ≤150/≤6); `Program.cs` still 314 lines of manual DI; 4 test harnesses remain; 6 services still in Host; `EngineReducer` (238 lines) still doesn't emit PnL/TradeClosed/risk/force-close/decision effects. On top of the unfinished extraction, three correctness/quality issues were found that were never in scope: an **indicator cache-key bug**, an **overloaded tick hotpath**, and **un-consolidated risk** (two drawdown implementations, force-close polled in two places).

### Why iter-22 stalled, and what is different here
iter-22's phases bundled ~6 capabilities each (e.g. "EngineReducer absorbs governor + SL/TP + risk + PnL + publishing + resets") and were strictly chained, so one stall blocked everything after it. **iter-23 uses small, independent phases, each with a machine-checkable acceptance gate** (a line count, a `grep` that must return nothing, or a named test). Groups A–G are largely independent; within a group, order matters. **Do not mark a phase done until its gate command passes — paste the command output into the commit message.**

**Prime directive (unchanged): the 7 PositionLifecycle characterization goldens are the behavioral contract. Green at every commit. Never push red.**

---

## Hard rules (carry forward)
- `decimal` for money/price. `IEngineClock` in engine code. `TradingEngine.Domain` zero infra deps. `TradingEngine.Engine` pure (no `ILogger`/EF/`EngineMode`) — architecture tests stay 3/3.
- cBot net6.0/C#10 untouched. `CancellationToken` on every async. EF migrations only.
- **Test-first**: write the failing test, confirm it fails for the stated reason, implement, confirm green.

```
dotnet test tests/TradingEngine.Tests.Architecture          # 3/3
dotnet test tests/TradingEngine.Tests.Unit
dotnet test --filter "FullyQualifiedName~PositionLifecycleGoldenTests"   # the 7 goldens
```

---

# GROUP A — Finish the EngineWorker extraction (one collaborator per commit)

> Each phase moves logic *out* of `EngineWorker` behind its existing behavior. Gate = the moved methods no longer appear in `EngineWorker.cs` AND simulation stays green.

## A1 — Move the 6 remaining services out of Host
**Do**: move `DataFeedService`, `DailyResetService`, `StrategyRegistry`, `StrategyBankService`, `CrossRateStore` → `TradingEngine.Application` (namespaces updated; DI updated). Pure moves, no logic change.
**Gate**: `ls src/TradingEngine.Host/*.cs` lists only → `Program.cs EngineHostFactory.cs EngineWorker.cs EngineWorkerDependencies.cs EffectExecutor.cs BacktestDriver.cs EngineServiceCollectionExtensions.cs ExperimentHostFactoryAdapter.cs GlobalUsings.cs`. (Further trimming in A4/C1.) All suites green.
**Commit**: `refactor(iter23-a1): move DataFeed/DailyReset/Strategy*/CrossRate services to Application`

## A2 — Extract market-data ingestion → `MarketEventSource`
**Do**: move `ProcessTicksAsync`, `ProcessBarsAsync`, `ProcessAccountUpdatesAsync`, `ProcessExecutionEventsAsync`, `DrainExecutionStreamAsync`, `HandleAccountUpdate` into a `MarketEventSource` that translates broker streams → `EngineEvent`s and exposes them as one event stream. (Tick-path hardening happens in F1 — here, just move.)
**Gate**: `grep -cE "ProcessTicksAsync|ProcessBarsAsync|ProcessAccountUpdatesAsync|ProcessExecutionEventsAsync" src/TradingEngine.Host/EngineWorker.cs` returns `0`. Simulation green.
**Commit**: `refactor(iter23-a2): extract MarketEventSource from EngineWorker`

## A3 — Extract indicator management → `IndicatorSnapshotService`
**Do**: move `BuildIndicatorSnapshot`, `RecomputeIndicatorsAsync`, `WarmUpIndicatorsAsync`, `BuildBarSnapshot`, `_bars`, `_indicatorValues`, `_reusableIndicatorDict` into an `IndicatorSnapshotService`. (This sets up E1 — do not fix the key bug yet, just move verbatim so behavior is unchanged and goldens stay green.)
**Gate**: `grep -cE "RecomputeIndicatorsAsync|BuildIndicatorSnapshot|_indicatorValues" src/TradingEngine.Host/EngineWorker.cs` returns `0`. Goldens green.
**Commit**: `refactor(iter23-a3): extract IndicatorSnapshotService from EngineWorker`

## A4 — Reduce EngineWorker to a thin pump
**Do**: what remains becomes the loop *receive event → `EngineReducer.Apply` → `EffectExecutor.Execute(effects)`*. Inject only: event source, reducer entry, effect executor, clock, run-context, logger.
**Gate**: add `tests/TradingEngine.Tests.Architecture/EngineWorkerSizeTest` asserting `EngineWorker.cs` ≤ 150 lines and the type has ≤ 6 constructor parameters. Full simulation green.
**Commit**: `refactor(iter23-a4): EngineWorker is a thin pump (≤150 lines, ≤6 deps)`

---

# GROUP B — Complete EngineReducer (one effect per commit, each an event-script test)

> Each phase: add one capability as an effect, prove with an event-script test (feed `EngineEvent[]`, assert `EngineEffect[]`), then delete the corresponding manual code from `PositionTracker`/`EngineWorker`/`BacktestDriver`.

## B1 — PnL-on-close + `PublishTradeClosed` effects
On the close transition, reducer computes gross/net PnL and emits `PublishTradeClosed(TradeResult)`. Remove the manual PnL+publish from `PositionTracker`.
**Gate**: event-script test `Close_emits_TradeClosed_with_correct_pnl`. `grep PublishAsync.*TradeClosed src/TradingEngine.Services/PositionTracker.cs` returns `0`.
**Commit**: `feat(iter23-b1): PnL + TradeClosed as reducer effects`

## B2 — Risk register/deregister effects
Reducer emits `RegisterRisk`/`DeregisterRisk` on open/close; executor calls the risk store. Remove manual `RegisterPosition`/`DeregisterPosition` calls from `PositionTracker`.
**Gate**: test `Open_then_close_emits_register_then_deregister`. Goldens green.
**Commit**: `feat(iter23-b2): risk register/deregister as reducer effects`

## B3 — Force-close as an effect (kill the polled flag)
Replace `RiskManager.ConsumeForceClosePending()` polling with: breach detection inside the reducer emits `CloseOpenPosition(reason: Breach)` effects.
**Gate**: `grep -rn ConsumeForceClosePending src/TradingEngine.Host src/TradingEngine.Risk` returns **nothing** (method and both call sites — `EngineWorker:183`, `BacktestDriver:95` — gone). Test `Breach_emits_close_effects_for_all_open_positions`.
**Commit**: `feat(iter23-b3): force-close on breach as reducer effect; remove polled ConsumeForceClosePending`

## B4 — `RecordDecisionEvent` on every transition
Every reducer branch (accept, reject, open, partial, close, breach, illegal) appends a `RecordDecisionEvent`. 
**Gate**: parametrized test asserting each `EngineEvent` subtype yields ≥1 `RecordDecisionEvent`. The unified journal shows accepts AND rejects on one path.
**Commit**: `feat(iter23-b4): structural RecordDecisionEvent on every reducer transition`

---

# GROUP C — Composition (independent; can run any time)

## C1 — Per-module `AddXxx` + assembly scanning + slim Program.cs
**Do**: create `AddRisk()`, `AddStrategies()`, `AddVenues(EngineMode)`, `AddPersistence(conn)`, `AddMarketData(EngineMode)`, `AddEventPipeline()` extensions, each in its owning project. Use **Scrutor** assembly scanning for `IStrategy`, `IEventHandler<>`, `ISizeModifier` (new ones register by existing). Collapse Host `Program.cs` + `EngineHostFactory` into one root that selects mode and calls the extensions; `Web/Program.cs` reuses them.
**Gate**: DI smoke test builds the provider and resolves `EngineWorker` + hosted services. `Program.cs` ≤ 40 lines AND `grep -cE "AddSingleton|AddScoped|AddTransient" src/TradingEngine.Host/Program.cs` ≤ 8.
**Commit**: `refactor(iter23-c1): per-module AddXxx + Scrutor scanning; ≤40-line Program.cs`

---

# GROUP D — One test harness (needs C1)

## D1 — `EngineHarnessBuilder` over production wiring
**Do**: fluent `EngineHarnessBuilder` that calls the C1 `AddXxx` extensions against in-memory config, swapping the venue for `SimulatedBrokerAdapter`/`FakeMessageTransport`. Migrate all tests off `CtraderTestHarness`, `ReplayTestHarness`, `EngineTestHarness`, `DrawdownTestHarness`, then delete the four.
**Gate**: `ls tests/TradingEngine.Tests.Simulation/Harness/*Harness*.cs` shows only `EngineHarnessBuilder.cs`. All sim tests green via the builder.
**Commit**: `refactor(iter23-d1): unified EngineHarnessBuilder; delete 4 bespoke harnesses`

---

# GROUP E — Indicator correctness (the bug)

## E1 — Key indicators by full signature; dedup; use IndicatorCache
**Defect**: `RecomputeIndicatorsAsync` caches under `$"{symbol}:{req.Key}"` — **no timeframe, period, or params in the key** — so two strategies sharing a `Key` (or the same key across timeframes) overwrite each other, and the correctly-keyed `IndicatorCache.BuildKey(symbol, tf, name, period, barCount)` is bypassed.
**Do**:
1. Compute indicator values keyed by the **full signature** `(symbol, timeframe, type, period, stddev/param1/param2)` — route through `IndicatorCache` (extend its key to include type + extra params).
2. Dedup: collect the distinct `(signature)` set across all strategies, compute each **once** per bar close, share the result.
3. Give each strategy a snapshot view namespaced so it reads **its own** request's value (by its `Key`) mapped to the right signature — no cross-strategy bleed.
**Failing test first** (`Tests.Unit`): two strategies on the same symbol — A wants `Atr` period 14 on H1, B wants `Atr` period 50 on M15, **both using `Key="atr"`**. Assert A reads the 14/H1 value and B reads the 50/M15 value. Fails today (collision). Then goldens green.
**Commit**: `fix(iter23-e1): indicator values keyed by (symbol,timeframe,type,period,params); dedup; via IndicatorCache`

---

# GROUP F — Tick hotpath hardening

## F1 — Lean tick path
**Do**: the tick handler does exactly one thing — build a `TickReceived` event and enqueue it for the pump. Move exec-drain, force-close (now B3), account-update, and sim-feed off the per-tick inline path into events/effects. **Remove per-tick logging** (delete the `LogDebug("TICK|…")`; if kept, guard with `if (_logger.IsEnabled(LogLevel.Trace))` and no interpolation otherwise). No per-tick heap allocation (no `ToList()`, no new dictionaries on the tick path).
**Gate**: `grep -n 'LogDebug("TICK' src` returns nothing on the production path; a tick-throughput test (or BenchmarkDotNet micro-bench, documented) shows no per-tick allocation regressions. Simulation green and not slower.
**Commit**: `perf(iter23-f1): lean tick hotpath — event-translate only, no per-tick logging/allocation`

---

# GROUP G — Risk consolidation (collapse the double systems)

## G1 — One drawdown implementation
**Do**: delete mutable `DrawdownTracker`; use pure `DrawdownReducer` + `DrawdownState` everywhere (governor, dashboard, snapshots read `EngineState.Drawdown`).
**Gate**: `grep -rln "class DrawdownTracker" src` returns nothing; `grep -rn "new DrawdownTracker\|DrawdownTracker " src` returns nothing. Drawdown tests green.
**Commit**: `refactor(iter23-g1): single DrawdownReducer; delete mutable DrawdownTracker`

## G2 — One validation path
**Do**: consolidate `OrderDispatcher.Validate` + `RiskGate.ProjectWorstCase` + `ValidateBudgetEntry` + the downsizing loop into a single `RiskGate`/guard invoked from the submit path. Remove `RiskManager`'s overlapping `Validate`/`ValidateBudgetEntry` (keep only what the guard needs, in the kernel).
**Gate**: a single entry point produces all rejection reasons; test `RiskGate_returns_all_violation_reasons`. `OrderDispatcher` no longer calls two separate risk objects.
**Commit**: `refactor(iter23-g2): single RiskGate validation path; retire RiskManager.Validate`

## G3 — Governor into the kernel
**Do**: port `TradingGovernorService` to a pure `GovernorMachine` `(GovernorState, EngineEvent) → (GovernorState, effects)` in `Engine`, composed by `EngineReducer`; retire the mutable singleton. `EngineState.Governor` is the source of truth (the UI envelope reads it).
**Gate**: governor unit tests pass against the pure machine; `grep -rln "class TradingGovernorService" src` returns nothing. Goldens green.
**Commit**: `refactor(iter23-g3): GovernorMachine in kernel; retire mutable TradingGovernorService`

---

## Definition of Done
- [ ] **A**: `EngineWorker` ≤150 lines/≤6 deps; MarketEventSource + IndicatorSnapshotService extracted; 6 services out of Host.
- [ ] **B**: PnL, TradeClosed, risk reg/dereg, force-close, and RecordDecisionEvent are all reducer effects; no manual side-effects in PositionTracker/EngineWorker/BacktestDriver; `ConsumeForceClosePending` gone.
- [ ] **C**: per-module AddXxx + Scrutor scanning; Program.cs ≤40 lines.
- [ ] **D**: one `EngineHarnessBuilder`; 4 harnesses deleted.
- [ ] **E**: indicators keyed by full signature, deduped, via IndicatorCache; cross-strategy/timeframe collision test green.
- [ ] **F**: tick path does event-translate only; no per-tick logging/allocation.
- [ ] **G**: one drawdown impl, one validation path, governor in the kernel; no double risk systems.
- [ ] Architecture tests 3/3; 7 characterization goldens green at every commit; full simulation green.

## Sequencing
- **Groups C, E, F, G are independent** of the A/B extraction and of each other — start with whichever de-risks fastest. **E (indicator bug) and B3 (force-close) are correctness fixes — prioritize them.**
- Within A: A1→A2→A3→A4 (A4 needs B complete to truly hit ≤150; if B lags, A4's gate may need the reducer further along — do B before A4).
- D needs C1. G3 (governor in kernel) pairs naturally with B (reducer completion).
- Run the 7 goldens after every change; run full simulation before declaring any phase done.
- Convert relative dates to absolute. Today is 2026-06-15.
