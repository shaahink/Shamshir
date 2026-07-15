# Iter-20 — The Decision Kernel: deterministic event → state → effects

**Audience**: the implementing agent (OpenCode + DeepSeek v4 Pro). This document is self-contained — do not assume access to the conversation that produced it.
**Branch**: create `iter/20-engine-core` off `main`. **One commit per phase**, message prefix `feat(iter20-pN):` or `refactor(iter20-pN):`.
**Prime directive**: this is a *strangler-fig refactor of a just-stabilized system*. The 28/28 simulation tests and all unit/integration tests are GREEN today (commit `d46e7e2`). They must be GREEN at the end of **every** phase. If a phase cannot keep them green, stop and document why — do not push red.

---

## 0. Why we are doing this (read before touching code)

Today the engine's "stateful" logic is spread across three projects and several owners with no single source of truth:

- **Order pre-flight** (validate → size → submit) lives in `TradingEngine.Services/OrderDispatcher.cs`.
- **Fill/open/close state** lives in `TradingEngine.Services/PositionTracker.cs` as ad-hoc dictionaries (`_pendingOrders`, `_openPositions`, `_processedExecutionIds`).
- **In-trade management state** lives in `TradingEngine.Services/PositionManager.cs` behind a *separate, half-used* `PositionLifecycleState` enum that `PositionTracker` never reads.
- **Account FSM** (`TradingGovernorService`) and **drawdown accumulator** (`DrawdownTracker`) live in `TradingEngine.Risk` and are already clean — but they mutate private fields and journal through different sinks.
- **Journals** are three incompatible sinks: `BacktestJournal` (UI stream), `PipelineEventWriter` (DB, has `seq`), and `ProtectionLedgerWriter` (currently only *logs* — never persists its `ProtectionLedgerEntryEntity`).
- **`EngineMode` leaks into core logic** — read inside `EngineWorker` (`:661` branches equity persistence on mode) and threaded into `PositionTracker`.

The target is one organizing idea: **deterministic event processing with explicit state and externalized effects.**

```
                 ┌─────────────────────────────────────────────┐
   adapters →    │   TradingEngine.Engine  (PURE, no I/O)        │   → adapters
 (broker, clock, │                                              │  (broker submit,
  data feed)     │   EngineState ──Apply(state, EngineEvent)──▶  │   journal write,
      │          │        ▲              │                       │   equity persist)
      │          │        │              ▼                       │      ▲
   EngineEvent ──┼────────┘   (EngineState', IReadOnlyList<      │      │
                 │                         EngineEffect>)        │   EngineEffect
                 │   composed of:                                │
                 │     • PositionLifecycle  (FSM per position)   │
                 │     • GovernorMachine     (account FSM)       │
                 │     • DrawdownReducer     (fold over equity)  │
                 │     • RiskGate            (worst-case guard)  │
                 │     • BarEvaluationPipeline                   │
                 │   every transition emits a DecisionRecord     │
                 └─────────────────────────────────────────────┘
```

**The kernel never knows whether it is in backtest or live.** It consumes abstract `EngineEvent`s and returns abstract `EngineEffect`s. `EngineMode` exists *only* at composition (`EngineHostFactory`, adapters).

### What this unlocks (the four payoffs the design is optimised for)

1. **Easier UI.** The dashboard's "empty panels" problem (iter-19 F8) exists because each panel polls a bespoke field. After this refactor the UI reads **two** things: the `DecisionRecord` stream (filtered) and `AccountSnapshot` (the serialized `EngineState`). Every panel — signals, rejections, governor state, equity curve, open positions — is a *projection over the same stream*. New panels are filters, not new endpoints.
2. **Easier testing.** A pure `Apply(state, event) → (state', effects)` is table-driven unit-testable with no DI, no harness, no cTrader. Integration tests feed an *event script* and assert the *effect list*. Parity tests run the same script through `SimulatedBrokerAdapter` and `NetMQBrokerAdapter` and diff the effects.
3. **Real account snapshots.** `EngineState` **is** the snapshot — serialize it for warm starts and crash recovery; replay `DecisionRecord`s to reconstruct it (event sourcing). Fixes the iter-19 "max DD fabricated / equity not captured in backtest" issue because the snapshot is captured the same way in both modes.
4. **Cleaner code & naming.** One vocabulary — *event, effect, state, reducer, phase, record, snapshot* — replaces three dictionaries, two state enums, and three journal shapes.

---

## 1. Hard rules (carried from iter-17/18/19 — violations block merge)

- `decimal` for ALL money/price arithmetic. Never `double` for money.
- `IEngineClock` everywhere; no `DateTime.UtcNow` in engine code (Web controllers/UI excepted).
- `TradingEngine.Domain` has zero infrastructure dependencies.
- **NEW: `TradingEngine.Engine` is pure** — it may reference `TradingEngine.Domain` only. No `ILogger`, no `DateTime.UtcNow`, no EF, no adapters, no `EngineMode`. Enforced by an architecture test (Phase 0).
- cBot project (`TradingEngine.Adapters.CTrader`) targets **net6.0 / C# 10** — no C# 11+ constructs there. Untouched by this iteration.
- Single composition root: `EngineHostFactory`.
- `CancellationToken` on every async method.
- EF migrations only — no raw SQL schema changes.
- **Test-first**: every phase begins with a failing test. Run it, confirm it FAILS for the stated reason, then implement, then confirm green. The failing-first run is the proof the change addresses the real defect.

Test commands:
```
dotnet build TradingEngine.sln
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation        # the 28/28 gate
dotnet test --filter "FullyQualifiedName~<TestClassName>"
```

---

## 2. Target project layout (clean separation of logic)

| Project | Role | May reference |
|---|---|---|
| `TradingEngine.Domain` | Pure data: value objects, `EngineEvent`, `EngineEffect`, `EngineState`, `DecisionRecord`, `AccountSnapshot`, `PositionPhase`, all port **interfaces** | *(nothing)* |
| **`TradingEngine.Engine`** *(NEW)* | The pure decision kernel: `EngineReducer`, `PositionLifecycle`, `GovernorMachine`, `DrawdownReducer`, `RiskGate`, `BarEvaluationPipeline` | `Domain` only |
| `TradingEngine.Application` | Orchestration contracts / use-case interfaces (currently near-empty — repurpose, do not invent new infra here) | `Domain` |
| `TradingEngine.Application` *(repurposed)* | **Runtime/orchestration services** that coordinate I/O around the kernel: `DataFeedService`, `DailyResetService`, `StrategyBankService`/`StrategyRegistry`, `CrossRateStore`, `SymbolCatalog`, `BarEvaluation` coordinator (its pure rule part lives in `Engine`). Currently near-empty — fill it | `Domain`, `Engine` |
| `TradingEngine.Infrastructure` | Adapter & persistence **implementations**, effect executors. Foldered by concern: `Persistence/`, `Venues/CTrader/`, `Venues/Simulated/`, `Transport/NetMq/`, `MarketData/`, `Indicators/`, `Events/` | `Domain`, `Application`, `Engine` |
| **`TradingEngine.Experiments`** *(NEW)* | Walk-forward, variant scoring, experiment reporting (moved out of Host) | `Domain`, `Application`, `Engine`, `Infrastructure` |
| `TradingEngine.Host` | **Composition + pump ONLY.** `Program`, `EngineHostFactory`, `EngineWorkerDependencies`, `EngineWorker`. Everything else moves out. `EngineMode` lives here | all of the above |
| `TradingEngine.Web` | Blazor UI — reads projections only | Host, Infrastructure, … |
| `TradingEngine.Services` / `TradingEngine.Risk` | **Shrinking.** Logic migrates into `Engine`; what remains becomes thin glue, deleted when empty | — |

### Venue vs transport (orthogonal concerns — keep them apart)

Today `NetMQBrokerAdapter` fuses two unrelated things: the **cTrader venue** (broker semantics — submit order, tick/bar/execution streams) and the **NetMQ transport** (Sub/Router sockets, poller, send queue). "cTrader" is also scattered across `Adapters.CTrader` (the cBot, net6.0, runs *inside* cTrader — leave it) and `CTraderRunner` (CLI launcher). Target:

```
IBrokerAdapter (port, Domain)               IMessageTransport (port, Domain)
   ├─ CTraderBrokerAdapter ──── uses ───▶      └─ NetMqMessageTransport   (Transport/NetMq/)
   │     (Venues/CTrader/)                      └─ (future: gRPC/ws transport, no venue change)
   └─ SimulatedBrokerAdapter (Venues/Simulated/, in-process — no transport)
```

A **venue** adapter speaks broker semantics and depends on `IMessageTransport`, never on `NetMQ` directly. A **transport** moves framed bytes and knows nothing about orders. This lets you change wire protocol without touching venue logic, and add a venue without touching the wire.

> The end state is: **the decision logic lives in `TradingEngine.Engine` and nowhere else.** `Services` and `Risk` are emptied across the phases below; deleting the now-empty projects is the last phase. Do **not** big-bang-move them — each phase migrates one concern behind its existing public surface, proven equivalent by characterization tests, before the old code is removed.

### Naming conventions (apply consistently — this is half the value of the refactor)

- **Events** (past tense, things that happened): `BarClosed`, `TickReceived`, `OrderSubmitted`, `OrderFilled`, `OrderPartiallyFilled`, `OrderRejected`, `CloseRequested`, `EquityObserved`, `DayRolled`, `WeekRolled`. Base: `EngineEvent` (sealed record hierarchy).
- **Effects** (imperative, things to do): `SubmitOrder`, `ModifyStop`, `ClosePosition`, `RecordDecision`, `PersistSnapshot`, `RaiseAlert`. Base: `EngineEffect`.
- **State**: `EngineState` (aggregate) → `IReadOnlyDictionary<PositionId, PositionState>` + `GovernorState` + `DrawdownState` + `ExposureState`.
- **Phase enum** (replaces `PositionLifecycleState`): `PositionPhase { Intended, Submitted, Open, Reducing, Closing, Closed, Rejected }`.
- **The chokepoint**: `EngineReducer.Apply(EngineState, EngineEvent) → EngineDecision` where `EngineDecision(EngineState State, IReadOnlyList<EngineEffect> Effects)`.
- **The journal row**: `DecisionRecord(RunId, SimTimeUtc, Seq, Symbol, StrategyId, PhaseBefore, Event, GuardResult, PhaseAfter, Reason, DetailJson)`.
- **The snapshot**: `AccountSnapshot(SimTimeUtc, Balance, Equity, FloatingPnL, PeakEquity, DailyStartEquity, DailyDrawdown, MaxDrawdown, OpenPositions)`.

---

## Phase 0 — Scaffolding & guardrails (no behavior change)

**Goal**: create the empty kernel project and the architecture test that will keep it pure forever. This phase ships zero logic — it builds the fence before we move anything inside it.

**Do**:
1. Add `src/TradingEngine.Engine/TradingEngine.Engine.csproj` (net10.0), `ProjectReference` → `TradingEngine.Domain` only. Add to `TradingEngine.sln`.
2. Add a new test project `tests/TradingEngine.Tests.Architecture` (xUnit + `NetArchTest.Rules` or a reflection-based test if the package is unavailable).
3. Write these architecture tests:
   - `Engine_references_only_Domain` — `TradingEngine.Engine` assembly depends on no assembly except `TradingEngine.Domain` and the BCL.
   - `Engine_has_no_ILogger_no_DateTimeNow` — fails if any type in `Engine` references `ILogger`, `Microsoft.Extensions.*`, `DateTime.UtcNow`/`Now`, or EF types. (Scan IL/types via reflection.)
   - `EngineMode_only_in_host_and_infrastructure` — `EngineMode` is referenced only from `TradingEngine.Host`, `TradingEngine.Infrastructure`, test projects, and composition. (This one will FAIL today because of `PositionTracker` — mark it `[Fact(Skip="enabled in Phase 6")]` with a comment, and un-skip in Phase 6.)

**Acceptance**: solution builds; new architecture tests run (the EngineMode one skipped); 28/28 sim tests still green.
**Commit**: `feat(iter20-p0): add pure TradingEngine.Engine project + architecture guardrail tests`

---

## Phase 1 — Unify the decision journal (additive, lowest risk, highest leverage)

**Goal**: one `DecisionRecord` written through one sink. This is first on purpose — it makes every later phase observable and is purely additive.

**Do**:
1. In `Domain`: add `DecisionRecord` (see naming section) and port `IDecisionJournal { void Record(DecisionRecord r); }` in `Domain/Interfaces`.
2. In `Host`: make `PipelineEventWriter` implement `IDecisionJournal` (it already has `seq`, a channel, and a DB batch flush — reuse it). Map `DecisionRecord` → its existing `PipelineEvent` row; add columns via an EF migration if needed (`PhaseBefore`, `PhaseAfter`, `GuardResult`, `Reason`).
3. Route the three existing sinks through it:
   - `OrderDispatcher` rejections (the `LogWarning("Blocked…")` and `BudgetBlocked` paths) emit a `DecisionRecord` with `Event=OrderRejected`, `GuardResult=<violation code>`, `Reason=…` — **same code path** as an accepted order (which emits `Event=OrderSubmitted`).
   - `TradingGovernorService` state changes emit a `DecisionRecord`.
   - **Revive `ProtectionLedgerWriter`**: it currently only logs `GovernorStateChanged`. Make it persist `ProtectionLedgerEntryEntity` (the migration `20260612170000_AddProtectionLedger` already exists) AND emit a `DecisionRecord`.
4. Keep `BacktestJournal` for the UI stream but have it subscribe to the same `DecisionRecord` flow rather than receiving bespoke `Write(eventType, message)` calls.

**Failing test first** (`tests/TradingEngine.Tests.Integration`): run a tiny backtest that produces one accepted and one rejected order; assert the journal contains **both** as `DecisionRecord`s with correct `Event`/`GuardResult`, queryable from one repository. Confirm it fails today (rejections are only `LogWarning`, never persisted).

**Acceptance**: one query returns the full decision timeline (accepts + rejects + governor changes); `ProtectionLedgerEntryEntity` rows exist after a protection event; 28/28 green.
**Commit**: `feat(iter20-p1): unified DecisionRecord journal; revive ProtectionLedgerWriter persistence`

---

## Phase 2 — Account snapshot + IEquitySink (fixes the parity leak at EngineWorker:661)

**Goal**: capture equity/drawdown identically in backtest and live, and remove the first `EngineMode` branch from the core loop.

**Do**:
1. In `Domain`: add `AccountSnapshot` (see naming) and ports `IEquitySink { void Observe(AccountSnapshot s); }` and `IAccountSnapshotStore`.
2. In `Infrastructure`: two `IEquitySink` impls — `PersistentEquitySink` (live → DB) and `BufferedEquitySink` (backtest → in-memory ring buffer + end-of-run flush). Choose between them in `EngineHostFactory` by `EngineMode`.
3. In `EngineWorker.cs`: delete the `if (_engineMode != EngineMode.Backtest) _persistence.SaveEquitySnapshotAsync(...)` branch at `:661`; replace with `_equitySink.Observe(snapshot)`. The worker no longer reads `EngineMode` for equity.

**Failing test first**: run a backtest with a known losing sequence; assert the resulting equity curve / max drawdown is **non-empty and matches the hand-computed value**. This fails today because backtest equity is never captured (the open issue "max DD fabricated").

**Acceptance**: backtest produces a real equity/DD curve; live still persists; the `EngineMode` branch is gone from `EngineWorker`; 28/28 green.
**Commit**: `refactor(iter20-p2): AccountSnapshot + IEquitySink; capture backtest equity, drop EngineMode branch in EngineWorker`

---

## Phase 3 — DrawdownReducer + RiskGate worst-case projection (prop-firm survival)

**Goal**: move drawdown into the kernel as a pure reducer and add the guard that actually protects an FTMO account: *would this new position breach daily/overall DD if every open position hit its stop simultaneously?*

**Do**:
1. In `Engine`: add `DrawdownReducer` — a pure `(DrawdownState, EquityObserved) → DrawdownState`. Port the math verbatim from `TradingEngine.Risk/DrawdownTracker.cs` (it is already pure). Leave the old `DrawdownTracker` in place as a thin delegating shim for now.
2. In `Engine`: add `RiskGate.Project(EngineState, TradeIntent, IReadOnlyList<PositionState> open) → GuardResult`. It runs `DrawdownReducer` over a hypothetical equity = current minus (sum of every open position's worst-case loss to its stop) minus the candidate's worst-case loss, and returns `Blocked(reason)` if it crosses the daily or overall floor.
3. Call `RiskGate.Project` inside `OrderDispatcher` validation (in addition to current checks). A block emits a `DecisionRecord` with `GuardResult=WorstCaseDDWouldBreach`.

**Failing test first** (`Tests.Unit`): with N open positions whose combined stop-loss risk + a new candidate would breach the daily DD floor on simultaneous stop-out, assert the candidate is blocked with `WorstCaseDDWouldBreach`. Current code only checks present equity → it passes the order → test fails.

**Acceptance**: projection guard blocks the over-exposed Nth order; existing single-trade behavior unchanged; 28/28 green.
**Commit**: `feat(iter20-p3): pure DrawdownReducer + RiskGate worst-case simultaneous-stop projection guard`

---

## Phase 4 — PositionLifecycle FSM (the core refactor — characterization-test-anchored)

**Goal**: one explicit FSM owns the *entire* position lifecycle, replacing the split between `PositionTracker`'s dictionaries and `PositionManager`'s `PositionLifecycleState`. This is the highest-value and highest-risk phase — it is fenced by characterization tests.

**Do — in this order, do not reorder**:
1. **Characterize first.** Before writing the FSM, add `tests/TradingEngine.Tests.Simulation/Characterization/PositionLifecycleGolden.cs` that drives the *current* `PositionTracker`+`PositionManager` through these scenarios and snapshots their observable outputs (positions opened/closed, mods emitted, trade results, journal lines): full fill; partial-then-full fill; duplicate execution event; close at SL; close at TP; force-close; partial close; breakeven then trail; gap-through-stop. These goldens encode "what the system does today."
2. In `Engine`: add `PositionPhase` enum, `PositionState` record, and `PositionLifecycle.Apply(PositionState, EngineEvent) → (PositionState, IReadOnlyList<EngineEffect>)` — pure, no I/O. Fill in **every (phase × event) cell explicitly**; for impossible cells emit `RecordDecision(reason: "illegal transition")` rather than silently ignoring. This enumeration is the point — it is what kills the edge-case backlog.
3. **Unit-test every cell** in `Tests.Unit` (table-driven). Especially: fill-while-`Closing`, partial fill while `Reducing`, rejection while `Submitted`, bar/tick while `Closed` (no-op), gap straddling the stop.
4. Re-implement `PositionTracker` and `PositionManager` to **delegate** to `PositionLifecycle` — translate `ExecutionEvent`/`Tick`/`Bar` into `EngineEvent`s, call `Apply`, execute the returned effects (submit/modify/close via broker, `RecordDecision` via journal). Keep the old dictionaries only as long as needed to pass the goldens, then remove them.
5. Delete `PositionLifecycleState` (replaced by `PositionPhase`).

**Acceptance**: characterization goldens pass unchanged (behavior preserved); every (phase × event) cell has a unit test; `PositionTracker`/`PositionManager` contain no business state, only translation; 28/28 green.
**Commit**: `refactor(iter20-p4): single PositionLifecycle FSM replaces dictionary + dual-enum position state`

---

## Phase 5 — EngineReducer chokepoint + effect executor (compose the kernel)

**Goal**: one `Apply` composes position FSM + governor FSM + drawdown reducer; `EngineWorker` becomes a thin pump.

**Do**:
1. In `Engine`: add `EngineState` aggregate and `EngineReducer.Apply(EngineState, EngineEvent) → EngineDecision`. It dispatches the event to the relevant sub-machine(s) and concatenates their effects; **every** transition appends a `RecordDecision` effect (so journaling is structural, not bolted on).
2. In `Host`: refactor `EngineWorker` to the loop: *receive `EngineEvent` from an adapter → `Apply` → execute each `EngineEffect` through the matching port (`IBrokerAdapter`, `IDecisionJournal`, `IEquitySink`)*. All I/O lives in the effect executor and nowhere else.
3. `GovernorMachine` in `Engine`: wrap `TradingGovernorService` logic as a pure `(GovernorState, EngineEvent) → (GovernorState, effects)`; leave the old service as a shim until green, then remove.

**Acceptance**: an *event-script* integration test (feed a list of `EngineEvent`s, assert the emitted `EngineEffect` list) passes; `EngineWorker` contains no decision branches, only event translation + effect execution; 28/28 green.
**Commit**: `refactor(iter20-p5): EngineReducer chokepoint; EngineWorker reduced to event pump + effect executor`

---

## Phase 6 — Seal the EngineMode boundary

**Goal**: make parity a checked invariant.

**Do**:
1. Remove the `EngineMode` ctor param from `PositionTracker` and any other core type; move mode-stamping of `TradeResult` to the effect-executor / persistence boundary in `Host`/`Infrastructure`.
2. Un-skip `EngineMode_only_in_host_and_infrastructure` from Phase 0 and make it pass.

**Acceptance**: architecture test green with `EngineMode` confined to Host/Infrastructure/composition; 28/28 green.
**Commit**: `refactor(iter20-p6): confine EngineMode to composition; enable parity architecture test`

---

## Phase 7 — UI as projections over the journal

**Goal**: fix the iter-19 empty-dashboard problem by making every panel a filter over `DecisionRecord` + `AccountSnapshot`.

**Do**:
1. In `Web`: add one read model `RunProjection` that the dashboard binds to — derived from `IDecisionJournal` query + `IAccountSnapshotStore`. Panels (signals, rejections, governor timeline, equity curve, open positions) become filtered views of it.
2. Ensure the backtest status endpoint returns the fields the page actually reads (the iter-19 F8 mismatch) — now trivially, since they all come from the projection.

**Acceptance**: run a backtest from the dashboard and see populated signals/trades/governor/equity panels; an integration test asserts the projection is non-empty after a run; 28/28 green.
**Commit**: `feat(iter20-p7): dashboard reads RunProjection over unified journal + snapshots`

---

## Phase 8 — Collapse legacy projects + test taxonomy

**Goal**: remove now-empty glue and make the test layering explicit.

**Do**:
1. Delete `TradingEngine.Services` and `TradingEngine.Risk` if empty (or reduce to nothing but re-exports, then delete); update `.sln` and `ProjectReference`s. If anything non-trivial remains, document why in `HANDOVER.md` rather than force-deleting.
2. Confirm the test taxonomy and document it in `tests/README.md`:
   - **Unit** — pure: reducer/FSM/guard cells. No DI, no DB, no clock.
   - **Integration** — event-script in, effect-list + DB rows out.
   - **Simulation** — full `CtraderTestHarness` end-to-end (the 28/28).
   - **Architecture** — boundary invariants (Engine purity, EngineMode confinement).
   - **Parity** *(new, optional stretch)* — same event script through `SimulatedBrokerAdapter` vs `NetMQBrokerAdapter`, diff effects.

**Acceptance**: solution builds with logic consolidated in `Engine`; all suites green; `tests/README.md` documents the taxonomy.
**Commit**: `refactor(iter20-p8): remove emptied Services/Risk projects; document test taxonomy`

---

## Phase 9 — De-bloat Host: persistence → Infrastructure, services → Application

**Goal**: Host shrinks to *composition + pump only*. No persistence, no orchestration services, no config loading inside it.

**Do** (pure moves — no logic change; rename namespaces, update `ProjectReference`s and DI registration in `EngineHostFactory`):
1. Move to `Infrastructure/Persistence/`: `EquityPersistenceHandler`, `TradePersistenceHandler`, `PipelineEventWriter` (now the `IDecisionJournal` impl from P1). They sit next to the repos/entities they already write to.
2. Move to `Application/` (orchestration): `DataFeedService`, `DailyResetService`, `BarEvaluationHandler` (its pure rule core to `Engine`, the coordinating shell to `Application`), `CrossRateStore`, `StrategyBankService`, `StrategyRegistry`, `SymbolCatalog`.
3. Move config loading (`ConfigLoader`) to `Infrastructure` (it does file I/O) under `Infrastructure/Configuration/`.
4. Host keeps only: `Program`, `EngineHostFactory`, `EngineWorkerDependencies`, `EngineWorker`, `GlobalUsings`.

**Test**: this is move-only; the gate is that **all suites stay green** with the new locations. Add an architecture test `Host_contains_only_composition_and_pump` (assert Host has no persistence/repository types and no orchestration services beyond the pump).

**Acceptance**: Host file count drops to the five above; persistence and services live in their proper projects; all suites green.
**Commit**: `refactor(iter20-p9): de-bloat Host — persistence to Infrastructure, runtime services to Application`

---

## Phase 10 — Separate venue from transport

**Goal**: split `NetMQBrokerAdapter` into a cTrader **venue** + a NetMQ **transport**, organized by concern.

**Do**:
1. In `Domain/Interfaces`: add `IMessageTransport` — `ConnectAsync`/`DisconnectAsync`, `Send(identity, payload)`, and an inbound message event/`ChannelReader`. Pure wire semantics; no order/broker concepts.
2. In `Infrastructure/Transport/NetMq/`: add `NetMqMessageTransport : IMessageTransport` — move *all* `using NetMQ`, `SubscriberSocket`/`RouterSocket`/`NetMQPoller`/`NetMQQueue` code here. This is the **only** file in the solution that references NetMQ.
3. In `Infrastructure/Venues/CTrader/`: add `CTraderBrokerAdapter : IBrokerAdapter` — the broker semantics from the old adapter (streams, `SubmitOrderAsync`, execution parsing), depending on `IMessageTransport`, not NetMQ.
4. Move `SimulatedBrokerAdapter` → `Infrastructure/Venues/Simulated/`. Move `BacktestReplayAdapter`, market-data providers to sibling folders by concern.
5. Update `EngineHostFactory`: in live/paper mode compose `CTraderBrokerAdapter(new NetMqMessageTransport(...))`; in backtest compose `SimulatedBrokerAdapter`. Delete `NetMQBrokerAdapter`.

**Failing test first**: add a fake `IMessageTransport` and assert `CTraderBrokerAdapter.SubmitOrderAsync` produces the correct framed message on it and that an inbound execution message surfaces as an `ExecutionEvent` — proving venue logic is testable **without NetMQ**. Impossible today (the two are fused).

**Acceptance**: NetMQ is referenced in exactly one file; venue logic unit-tested with a fake transport; live + backtest paths work; all suites green.
**Commit**: `refactor(iter20-p10): split cTrader venue from NetMQ transport behind IMessageTransport`

---

## Phase 11 — Extract Experiments project

**Goal**: experiments are a distinct concern, not part of the engine host.

**Do**:
1. Create `src/TradingEngine.Experiments/` (net10.0). Move `Host/Experiments/*` (`ExperimentRunner`, `WalkForwardSplitter`, `VariantScorer`, `ExperimentReportWriter`, `ConfigOverrideApplier`, `ExperimentCli`) into it. Reference `Domain`, `Application`, `Engine`, `Infrastructure`.
2. Update `.sln`, `ProjectReference`s, and any Web/Host entry points that launch experiments.

**Acceptance**: experiments build and run from the new project; Host no longer contains an `Experiments/` folder; all suites green.
**Commit**: `refactor(iter20-p11): extract TradingEngine.Experiments project out of Host`

---

## 3. Definition of Done (whole iteration)

- [ ] Decision logic lives in `TradingEngine.Engine`; architecture tests prove its purity and `EngineMode` confinement.
- [ ] One `DecisionRecord` journal; `ProtectionLedgerEntryEntity` actually persists.
- [ ] `AccountSnapshot` captured identically in backtest and live; backtest DD is real.
- [ ] One `PositionLifecycle` FSM; every (phase × event) cell unit-tested; characterization goldens unchanged.
- [ ] `EngineReducer` is the single state-change chokepoint; `EngineWorker` is a pump.
- [ ] `RiskGate` worst-case projection guard blocks over-exposed orders.
- [ ] Dashboard panels populate from one projection.
- [ ] Host contains only composition + the pump; persistence in Infrastructure, runtime services in Application (architecture test enforces it).
- [ ] cTrader **venue** is split from NetMQ **transport** behind `IMessageTransport`; NetMQ referenced in exactly one file; venue logic unit-tested with a fake transport.
- [ ] Experiments live in their own `TradingEngine.Experiments` project.
- [ ] All hard rules respected; 28/28 simulation + all unit/integration green at every commit.

## 4. Sequencing notes for the agent

- Phases 0–3 are low-risk and independently shippable; do them in order and you will have made the system *more observable and safer* before touching the position FSM.
- **Phase 4 is the one that can break things.** Do not start it until Phase 1's journal is in place (you need it to debug) and the characterization goldens are written and passing against the *old* code.
- If any phase turns red and you cannot make it green within the phase's scope, **stop, revert the phase, and write findings under a `## Phase N findings` heading here.** A documented blocker beats a red push.
- **Phases 9–11 are organizational (mostly file/namespace moves) and are low logic-risk but high churn.** They can run after P8, or be interleaved earlier *between* logic phases — P10 (venue/transport) in particular is independent of the kernel work and can be done any time after P1. Do them when the tree is otherwise quiet to avoid merge pain. Move files in small batches and keep the build green after each batch.
- Convert any relative dates you write into absolute dates. Today is 2026-06-14.
