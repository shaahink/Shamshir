# Iter-35 — Replay-Native Kernel: One Decision Core, One Journal, Replayable Backtests

**Status:** PLAN WRITTEN (not executed) — 2026-06-19
**Branch base:** `iter/34-ui-completion` → cut `iter/35-kernel`
**Shape (owner-locked):** **one mega-iteration**, **replay-native event-sourced kernel**.
**Inputs:** `docs/reference/SYSTEM-AUDIT.md`, `docs/OPEN-ISSUES.md` (75+ findings), and the verification + kernel reading in this plan.
**Audience:** the implementation agent (OpenCode/DeepSeek).

> **Thesis.** Almost every confirmed bug is a symptom of one root cause: the pure kernel (`EngineState` + `EngineReducer` + `PositionLifecycle` + `EngineEffect`/`EffectExecutor`) was built in iter-20 but only the **Positions** slice was ever wired. Risk, drawdown, protection, governor, resets, and SL/TP exits still run **imperatively beside** the kernel (`RiskManager`, `AccountProcessor`, `OrderDispatcher`, `EngineRunner.SimulateBarExitsAsync`), with **dead twins** left behind (`RiskGate`, `GovernorMachine`, the reducer's `BarClosed/EquityObserved/DayRolled` branches). This iteration **finishes the kernel**, makes it the single authority, feeds it from a **recorded data tape**, turns its **effect stream into the journal**, and thereby gets **deterministic replay** (re-run the same data with different strategy/risk) and **scenario pressure-testing** as consequences of the design — not as bolted-on features.

> **Anti-stall rule (non-negotiable).** iter-22 stalled by "double-systeming" (new kernel beside old loop, old never deleted) and iter-33 by big-bang. This plan is mega-scope but **strangler-disciplined**: the new kernel runs behind the **existing** `TradingLoop.ProcessBarAsync` surface; a **golden replay gate** (Part A) pins behavior; **old paths are deleted the moment the new path passes** (see the Kill-List). No phase that adds a new authority may close while its imperative twin still executes in production.

---

## 0. How to use this plan

1. Read Section 1 (verification) and Section 2 (kernel model + the Kill-List) before touching code.
2. **Part A is the spine and is strictly sequential and gated.** Do not start Part B/C/D until the Part A golden replay gate is green.
3. Failing-test-first everywhere. Each phase lists the test to write first and a machine-checkable gate.
4. The kernel reducer must be **pure**: a function of `(EngineState, EngineEvent)` only. No `DateTime.UtcNow`, no `Guid.NewGuid()`, no I/O inside the reducer or `PositionLifecycle`. All nondeterminism (wall-clock, id minting, broker calls) lives in **effects** executed by `EffectExecutor`, or is **carried on the event** (sim-time, seq) / **seeded**. This is what makes replay bit-identical.
5. Money math stays `decimal`. Sim-time (from the tape), never wall-clock, on every journal/monitor record. Follow `docs/reference/` + project code standards.
6. You do not need cTrader or a full app run. Unit/Arch/Golden/Simulation suites are the loop (`docs/reference/TEST-ARCHITECTURE.md`).

---

## 1. Verification of the audit (confirmed before planning)

### 1.1 Confirmed live
C3/H1 (`RiskManager.cs:186` worst-case floor base wrong: trailing must use `PeakEquity`, fixed must use `InitialAccountBalance`), C4 (`RiskManager.cs:299-307` MaxDD protection never exits; `ProtectionResetPolicy` unread), H2 (`RiskManager.cs:103-109` gate checks only daily+total; weekly/monthly reach `ConstraintSet` but are dropped), H7 (governor `OnDailyReset` has no caller), C5 (`SimulatedBrokerAdapter` `AccountUpdate(_currentBalance, 0m, _currentBalance)` → Equity=0, vs record `IBrokerAdapter.cs:72` `(Balance, Equity, FloatingPnL)`), C6 (partial close: no costs/balance/AccountUpdate), C7 (`ExpiryBarCount--` per tick), C8 (`SessionBreakoutStrategy.cs:55-56` range = whole buffer), C9/H17/H19 (journal channels `DropOldest` + ignored `TryWrite` + `buffer.Clear()` at top of flush loop), C12 (`RunsController.cs:88` cancel ignores `runId`), H23 (`StartRunRequest` has no `StrategyOverrides`).

### 1.2 Corrections to the audit (trust these)
- **H3/H4 point at dead code.** `RiskGate.ProjectWorstCase` is called only by `RiskGateTests.cs`. Live worst-case logic is inline in `RiskManager.ValidateOrder`; there H3 is already fixed (respects `DailyDdBase`) but C3/H1 are live. → the kernel absorbs this logic; **delete `RiskGate`**.
- **C14 is currently inert.** `SlTpHelpers.IsSlValid` has **no caller** in the dispatch pipeline. Two real issues: SL-distance validation is unenforced (**NEW-3**), and `MaxSlPips=0` would reject everything if wired. Fix both in the kernel gate; treat `MaxSlPips<=0` as "no limit".
- **`DailyResetService` is in `TradingEngine.Application`**, not Host; it is a wall-clock `BackgroundService` (`Program.cs:36`) irrelevant to sim-time backtests (**NEW-2**).

### 1.3 New findings
- **NEW-1** (High): daily reset rolls on raw UTC `DayOfYear` (`AccountProcessor.cs:64-72`), ignoring `dailyResetTimeUtc`/`dailyResetTimezone` from the rule set → daily-DD window misaligned with the real FTMO day.
- **NEW-2** (Low): two daily-reset mechanisms (hosted `DailyResetService` + in-loop `AccountProcessor`).
- **NEW-3** (High): SL-distance validation unwired.
- **NEW-4** (Med): four overlapping observability sinks (PipelineEvents, BarEvaluations, logger lines, SignalR), two lossy, no single per-bar "what happened" record.
- **NEW-5** (Med/perf): hot-path allocations in `TradingLoop.ProcessBarAsync` (3× `new Dictionary` per strategy, `Sum(Count)` per strategy, `RemoveAt(0)` O(n), `ToList()` per trailing call, fire-and-forget `PublishAsync`).
- **NEW-6** (Med): per-trade chart fails for non-H1 — `TradeSummary` has no `timeframe`, so `trade-detail.component.ts:63` always queries H1.
- **NEW-7** (Low): live monitor `setInterval` never cleared.
- **NEW-8**: no per-protection enable toggle anywhere.
- **NEW-9** (this read): **dead governor twin** — `GovernorMachine.ApplyBar/ApplyDailyReset` (kernel) is reachable only from dead reducer branches; live governor is `TradingGovernorService`. Same pattern as `RiskGate`. One must win (kernel).
- **NEW-10**: kernel nondeterminism — `EffectExecutor` mints `Guid.NewGuid()` for `TradeResult.Id` and reads `_clock.UtcNow`. For bit-identical replay, output-affecting ids/timestamps must be seeded or tape-derived.

### 1.4 Kernel reality (verified by reading the code)
- `EngineState` (`Domain/RiskAndEquity/EngineState.cs`): `{ Positions, Governor, Drawdown, OpenPositionCount }`. **Doc-comment admits only `Positions` is wired; `Governor`/`Drawdown` are "frozen at Empty … RiskManager owns the authoritative … state imperatively."**
- `EngineReducer.Apply(state, event) → EngineDecision(state', effects)` (`Engine/EngineReducer.cs`): **wired** for `OrderSubmitted/Filled/PartiallyFilled/Rejected/Cancelled`, `CloseRequested`, `ForceCloseAllRequested`. **Explicitly UNWIRED** (dead) for `BarClosed`, `TickReceived`, `EquityObserved`, `DayRolled`, `WeekRolled`, `MonthRolled` — each carries an "UNWIRED — RiskManager is authoritative" comment. `DetectSlTpExit` (the single clean SL/TP authority) is dead; backtest exits run in `EngineRunner.SimulateBarExitsAsync`.
- `EngineEffect` (`Domain/Events/EngineEffects.cs`): `SubmitOrder, ModifyStopLoss, ModifyTakeProfit, CloseOpenPosition, RecordDecisionEvent, PublishTradeClosed, RegisterRisk, DeregisterRisk`.
- `EffectExecutor` (`Host/EffectExecutor.cs`) is **already the single I/O boundary** (effects → broker/eventbus/journal/risk-manager). It is exactly where the kernel meets the world.

**Conclusion:** the redesign is a *consolidation onto existing scaffolding*, not a green-field rewrite. The work is: (1) make the UNWIRED reducer branches authoritative, (2) move risk/sizing/protection/reset decisions into pure kernel functions producing effects, (3) feed the reducer from a recorded tape, (4) make the effect/decision stream the journal, (5) delete the imperative twins.

---

## 2. The target architecture + Kill-List

### 2.1 Target
```
                          ┌──────────────────────────────────────────┐
   Recorded Tape          │                 KERNEL (pure)            │
   (Dataset: bars now,    │   step(EngineState, EngineEvent)         │
    ticks later;          │       → EngineDecision(state', effects[])│
    content-addressed) ──►│   owns: Positions, Drawdown, Governor,   │──► effects[] ──► EffectExecutor (only I/O)
        + ConfigSet       │          Protection, Risk gate, SL/TP,   │        ├─ SubmitOrder/Modify/Close → IBrokerAdapter
        (strategy+risk+   │          sizing, resets                  │        ├─ PublishTradeClosed → trade store
         prop-firm+gov,   │   NO wall-clock, NO Guid.New, NO I/O      │        └─ AppendJournal(StepRecord) → ONE journal
         captured         └──────────────────────────────────────────┘
         immutably)                       ▲   │
                                          │   ▼
                              feedback events (fills, account) re-enter as EngineEvents
```
- **Run = (DatasetRef, ConfigSet, Seed).** Data is decoupled from config. Re-running a DatasetRef with a different ConfigSet = replay with different strategy/risk. "Save as new backtest" = persist a new `(DatasetRef, ConfigSet)` Run.
- **Journal = the kernel's append-only `StepRecord` stream** (input event + decision + effects + risk/regime snapshot, by `seq`/sim-time). Lossless, single sink, SQL-queryable, NDJSON-exportable, human-renderable. The live page streams its tail.
- **Determinism:** identical `(DatasetRef, ConfigSet, Seed)` ⇒ bit-identical journal. This is the regression oracle and the golden gate.

### 2.2 Kill-List (delete when the replacement passes — enforced by gates)
| Delete | Replaced by | Guard gate |
|--------|-------------|-----------|
| `Engine/RiskGate.cs` + `Phase3BTests/RiskGateTests.cs` | kernel worst-case in reducer | `grep -rn "RiskGate" src tests` → 0 |
| `GovernorMachine` **or** `TradingGovernorService` (keep the kernel `GovernorMachine`; retire the service) | reducer `BarClosed`/`DayRolled` governor branch | one governor impl referenced in `src` |
| `RiskManager`'s imperative state mutation (`CurrentState`, `Drawdown`, `_protectionCause`, `OnDailyReset` side-effects) | `EngineState.{Drawdown,Protection,Governor}` via reducer | `RiskManager` becomes a thin pure-calc helper or is deleted |
| `AccountProcessor` breach-watchdog block (`cs:79-115`) | reducer `EquityObserved` → `ForceCloseAllRequested`/protection in state | watchdog logic not duplicated outside kernel |
| `EngineRunner.SimulateBarExitsAsync` | reducer `BarClosed` → `DetectSlTpExit` → `CloseOpenPosition` | single SL/TP authority |
| Duplicate journal sinks (`PipelineEventWriter` *and* `BarEvaluationHandler` as independent writers) | one `JournalWriter` over `StepRecord`; others become projections or are deleted | exactly one journal writer in `src` |
| `IRiskManager.OnDailyReset` redundant w/ hosted `DailyResetService` | one sim-time reset path through the reducer | one reset mechanism |

> If retiring `RiskManager`/`AccountProcessor` wholesale proves too entangled within the iteration, the **minimum** acceptable end-state is: their decision logic is **moved into pure kernel functions** and the imperative copies are **deleted**, even if the class shells remain as thin effect-executors. Two authorities for the same decision is the one outcome this plan forbids.

---

## 3. Part A — The spine (sequential, gated by the golden replay test)

### A0 — Golden replay oracle FIRST (build the gate before changing anything)
**Goal:** lock current behavior so the kernel migration is provably behavior-preserving.
**Do:** add a `GoldenReplayTests` suite that runs a small fixed multi-symbol bar fixture (deterministic) through the **current** `TradingLoop`/engine with a fixed ConfigSet and snapshots the full output: ordered trades, equity curve, and every journal/decision line (normalize wall-clock fields out). Commit the snapshot as the baseline.
**Gate:** `dotnet test --filter ~GoldenReplay` green and the baseline snapshot file exists. **Every later Part-A phase must keep this green** (the snapshot only changes when we deliberately accept a corrected behavior — and then the diff must be reviewed and the reason recorded in HANDOVER).

### A1 — Recorded data tape + Run = (DatasetRef, ConfigSet, Seed)
**Goal:** decouple market data from run config so replay is possible.
**Do:**
- Define a `Dataset` = ordered bar stream for `(symbols, timeframes, [from,to])`, content-addressed by a hash of the canonical bar bytes. Persist a `Datasets` table (id, symbols, tfs, range, source, rowcount, hash). The existing `Bars` table is the storage; a Dataset is a named, hashed view over it. (Ticks: define the same seam now — `Dataset.Granularity ∈ {Bar, Tick}` — but only implement Bar; leave a `// TICKS: …` seam.)
- Define `ConfigSet` = immutable snapshot of everything that determines behavior: strategy configs, risk profile, prop-firm ruleset, governor, sizing, regime, rotation, news windows. Capture it at run start (extend the existing `EffectiveConfig`/`ResolveEffectiveConfigJsonAsync`) and persist as a JSON blob with its own hash.
- `Run` row references `DatasetId` + `ConfigSetId` + `Seed`. Add to the run repository.
**Test-first:** dataset hashing is stable (same bars → same hash; one changed bar → different hash); a Run round-trips its DatasetRef + ConfigSet.
**Gate:** `Datasets`/`ConfigSets` migrations present; golden test still green.

### A2 — Finish the pure kernel (make it authoritative for all slices)
**Goal:** one decision core. `EngineState` becomes authoritative for `Positions`, `Drawdown`, `Protection`, `Governor`; the reducer owns the pre-trade gate, worst-case projection, sizing decision, breach/force-close, resets, and SL/TP exits.
**Do (incrementally, keeping A0 green after each sub-step):**
- Extend `EngineState` with the authoritative `Protection` slice (`InProtectionMode`, `Cause`, `ResetPolicy`, `Until`) and ensure `Drawdown`/`Governor` are updated by the reducer, not "frozen at Empty".
- Wire the dead branches as the single authority and remove their "UNWIRED" comments:
  - `EquityObserved` → `DrawdownReducer.Apply` + breach detection → emit `ForceCloseAllRequested`/enter protection (absorbs `AccountProcessor` watchdog).
  - `BarClosed` → `DetectSlTpExit` → `CloseOpenPosition` (absorbs `EngineRunner.SimulateBarExitsAsync`); trailing/breakeven via `PositionLifecycle` → `ModifyStopLoss`.
  - `DayRolled/WeekRolled/MonthRolled` → reducer resets, honoring `ProtectionResetPolicy` (C4) and the rule set's reset time/zone (NEW-1); governor reset here (H7); single reset path (NEW-2).
- Move the **pre-trade gate + worst-case + sizing** into pure kernel functions producing either a `SubmitOrder` effect or a `RecordDecisionEvent(reject)` — replacing `OrderDispatcher`/`RiskManager.Validate/ValidateOrder/CalculateLotSize`. Correct the bugs here (so they can't recur): C3/H1 floor base via one `MaxDdFloorBase` helper; H2 weekly/monthly checks; NEW-3/C14 SL validation with `MaxSlPips<=0`="no limit"; H5/H6 sizing (`AntiMartingale`, drawdown-scale on fixed methods); M7 costs in worst-case; M6 profit-target uses equity; M8 velocity every update.
- Resolve **NEW-10 determinism:** the reducer/`PositionLifecycle` take ids + sim-time from the event; `EffectExecutor` stops minting `Guid.NewGuid()`/reading wall-clock for anything that lands in the journal/trade output (seed a deterministic id source or derive from `(runSeed, seq)`).
- Pick the governor (NEW-9): keep kernel `GovernorMachine`, retire `TradingGovernorService` (move any needed logic into `GovernorMachine`).
**Test-first (pure, fast — these are the new home for the risk bugs):** trailing-mode floor anchored to peak rejects a breaching order; fixed-mode floor anchored to initial balance; weekly/monthly limits block; MaxDD protection persists per `ProtectionResetPolicy`, daily-DD clears; governor profit-lock clears on day-roll; reset honors 22:00 Prague boundary (NEW-1); over-wide SL rejected, `MaxSlPips=0` doesn't reject; force-close-all emits `CloseOpenPosition` per position by **OrderId**.
**Gate:** kernel reducer unit suite green; golden test green or diff-reviewed-and-accepted; `grep -n "UNWIRED" src/TradingEngine.Engine/EngineReducer.cs` → 0; `grep -rn "SimulateBarExitsAsync" src` → 0.

### A3 — The journal IS the effect/decision stream (one lossless sink)
**Goal:** the user's "perfect journal" — one append-only record of exactly what happened each step, human + machine, lossless, downloadable.
**Do:**
- Define `StepRecord { runId, seq, simTimeUtc, inputEvent (typed/JSON), decision (gate verdict + reason), effects[], riskSnapshot (equity/balance/dailyDD/maxDD/protection/governor/distance), regime, per-strategy verdicts (signal/none + reason, indicators per Q5 sampling) }`. The reducer already produces `(event → decision → effects)`; this record wraps it.
- One `JournalWriter` consuming `StepRecord` on a **lossless `Wait` channel, single reader**, persisting to a `Journal`/`StepRecords` table. Fix the persistence bugs here once: no `DropOldest`, `buffer.Clear()` only after a successful flush (retry on failure, never silently drop — C9/H17/H19), batch grouped by `RunId` (C10), WAL + busy_timeout + retry (H20/H21), dispose drains before cancel (M16).
- Make `PipelineEvents`/`BarEvaluations` either **projections** over `StepRecords` or delete them (Kill-List). Normalizer fixes: M11 (`OrderCancelled` → `ENTRY_EXPIRED` vs `CANCELLED` by reason), M12 (add `TRAIL`/`BREAKEVEN`/`PARTIAL`).
- Endpoints: `GET /api/runs/{id}/journal` SQL-paged by `seq`/`afterSeq` (fix M17 — no `.AsEnumerable()` whole-set); `GET /api/runs/{id}/journal/export` → NDJSON (one StepRecord per line) for download; a compact text renderer for CLI/logs.
- All journal/monitor timestamps = sim-time (H25/H26).
**Test-first:** golden journal test — tiny series + stub strategy firing on bar k: assert one StepRecord per step, the signal+reason+order on bar k, the close+costs on the fill step, NDJSON round-trips, and **no drop under a burst** (write N+1 into capacity-N → all N+1 persisted; repo throws once → batch retried not lost).
**Gate:** exactly one journal writer in `src` (`grep`); `grep -rn "DropOldest" src/...Events src/...Persistence src/...Caching` → 0 (market-data streams excepted, documented); golden journal test green.

### A4 — Replay engine + scenario harness
**Goal:** re-run a DatasetRef with same or different ConfigSet; save as a new backtest; pressure-test the rule engine.
**Do:**
- A `ReplayRunner` that, given `(DatasetId, ConfigSetId, Seed)`, materializes the tape into `EngineEvent`s and drives the kernel — the **same** code path as a fresh backtest (no replay-only fork).
- API/UI: from a finished run, "Re-run with…" (pick a different risk profile / strategy set / prop-firm toggles) → creates a new Run referencing the same `DatasetId` + a new `ConfigSetId`, executes, and is itself saved/listed as a normal backtest.
- **Determinism test:** re-running `(DatasetId, ConfigSetId, Seed)` reproduces the prior run's journal/trades **bit-identically** (after wall-clock normalization). This is the strongest correctness guarantee in the system — wire it as a CI-style test.
- **Scenario/pressure harness:** a test+CLI that takes one DatasetId × a matrix of ConfigSets (risk dialed to extremes, toggles on/off, adversarial loss sequences) and asserts **invariants** on the journal: never exceed a configured DD without entering protection; force-close fires iff `forceCloseOnBreach` on; weekly/monthly enforced when enabled; no trade passes the gate whose worst case breaches a floor.
**Test-first:** the determinism test + at least 3 invariant scenarios.
**Gate:** determinism test green; scenario suite green; "Re-run with different risk" produces a new listed run over the same dataset hash.

### A5 — Indicator engine (incremental, shared, correct, fast)
**Goal:** indicators computed once per `(symbol, tf, bar)`, shared across strategies via one snapshot, per-strategy views as projections, keys correct, cancellation honored.
**Do:**
- Make indicator state **incremental** (update on each new bar; no full recompute over the buffer per bar). One canonical key scheme (the iter-29 bug was a key-prefix mismatch) with an Arch/unit test that every strategy's requested keys resolve.
- Compute the union of indicators needed by the **active** strategies once per bar; expose a shared read-only snapshot; per-strategy `BuildStrategyIndicatorValues` is a projection (no recompute, no per-strategy `Dictionary` copy). Honor `CancellationToken` (M9).
- O(1) warm-up ring buffer replacing `List.RemoveAt(0)`; configurable capacity ≥ max `RequiredBarCount` (H8/H9).
**Test-first:** a strategy requesting indicators X,Y receives correct values with one computation per bar (assert recompute count via a counting fake); a >500-bar warm-up strategy still evaluates.
**Gate:** golden test green; `grep -n "RemoveAt(0)" src/TradingEngine.Host` → 0; indicator-key resolution test green.

**▶ PART A GATE (must be green before Part B/C/D):** golden replay + golden journal + determinism tests green; Kill-List greps for `RiskGate`/`UNWIRED`/`SimulateBarExitsAsync`/duplicate journal writers all → 0; one governor impl; the engine runs end-to-end through the kernel with the effect-log journal.

---

## 4. Part B — Risk, money, protection (now kernel-pure)

> Most B-bugs are **fixed in A2** as part of moving logic into the kernel. Part B is the toggle feature + the venue-side money correctness that the kernel depends on.

### B1 — Toggleable protections "without faff"
**Goal:** each protection (daily/weekly/monthly/max DD, profit-target gate, news, weekend, force-close-on-breach, governor) independently on/off + tunable; stored as JSON, seeded to DB, edited from UI, persisted back; DB is source of truth.
**Do:** add a `ProtectionToggles` object to `PropFirmRuleSet` (persisted in the existing JSON blob — no schema break), defaults = current behavior (daily+max on, weekly/monthly off). Thread into `ConstraintSet` (kernel reads `Enabled*` flags; a disabled check is skipped in the reducer gate **and** the breach branch). `GET/PUT /api/prop-firms` + `/api/risk-profiles` (+ governor) using the existing `UpsertAsync`; fix M18 (drop stale `GovernorOptions` singleton — read DB) + M19 (no bare `catch{}`). Angular Settings page with real toggle switches + numeric inputs. `POST /api/config/export` to write DB config back to `config/**.json` on demand (one-way, no background sync).
**Test-first:** `weeklyDd:false` → no violation at threshold; `true` → violation. `forceCloseOnBreach:false` → enters protection, no force-close; `true` → force-closes. `PUT` then a new run resolves the mutated ConstraintSet (assert via the journal risk snapshot).
**Gate:** toggle tests green; a run after `PUT` reflects new limits in its StepRecord risk snapshot.

### B2 — Venue money/fill correctness (kernel depends on truthful AccountUpdates)
**Do:** C5 (`AccountUpdate(_currentBalance, _currentBalance, 0m)` for flat book; balance+floating when open — all 3 sites); C6 (partial close: `ComputeCosts`, balance update, cost-stamped exec + `AccountUpdate`); C7 (`ExpiryBarCount--` per **bar**, not tick); C8 (`SessionBreakout` range = session window only); H13 (`FilledLots`>0 on full close); H14/H15/H16 (align fill ts/price; directional bid/ask for floating + fills, consistent replay↔simulated); H11 (synthetic close uses last price, not 0); M10 (`TradeCostCalculator` doesn't swallow to zero). cTrader-side C1/C2/M1 (limit/stop exec + `cancel_order` handler + partial-close cost timing) — **code + `FakeTransport` contract test only; live verification is an owner follow-up.**
**Test-first:** force-close emits `Equity==Balance` (not 0) and doesn't trip the watchdog (the C5 regression); partial close moves balance by net PnL with costs stamped; SessionBreakout range = window on a 3-day fixture; replay close `FilledLots>0`.
**Gate:** venue/simulation tests green; `grep -n "0m, _currentBalance" src/.../SimulatedBrokerAdapter.cs` → 0.

---

## 5. Part C — Surfaces (web, charts, live page)

### C1 — Web run lifecycle
**Do:** C11 (replay path uses caller's `CancellationToken`, linked with timeout); C12 (cancel only the target `runId` via a per-run CTS registry); C13 (resolve `api/backtest` route collision); H22 (pre-run setup inside try/finally — no stuck "starting"); H23 (`StrategyOverrides` on `StartRunRequest` → `cfg`/run plan → `EffectiveConfigResolver` → ConfigSet); H24/H25/H27 (`Interlocked` BarCount; purge `_runs` + `_lastSentTicks` on completion).
**Gate:** cancelling run A leaves run B running; a throw in setup marks run `failed`; `StrategyOverrides` changes the resolved ConfigSet; `grep -n "StopAllAsync" src/.../RunsController.cs` → 0.

### C2 — Per-trade chart (explicit ask — "hasn't happened yet")
**Findings:** the pieces exist (`CandleChartComponent`, `BarsController`, `TradeDetailComponent`). It fails on NEW-6 (no `timeframe`) and lacks SL/TP markers.
**Do:** add `timeframe` to the trade DTO + persist on the trade; frontend uses `t.timeframe` (drop `|| 'H1'`). Add SL/TP price lines + time-anchored entry/exit markers (`BarResponse.Time` unix-seconds already matches `b.time*1000`). Wire trade-list rows → `/trades/:id`; add cost columns (M21). Meaningful empty-state when no bars in window.
**Gate:** a known trade renders candles + entry/exit/SL/TP; `grep -n "timeframe || 'H1'" web-ui/src` → 0.

### C3 — Live monitor page (UX the owner specified)
**Do:**
- **Journal must not flicker or reset scroll.** Render an append-only, `seq`-keyed, **virtualized** list. Merge incoming StepRecords by `seq` (dedupe), **never** `set(slice(-200))` replacing the array (L2). **Stick-to-bottom auto-scroll**: auto-scroll to the newest only when the user is already at/near the bottom; if they've scrolled up, hold position and show a "↓ jump to latest (N new)" affordance. Preserve `scrollTop` across updates.
- Clear `breachBanner` on recovery / non-breach terminal status (L3). Clear the elapsed `setInterval` in `ngOnDestroy` (NEW-7).
- Add a live **open-positions** table and **per-strategy counters**; an optional live **price+entries** mini-chart (reuse `CandleChartComponent` fed from `/api/bars` up to sim-time + entry markers from the journal stream).
- Fix `EquityChartComponent` double `setData`/no-op `forEach`/`showBalance` (L1).
- **Download journal on finish:** a "Download journal (NDJSON)" button on the report page hitting `/journal/export`, plus a rendered run summary.
**Test-first:** two consecutive progress frames (2nd shorter) leave the journal monotonically growing and scroll position stable; a breach then recovery clears the banner.
**Gate:** live-monitor specs green; `grep -n "slice(-200)" web-ui/.../run-monitor` → 0.

### C4 — Frontend/strategy data-correctness
**Do:** H28 (scatter plots `(MAE,MFE)`), H29 (`Gross-Comm-Swap-Net`, no per-term `abs`), H30 (`TrendBreakout` routes SL/TP via `SlTpResolver`/config), strategy nits (MeanReversion `RequiredBarCount` includes `RsiPeriod`; drop unused BollingerBands; EMA wording; TrendBreakout `Stats` allocation), M20 (export CSV emits rows), M21 (cost fields on `RunSummary`/`TradeSummary`), dashboard placeholders wired or hidden.
**Gate:** frontend + strategy tests green; export returns > header.

---

## 6. Part D — Performance (validate on the finished kernel)
> A2/A5 already remove the worst hot-path costs. Part D verifies + adds the rest.
**Do:** confirm zero per-strategy `Dictionary` copies and per-strategy `Sum(Count)`; incremental indicators only for active strategies; O(1) ring buffer; journal batched on a lossless channel sized so the producer rarely blocks (with Q5 indicator-sampling so the journal doesn't dominate); DB indices `TradeResults(RunId)`, `TradeResults(PositionId)`, `EquitySnapshots(RunId,TimestampUtc)`, `Journal(RunId,Seq)` — regenerate migration; `GetRecentBars` passes a read-locked view, not `ToList()` per position per bar.
**Test-first:** a benchmark over a fixed N-bar series asserting allocations/bar + wall-time below a recorded baseline; golden output unchanged.
**Gate:** benchmark ≥ target improvement (record before/after in HANDOVER); golden test unchanged; indices present.

---

## 7. Sequencing
```
A0 golden oracle ─► A1 tape/Run ─► A2 finish kernel ─► A3 journal ─► A4 replay+scenarios ─► A5 indicators
                                                                                                │
                                          ▼ PART A GATE (all golden/determinism/Kill-List green) ▼
        B1 toggles ─┐   B2 venue ─┐   C1 web ─┐   C2 trade chart ─┐   C3 live page ─┐   C4 frontend ─┐
                    └──────────────┴──────────┴──────────────────┴─────────────────┴───────────────┴─► D perf
```
- Part A is strictly ordered. Part B/C/D parallelizable after the Part A gate. B2 should land before C2/C3 rely on truthful bars/positions.

## 8. Definition of Done
- All phase gates green; Unit/Arch/Golden/Simulation/Determinism suites green.
- Kill-List fully executed (greps → 0); **no decision has two authorities**.
- `docs/OPEN-ISSUES.md` reconciled (resolved → `RESOLVED-ISSUES.md`); `docs/reference/SYSTEM-AUDIT.md` + `CODE-MAP.md` + `SYSTEM-MODEL.md §3.2` updated to describe the finished kernel (no more "RiskManager is authoritative / frozen at Empty" notes).
- `HANDOVER.md` records: per-phase deltas, any golden-snapshot changes with rationale, perf before/after, and the cTrader live-verification follow-ups (C1/C2/M1).
- Money math `decimal`; sim-time on all journal/monitor records; reducer provably pure (no wall-clock/`Guid.New`/I/O — add an Arch test asserting the `Engine` project references neither `DateTime.UtcNow` nor `Guid.NewGuid`).

## 9. Risks & mitigations (read before starting)
- **Biggest risk: this is mega-scope and your history here is stalls.** Mitigation = the **A0 golden oracle + Part A gate + Kill-List greps**. If you cannot keep the golden test green while wiring a slice, stop and reconcile before proceeding — do not leave both authorities running.
- **Determinism leaks** (NEW-10) will silently break replay; the Arch purity test + the bit-identical determinism test are the guards.
- **Scope creep into strategy alpha** is out of scope — only the correctness fixes named in B2/C4 touch strategies.
- **Out of scope:** new strategies; cTrader live end-to-end validation (code + contract tests only); tick-level replay (seam defined in A1, implementation deferred).
