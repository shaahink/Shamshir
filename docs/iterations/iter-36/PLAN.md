# Iter-36 — The Kernel Cutover (do it for real, with real gates): One Engine, One Journal, Real Replay

**Status:** PLAN WRITTEN (not executed) — 2026-06-20 (rev-3; this is the cutover the last three rounds deferred)
**Branch base:** `iter/35-kernel-finish-ab` → cut `iter/36-kernel-cutover`
**Audience:** the implementation agent (OpenCode/DeepSeek).
**Owner direction (2026-06-20):** *Get the kernel piece actually done — installed, consistent, no half-state — so the frontend iteration (iter-37) builds on a real foundation. Then mop up the remaining of what the kernel was meant to fix. Within budget.*

> **Why this stalled before, and the one thing that changes now.** The previous rounds did step 1–2 of "shadow-then-replace" (extract the pure decision *functions* and call them from the old imperative loop) but **never replaced the driver**. It *looked* done because the safety gates were hollow: the equivalence test asserted a magic `0.20m` instead of the golden run, the determinism test fed a **BarClosed-only** fixture (so no ids/positions to diverge), and the purity test checked **method signatures**, not bodies. **K0 builds the real gates first.** After that, every phase deletes its imperative twin *in the same commit that proves the kernel replacement on those real gates* — a phase cannot close while both run. That single rule is the anti-stall mechanism.

> **Budget fence (hold the line).** Scope = the **backtest path only** (the path iter-37 testing needs). Live cTrader keeps working through the same kernel for free (the driver is mode-agnostic) but is **not** the validation target here. **Out: performance, tick-level replay, new strategies, all UI (→ iter-37), and any bug not on the cutover's critical path.** "Remaining" (K7) is correctness-first and only as budget allows. If a phase balloons, cut K7, not the gates.

---

## 0. Verified ground truth (static analysis, 2026-06-20) — the scaffolding already exists

The cutover is **assembly of existing, tested parts**, not a green-field build:

- **`KernelDriver` (`Engine/Kernel/KernelDriver.cs`) is real and correct** — `tape → queue → EngineReducer.Apply (pure) → (state', effects[]) → one StepRecord per event + IEffectExecutor`, draining feedback events in order. Its own doc: *"Both backtest and live run through THIS driver… There is no replay-only fork."* It is a **skeleton with three named seams**: `_evaluatorView` (strategy verdicts + `OrderProposed`), `DecisionReason` (gate reason, currently `null`), and a shared `JsonSerializerOptions`.
- **The Host `EffectExecutor` (`Host/EffectExecutor.cs`) is COMPLETE** — handles all 8 effects (`SubmitOrder`, `ModifyStopLoss/TakeProfit`, `CloseOpenPosition`, `RecordDecisionEvent`, `PublishTradeClosed`, `RegisterRisk`, `DeregisterRisk`) against the **real** `IBrokerAdapter`, with correct cost/PnL/MAE/MFE/R math in `HandlePublishTradeClosed`. **This is the I/O boundary the cutover plugs into.** It currently writes `RecordDecisionEvent` to the old `IDecisionJournal` (repointable in K5).
- **The reducer branches are wired** — `EngineReducer.Apply` handles `OrderProposed/Submitted/Filled/PartiallyFilled/Rejected/Cancelled`, `BarClosed`, `EquityObserved`, `Day/Week/MonthRolled`, `CloseRequested`, `ForceCloseAllRequested` (the "UNWIRED" comments are gone). `EngineState` already carries `{Positions, Governor, Drawdown, OpenPositionCount, Protection, Account}`. `PositionId == OrderId` (determinism seam done; no `Guid.NewGuid` in Engine).
- **`BarTape` (`Infrastructure/BarTape.cs`)** already materializes DB bars → `BarClosed` events (the backtest `IEventTape`).
- **What's genuinely missing (the cutover work):**
  1. an **evaluator stage**: per bar, run indicators + regime + strategy bank + signal gate and emit `OrderProposed` events (+ per-strategy verdicts) into the queue — i.e., port `TradingLoop`'s evaluation into an event producer feeding `_evaluatorView`/the queue;
  2. a **venue-feedback bridge**: broker fills + account updates → `OrderFilled` / `EquityObserved` events back onto the queue (today they arrive via `MarketEventSource`/`AccountStream`, outside the kernel);
  3. **`EngineState` as the single authority** for positions/drawdown/protection/governor (today `RiskManager`/`AccountProcessor`/`PositionTracker` hold imperative copies);
  4. **one journal** (StepRecord) replacing `PipelineEvents` + `BarEvaluations` + `DecisionRecord`;
  5. **real replay** = the same `KernelDriver` + `EffectExecutor` + `BacktestReplayAdapter` (delete the fake `ReplayEffectExecutor`).
- **The realistic venue is `BacktestReplayAdapter`** (DB bars, real spread/commission/swap, SL/TP-fill-at-stop). The cutover keeps it as the venue the EffectExecutor submits to — replay/duplicate run through it, **never** a fake CSV/price-0 shortcut.

---

## 1. Discipline (the gates ARE the plan)
- **Golden + the K0 equivalence + determinism + purity gates stay green after every commit.** A golden re-baseline is allowed only with a reviewed diff + a recorded reason in HANDOVER, and only when trades+risk still match (journal vocabulary may differ).
- **Delete the twin in the same phase that proves its replacement.** Each cutover phase lists a `grep … → 0` gate. If it isn't 0, the phase isn't done.
- **Failing-test-first.** Reducer/evaluator tests are pure + fast; the equivalence/determinism tests drive the **real** backtest harness.
- **Reducer stays pure** (no wall-clock/`Guid.NewGuid`/I/O); all nondeterminism lives in effects or is carried on the event. The K0 purity body-scan enforces it.
- Use the skills: `shamshir-kernel` (model + cutover pattern), `shamshir-ui`/`run-shamshir` for the real-run smoke after each flip.

---

## 2. The cutover (sequential; K0 first, then each phase deletes its twin)

### K0 — Build the REAL gates (the missing foundation; nothing else starts until these bite)
**Goal:** gates that actually fail if the kernel path diverges — so the cutover is provably behavior-preserving.
**Do:**
- **`KernelGoldenEquivalenceTests`** (`[Trait("Speed","Fast")]`): drive the **golden bar fixture** (`GoldenBarFixture.Create()`, a position-opening fixture) through the **real** path you're about to wire — `BarTape → evaluator → KernelDriver → Host EffectExecutor → BacktestReplayAdapter (or a FakeVenue with identical fill rules)` — producing trades + the `StepRecord` journal + final `EngineState` risk. Assert **trades + final drawdown/protection match the committed `golden-snapshot.json` exactly**; let the journal text be its own kernel baseline (`golden-kernel-snapshot.json`). This is the per-phase gate for K1–K6.
- **Real determinism test:** replace the BarClosed-only fixture with one that **opens, fills, and closes positions**; run the real path twice; assert **byte-identical** full `StepRecord` stream + trades (normalize only documented wall-clock telemetry like `ReceivedAtUtc`).
- **Real purity test:** body-scan (Mono.Cecil over the Engine IL, or source-text) asserting **zero** `Guid.NewGuid`/`DateTime.UtcNow`/`DateTime.Now`/`DateTimeOffset.UtcNow` in `Kernel.cs`, `EngineReducer.cs`, `PreTradeGate.cs`, `KernelSizing.cs`, `PositionLifecycle.cs`, `GovernorMachine.cs`, `DrawdownReducer.cs`.
**Gate:** all three green; the equivalence test exercises a run that opens+closes positions (not an empty tape). **These never go red again.**

> **STATUS — K0 DELIVERED 2026-06-20 (commit on `iter/36-kernel-cutover`).** Done by the owner before agent handoff:
> - **Purity body-scan** already existed (`EnginePurityTests.Engine_has_no_GuidNewGuid_or_DateTimeUtcNow_in_source`) — **hardened** to fail loudly if the source dir can't be resolved and to assert it actually covers the 7 kernel-core files (so it can't silently scan nothing).
> - **Determinism test strengthened** — `DeterminismTests.SerializeJournalFull` now compares the **full** StepRecord (real effect payloads by runtime type — ids/prices/lots — **plus** the risk snapshot), not just `{seq, kind, count}`. The position-opening fixture already existed; it now actually bites on id/price/risk divergence.
> - **Equivalence de-magic'd** — `KernelAcceptanceTests` now loads `golden-snapshot.json` (via new `GoldenSnapshotLoader`) and asserts the first order's lots+direction **against the baseline**, replacing the hollow magic `0.20m`.
> - **🔴→🟢 Golden oracle was RED and is now fixed** — the harness pinned its sim clock to **wall-clock** (`EngineHarnessBuilder.cs:57: clock.UtcNow = DateTime.UtcNow`), so the gate's `IsWeekend(clock.UtcNow)` rejected **every** order with `WEEKEND_RESTRICTION` on Sat/Sun → zero trades → golden red on weekends only (date-dependent, flaky). Anchored the clock to the fixture's sim-time; golden + `KernelOrderGateEquivalenceTests` now green and date-independent. **No snapshot re-baseline** — the fix reproduces the existing committed baseline. (Latent prod note: both `RiskManager.cs:152` and `KernelOrderGate.cs:116` read `clock.UtcNow` for weekend/news — the cutover must carry sim-time on the event so a real backtest isn't date-dependent either. Verify in K1/K2.)
> - **Deferred to K3 (breadcrumb in place):** the FULL-run trade+risk equivalence-to-golden (`KernelAcceptanceTests.KernelFullRun_MatchesGolden_TradesAndRisk`, `[Skip]`) — it needs the kernel backtest loop (evaluator + real fills) that K1–K3 build. Un-skip it at K3.
> - **Verified:** Architecture 4/4, GoldenReplay+Determinism+KernelAcceptance 6 pass/1 skip, fast Simulation 68 pass / 2 fail (both **pre-existing**: `ReplayTestHarness` missing `EntryPlanner` DI reg; `NetMQEngine` smoke — neither touched here).
>
> **Agent starts at K1.**

### K1 — Evaluator stage: per-bar evaluation → `OrderProposed` events
**Goal:** the strategy evaluation that today lives imperatively in `TradingLoop.ProcessBarAsync` becomes an **event producer** feeding the kernel queue.
**Do:** build an evaluator that, per `BarClosed`, runs `IndicatorSnapshotService` (incremental ok) + `IRegimeDetector` + `IStrategyBank.GetActive` + `ISignalGate`, and for each firing strategy enqueues an `OrderProposed` (carrying `SlPips` + cross-rate-aware `PipValuePerLot` exactly as `MapOpenPositionsToProjected` does today) and records a per-strategy `StrategyVerdict` (signal/none + reason + sampled indicators) for `_evaluatorView`. The kernel gate (`PreTradeGate`, already correct) decides accept→`SubmitOrder` or reject→`RecordDecisionEvent`. **Port** `KernelOrderGate.ComputeVerdicts` (news/weekend/compliance/governor) as the gate's `ExternalVerdicts` so no protection is silently dropped.
**Test-first:** on the golden fixture, the evaluator emits the **same first `OrderProposed`** and the gate produces the **same 0.20-lot accept + the same accept/reject sequence** as the old `TradingLoop`+`KernelOrderGate` (assert against K0).
**Gate:** K0 equivalence green via the evaluator; one `SubmitOrder` per accepted proposal (no double-submit).

### K2 — Venue-feedback bridge: fills + account → events
**Goal:** the venue's `OrderFilled`/`PartiallyFilled`/`Cancelled` and `AccountUpdate` re-enter the kernel as `EngineEvent`s, so `EngineState` (positions, drawdown, protection) is evolved by the reducer — not by `PositionTracker`/`AccountProcessor`.
**Do:** adapt `MarketEventSource` execution drains + `AccountStream` reads into enqueued `OrderFilled` / `EquityObserved` events (carry sim-time + price off the venue event). `EquityObserved` drives `DrawdownReducer` + breach (force-close/protection, toggle-gated — already in the reducer). Day/week/month rolls become `DayRolled/WeekRolled/MonthRolled` events keyed off the prop-firm reset time/zone (NEW-1), not raw UTC `DayOfYear`.
**Test-first:** a flat book emits `Equity==Balance` (not 0) and does **not** trip the watchdog (the C5 regression guard); a 6% equity drop enters protection exactly once; SL/TP fill closes a position via the reducer.
**Gate:** K0 + determinism green; breach/exit logic runs only inside the kernel (grep: no second watchdog).

### K3 — Stand up the kernel-driven backtest loop (proves equivalence end-to-end)
**Goal:** a single `RunAsync` that drives `BarTape → (evaluator+feedback) → KernelDriver → EffectExecutor → BacktestReplayAdapter`, with `EngineState` as the authority, reproducing the golden run.
**Do:** assemble the loop (the parts from K1/K2 + the existing driver/executor). Thread `EngineState` forward; capture risk via `RiskSnapshots.Capture`. Keep the old `EngineRunner.RunBacktestLoopAsync` present but **unused-by-default** only for the length of this phase.
**Test-first:** the full golden run through the new loop equals `golden-snapshot.json` (trades + risk); determinism green.
**Gate:** K0 equivalence + determinism green **through the new loop**.

### K4 — Flip the default + DELETE the imperative twins (the actual cutover)
**Goal:** the kernel loop is the only backtest path; the imperative engine is gone.
**Do (one phase, twins deleted in-commit):** point `BacktestOrchestrator.RunEngineReplayAsync` at the kernel loop. **Delete:** `EngineRunner.RunBacktestLoopAsync` body + `SimulateBarExitsAsync`; `AccountProcessor` (watchdog + reset side-effects); `KernelOrderGate` (its job is now the evaluator+gate) and `OrderDispatcher`; `RiskManager`'s imperative drawdown/protection/governor **state** (it may remain only as the thin `RegisterPosition`/`DeregisterPosition` risk-amount tracker the `RegisterRisk` effect uses, with no authoritative DD/protection state). `PositionTracker` open-position state → `EngineState.Positions` (keep only what trailing/UI reads, sourced from state).
**Gate (all → 0):** `grep -rn "RunBacktestLoopAsync\|SimulateBarExitsAsync\|AccountProcessor\|KernelOrderGate\|OrderDispatcher" src` → 0 (or, for `RiskManager`, no authoritative DD/protection mutation outside the reducer); golden + K0 + determinism green through the **only** path.

### K5 — One journal: `StepRecord` is the single sink
**Goal:** the owner's "one journal" — append-only, lossless, structured, the single source for report + NDJSON + live tail.
**Do:** persist `KernelDriver`'s `StepRecord`s via the **real** `SqliteStepRecordSink` (lossless `ChannelJournalWriter`: `Wait`+retry+clear-after-save). Thread the gate **`DecisionReason`** (today `null`) from `PreTradeGate`. Fold **per-strategy verdicts** (from K1) into the `BarClosed` StepRecord so the per-bar "why" is in the one journal — **`BarEvaluations` is subsumed**. Add structured fields the UI needs (`orderId`, `violations: string[]`, `commission/swap/gross/net`, `lots`, `price`) onto the record/projection. **Repoint** `EffectExecutor.RecordDecisionEvent` to the StepRecord sink. **Delete** `PipelineEventWriter` + `BarEvaluationHandler` (and their `DropOldest` channels); repoint `GET /api/runs/{id}/journal` to read `StepRecords` (SQL-paged by `seq`, no `.AsEnumerable()`). Stable polymorphic JSON for the NDJSON export.
**Test-first:** lossless **burst test** (N+1 into capacity-N → all persist; repo throws once → batch retried, `DroppedBatches==0`); a REJECTED record exposes named `violations`; a CLOSE exposes non-null costs; an ORDER+FILL share an `orderId`.
**Gate:** exactly one journal writer in `src` (grep); `grep -rn "DropOldest" src/...Events src/...Persistence` → 0 (market-data streams excepted, documented); burst test green.

### K6 — Real replay + duplicate-with-a-different-strategy (same path, no fake)
**Goal:** "re-run this backtest" / "duplicate with a different strategy" = the **same** kernel loop over the same tape with the same-or-new `ConfigSet`. Bit-identical when nothing changes.
**Do:** rewrite `ReplayRunner` to drive the kernel loop through the **real** `EffectExecutor` + `BacktestReplayAdapter` (fills from the tape's bar prices at sim-time, the order's real symbol/lots). **Delete** `ReplayEffectExecutor` + `ReplaySinkRead` (the price-0/`EURUSD`/`MinValue`/can't-persist fake). Wire **Run = (Dataset, ConfigSet, Seed)** onto the production run (the A1 entities exist — populate `DatasetId`=content-hash of the bar set, `ConfigSetId`=hash of resolved effective config, `Seed`). `POST /api/runs/{id}/duplicate` (optional `StrategyIds`/`RiskProfileId`/`StrategyOverrides`) → a new run over the **same `DatasetId`**, new `ConfigSetId`, `ParentRunId` set, executed via the same kernel loop.
**Test-first:** re-running `(DatasetId, ConfigSetId, Seed)` reproduces the prior run's journal+trades **byte-identically**; a duplicate with a different `RiskProfileId` yields a new run, same `DatasetId`, different `ConfigSetId`, run through `BacktestReplayAdapter` (assert the venue, not a fake).
**Gate:** determinism green on a **position-opening** replay; `grep -rn "ReplayEffectExecutor\|ReplaySinkRead" src` → 0; duplicate endpoint test green.

### K7 — The remaining the kernel was meant to fix (correctness-first, as budget allows)
**Goal:** close the gap between "kernel is authoritative going forward" and "the old buggy code is gone."
**Do:**
- **Verify the shadowed bugs are deleted, not just bypassed:** the old `RiskManager.cs:186` trailing-floor bug, `OnDailyReset` C4 bug, etc. — confirm those code paths no longer exist/execute after K4 (the kernel owns the logic now). `OPEN-ISSUES.md` currently says "old RiskManager still has the bug but kernel is authoritative" for C3/C4 — after K4 those notes must become "removed."
- **Backtest/sim venue correctness still open** (do the ones that affect the realistic backtest): C6 (partial-close costs — done?), H13 (`FilledLots>0` on full close), H14/H15 (fill ts/price alignment), H16 (directional bid/ask for floating). **Flag** cTrader-live-only ones (C1/C2/M1/H11) as **owner live-verification follow-ups** (no cTrader in sandbox — see `project-test-harness-gotchas`).
- Reconcile `docs/OPEN-ISSUES.md` (resolved → `RESOLVED-ISSUES.md`), update `SYSTEM-AUDIT.md`/`CODE-MAP.md`/`SYSTEM-MODEL.md §3.2` to describe the **finished** kernel (drop every "RiskManager is authoritative / frozen at Empty" note).
**Gate:** named backtest-path bugs fixed with tests; doc reconciliation done; cTrader-live items explicitly listed as follow-ups.

---

## 3. Sequencing
```
K0 real-gates ─► K1 evaluator ─► K2 feedback-bridge ─► K3 kernel-loop ─► K4 FLIP+delete-twins ─► K5 one-journal ─► K6 real-replay ─► K7 remaining
                 └────────────── K0 equivalence + determinism + purity green after EVERY step ──────────────┘
```
Strictly ordered. K4 is the irreversible flip — it must not start until K3 proves equivalence. K7 is the only phase that may be trimmed for budget.

## 4. Definition of Done
- The backtest runs **through `KernelDriver` only**; `EngineState` is the single authority for positions/drawdown/protection/governor; **all imperative twins deleted** (every K4 grep → 0). No decision has two authorities.
- **One lossless `StepRecord` journal** is the single per-run record (report + NDJSON + per-bar "why" all read it); the old sinks are gone.
- **Replay is real and bit-identical** on a position-opening run, through `BacktestReplayAdapter`; "duplicate with a different strategy" creates a new listed run over the same dataset hash; the fake executor is deleted.
- K0 equivalence + real determinism (positions open) + body-scan purity green; golden green or re-baselined-with-reason.
- `OPEN-ISSUES.md`/audit reconciled; HANDOVER records per-phase deltas, every golden re-baseline + reason, and the cTrader live-verification follow-ups.
- **Then iter-37 (frontend) builds on this** — the journal/replay/duplicate it surfaces are real.

## 5. Stall signals — STOP and reconcile if any occur
- You're running both the old loop and the kernel loop into the **next** phase (delete the twin now, or revert the phase).
- A gate is "green" but you can't point to the line where the old authority was deleted (the gate is hollow — fix the gate).
- You're tempted to re-baseline the golden snapshot to make trades match (that's a real divergence — find the bug, don't move the goalposts).
- A phase is sprawling past budget — trim **K7**, never **K0** or the deletes.

## 6. Risks
- **Biggest risk is still scope + stalls.** Mitigation = K0's real gates + delete-twin-in-commit + the budget fence. This plan is one focused doc (not four) precisely so the gates can't get lost.
- **Determinism leaks** in persisted timestamps break replay — the K0 determinism test + the documented normalization list are the guard.
- **EngineState authority migration is the subtle part** — positions/drawdown must be evolved *only* by the reducer after K4; any lingering imperative mutation reintroduces the two-authority bug. The equivalence test catches divergence; the K4 greps catch the code.
- **Out of scope:** UI (iter-37), perf/indicators, tick replay, new strategies, live cTrader e2e.
