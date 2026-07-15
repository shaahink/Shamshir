# Iter-35 (cont.) — FINISH Part A + B: Cut the Kernel Over, Then Complete the Toggles & Venue

**Status:** PLAN WRITTEN (not executed) — 2026-06-19
**Branch:** `iter/35-kernel` (continue on it)
**Owner decision (2026-06-19):** *Finish A and B before any C/D.*
**Audience:** the implementation agent (OpenCode/DeepSeek).
**Read first:** `docs/iterations/iter-35/PLAN.md` (master), `HANDOVER.md` (what shipped), and this doc (what's left + the gates).

---

## 0. Why this plan exists (verified state, not the headline)

A verification pass (2026-06-19) found that the HANDOVER's "**Parts A and B delivered**" overstates the tree. Build is green; Unit 203/4-skip and the 13 fast Simulation tests pass. **But the kernel is a well-tested *sidecar that nothing in production calls*** — the strangler cutover that is the entire point of Part A did not happen. We are in the exact "double-system" state the master plan's anti-stall rule forbids.

**Confirmed by reading the code (trust these):**

| Claim in HANDOVER | Reality |
|---|---|
| A2 kernel authoritative | `grep KernelDriver\|new Kernel(` → only kernel files + tests. `TradingLoop.ProcessBarAsync` still calls `orderDispatcher.DispatchAsync` (`TradingLoop.cs:177`). `RiskManager`, `AccountProcessor:79-115` watchdog, `EngineRunner.SimulateBarExitsAsync` (`EngineRunner.cs:228`), `TradingGovernorService`, `PositionSizer`/`DrawdownScaler`, and the `DropOldest` journal sinks **all still run.** Kill-List ≈ 30% done. |
| Determinism guaranteed | `PositionLifecycle.CreateIntended` mints `Guid.NewGuid()` (`PositionLifecycle.cs:249`) on the kernel accept path → replay is **not** bit-identical once any position opens. `DeterminismTests` feeds a **BarClosed-only** fixture (no orders → no Guid) and compares only `{Seq,EventKind,EffectCount}` — it can't catch NEW-10. `EnginePurityTests` is a reflection **signature** check, not a body scan — it doesn't enforce no-`Guid.NewGuid`/no-`DateTime.UtcNow`. |
| Replay engine (A4) | `ReplayRunner.ReplayEffectExecutor` fills every market order at **price 0** (`so.LimitPrice ?? new Price(0m)`), hardcodes **`Symbol.Parse("EURUSD")`** + 0 lots on closes, stamps `DateTime.MinValue`, and **casts the sink to `ReplaySinkRead`** (the real `SqliteStepRecordSink` would throw). It is a broken replay-only fork — the one thing the master plan forbade. |
| Kernel == old gate | `KernelAcceptanceTests` asserts a **magic `0.20m`**, never loads `golden-snapshot.json`. No full-run kernel-vs-golden equivalence test exists. |
| A3 lossless journal | `ChannelJournalWriter` is correct, but there is **no burst/retry test**, the old `DropOldest` sinks still coexist (so "one journal writer" is false), and `StepRecord.DecisionReason` is always `null`. |

**What is genuinely done and good (don't redo):** the pure `PreTradeGate` + `KernelSizing` + `DrawdownState.GetMaxDrawdownFloor` logic (C3/H1/H2/H3/M7/H5/H6/C14/NEW-3 fixes read correctly); the **live, tested** venue fixes C5/C7/C8/M10; `ChannelJournalWriter`'s lossless mechanics; the committed golden oracle over the *old* engine. **`EffectExecutor` (Host) already exists** (`src/TradingEngine.Host/EffectExecutor.cs`) and is the real I/O boundary the cutover plugs into — do **not** use the fake `ReplayEffectExecutor` for production.

**Bugs/quirks to fix while cutting over:** 3 of 9 B1 toggles are dead (`ProfitTargetEnabled`/`NewsFilterEnabled`/`WeekendFilterEnabled` have no consumer); per-strategy cap uses the global `MaxConcurrentPositions` (`PreTradeGate.cs:80`, faithful port of `RiskManager.cs:115` — give it a real per-strategy limit); C8 still contaminates across days (filters by time-of-day over the whole buffer, not today's session date); B1 has **no API/UI** and B2 is **4 of ~12** venue fixes.

> **The discipline for this whole plan (non-negotiable).** Every cutover phase must keep the golden test green **and** the new full-run equivalence harness (AF0) green. When a corrected behavior changes the golden snapshot, re-baseline it **in the same commit**, with the diff and the reason recorded in HANDOVER. **Delete the imperative twin in the same phase that proves its replacement** — never leave both running into the next phase.

---

## 1. AF0 — The missing oracle: full-run kernel-vs-golden equivalence (build this FIRST)

**Goal:** a real gate that proves the *whole kernel path* reproduces the old engine's behavior on the golden fixture — the thing that makes the cutover safe. Today only a magic-number gate exists.

**Do:**
- Add `KernelGoldenEquivalenceTests` to `tests/.../GoldenReplay/`. Drive the **golden bar fixture** (`GoldenBarFixture.Create()`) through the **real production kernel path** you are about to wire (the `KernelDriver` + the Host `EffectExecutor` against a `FakeVenue`/`SimulatedBrokerAdapter`), producing trades + the `StepRecord` journal + final risk state.
- Compare against the committed `golden-snapshot.json` using `OracleNormalizer` (normalize wall-clock/ids out). Because the kernel's reject-reason vocabulary differs from the old gate, **map or re-baseline** the journal field: first run, write a `golden-kernel-snapshot.json`; assert trades + risk match the *old* `golden-snapshot.json` exactly, and let the journal text be its own kernel baseline.
- This test is **`[Trait("Speed","Fast")]`** and becomes the per-phase gate for AF2–AF7.

**Gate:** `KernelGoldenEquivalenceTests` green; trades + final drawdown/protection identical to the old golden baseline (any difference is a real bug to fix or a reviewed, recorded re-baseline).

---

## 2. Part A — finish the spine (sequential; AF0 + golden green after each)

### AF1 — Determinism made real (NEW-10) + a purity test that actually bites
**Goal:** identical `(Dataset, ConfigSet, Seed)` ⇒ bit-identical journal **including position ids** — the replay contract.
**Do:**
- Thread a deterministic id source into the decision path: `PositionLifecycle.CreateIntended` takes its `PositionId` from `(RunSpec.Seed, seq)` (e.g. a seeded counter or a hash of `seed|seq|orderId`), not `Guid.NewGuid()`. Remove the `DateTime.MinValue` placeholder by carrying sim-time off the event.
- Replace `EnginePurityTests.Engine_has_no_ILogger_no_DateTimeNow` with (or add) a **method-body scan** (Mono.Cecil over the Engine assembly IL, or a source-text Arch test) asserting **zero** `Guid.NewGuid`, `DateTime.UtcNow`, `DateTime.Now`, `DateTimeOffset.UtcNow` in `Kernel.cs`, `PreTradeGate.cs`, `KernelSizing.cs`, `EngineReducer.cs`, `PositionLifecycle.cs`, `GovernorMachine.cs`, `DrawdownReducer.cs`.
- Rewrite `DeterminismTests` so its fixture **opens, fills, and closes positions** (drive `OrderProposed` → `SubmitOrder` → `OrderFilled` → SL/TP exit), and serialize the **full** `StepRecord` (EffectsJson + risk snapshot + ids), not just `{Seq,EventKind,EffectCount}`. Run twice, assert byte-identical.
**Gate:** purity body-scan test green; determinism test green **with positions opening**; `grep "Guid.NewGuid" src/TradingEngine.Engine/PositionLifecycle.cs` → 0.

### AF2 — Cut over the order path (A2a) — *delete the old gate*
**Goal:** the pre-trade decision is the kernel's, in production.
**Do:**
- In `TradingLoop.ProcessBarAsync`, replace the `orderDispatcher.DispatchAsync(...)` call (`TradingLoop.cs:177-185`) with: build an `OrderProposed` (carry `SlPips` + cross-rate-aware `PipValuePerLot` from `PipCalculator`, same source `MapOpenPositionsToProjected` already uses) and run it through `Kernel.Decide`; execute the resulting effects via the **existing Host `EffectExecutor`** (`SubmitOrder`→broker, `RegisterRisk`→risk tracker, `RecordDecisionEvent`→journal). Provide `KernelConfig.ResolveSymbol` (= `symbolRegistry.Get`) and `ProjectOpenPositions` (= the existing `MapOpenPositionsToProjected`).
- Keep `positionTracker.TrackOrder` fed (from the `SubmitOrder` effect / `OrderSubmitted` feedback) so trailing/exit code still sees positions during the remaining cutover.
- **Delete** `OrderDispatcher`'s validate+size responsibility (the gate). If `OrderDispatcher` has nothing left, delete the class; otherwise reduce it to order-id minting only.
- Resolve the double-submit risk: `Kernel.DecideProposed` runs `EngineReducer.Apply(OrderSubmitted)` **and** adds a `SubmitOrder` effect — confirm `PositionLifecycle.Apply(OrderSubmitted)` does **not** also emit a venue submit (assert exactly one `SubmitOrder` reaches the broker per accepted proposal).
**Test-first:** on the golden fixture, the kernel-pathed `ProcessBarAsync` produces the same first order (0.20 lots) and the same accept/reject sequence as the old gate (AF0).
**Gate:** AF0 + golden green; `grep -rn "DispatchAsync" src/TradingEngine.Host` → 0; exactly one `SubmitOrder` per accepted proposal (test).

> **⚠ Parity gap discovered (2026-06-19) — close BEFORE the swap.** The old gate is `RiskManager.ValidateOrder`, which calls `RiskManager.Validate` first (`RiskManager.cs:158`). `Validate` enforces checks `PreTradeGate` does **not** yet replicate: **NEWS_WINDOW** (`newsFilter`), **WEEKEND_RESTRICTION** (`sessionFilter` + `AllowWeekendHolding`), **COMPLIANCE_BLOCK** (`_complianceService.ValidateSignal`), and the **governor** (`ITradingGovernor.Evaluate`, a different model from the kernel `GovernorState`). Swapping in today's `PreTradeGate` would **silently drop** all four — and the golden fixture (mocked news/weekend, null governor) would stay green while production lost protections. **AF2 must first extend `PreTradeGate`** to cover news/weekend/compliance (thread the filters/compliance verdict in via `KernelConfig` or as pre-checks in `KernelOrderGate`) and reconcile the governor (do AF6's `GovernorMachine` mapping here, or keep calling the existing `ITradingGovernor` for the governor verdict and pass `GovernorState.Normal` to the gate). Add equivalence tests for each (news-active rejects; weekend rejects; compliance-block rejects) before deleting `ValidateOrder`.

### AF3 — Cut over bar exits (A2c) — *delete `SimulateBarExitsAsync`*
**Do:** route `BarClosed` through `Kernel.Decide` so `EngineReducer.HandleBarClosed` → `DetectSlTpExit` → `CloseOpenPosition` is the **single** SL/TP authority (it's already written and wired in the reducer). Thread the **exit price** through the close effect so the ledger fills at the stop/target, not last-close (the iter-26 F2 concern). Trailing/breakeven move via `PositionLifecycle` → `ModifyStopLoss`. **Delete** `EngineRunner.SimulateBarExitsAsync` and its call at `EngineRunner.cs:228`.
**Gate:** `grep -rn "SimulateBarExitsAsync" src` → 0; AF0 + golden green; un-skip the iter-26 `F2_sl_tp_fills_at_stop_price` test and make it pass.

### AF4 — Cut over equity/breach (A2b) — *delete the `AccountProcessor` watchdog*
**Do:** route venue `AccountUpdate`s in as `EquityObserved`; `Kernel.DecideEquity` owns the breach watchdog (enter protection / force-close, toggle-gated — already written). **Delete** `AccountProcessor:79-115`. Verify the daily→max→weekly→monthly order + `FlattenAtFraction` semantics match the old watchdog (this is the C5 regression guard: a flat book emits `Equity==Balance`, not 0, and must **not** trip the watchdog).
**Test-first:** force-close emits `Equity==Balance` and does not re-enter protection; a real 6% equity drop enters protection once.
**Gate:** AF0 + golden green; watchdog logic exists in exactly one place (kernel).

### AF5 — Cut over resets (A2d) + finish C4 + NEW-1 — *delete `RiskManager.OnDailyReset` side-effects*
**Do:** route day/week/month rolls as `DayRolled`/`WeekRolled`/`MonthRolled` events through the kernel (`Kernel.DecideReset` clears protection per `ProtectionState.ClearsOn`; reducer resets governor (H7) + drawdown). **Complete the `ProtectionState.ClearsOn` ResetPolicy matrix** (the TODO at `ProtectionState.cs:37-49`) from `PropFirmRuleSet.ProtectionResetPolicy` + the FTMO docs (`docs/reference/`). Add the prop-firm **daily reset time/zone** (NEW-1) to `KernelConfig` and key the day-roll boundary off it (not raw UTC `DayOfYear`). **Delete** `RiskManager.OnDailyReset`'s state mutation. One reset path only.
**Test-first:** MaxDD protection persists then clears per policy; daily-DD clears on the 22:00-Prague boundary, not 00:00 UTC; governor profit-lock clears on day roll.
**Gate:** AF0 + golden green; one reset mechanism; un-skip iter-26 `F7_no_spurious_protection_on_day_roll`.

### AF6 — Governor (A2e) — *delete `TradingGovernorService`*
**Do:** make kernel `GovernorMachine` the only governor; move any logic `TradingGovernorService` has that the machine lacks into `GovernorMachine`; repoint `ITradingGovernor` callers (`TradingLoop.governor?.OnBar`, `EffectExecutor._governor?.OnTradeClosed`) at the kernel state or delete the interface usage. **Delete** `TradingGovernorService`.
**Gate:** one governor impl referenced in `src` (grep); AF0 + golden green.

### AF7 — Sizing (A2f) — *delete `PositionSizer`/`DrawdownScaler`*
**Do:** `KernelSizing` becomes the only sizing authority; delete `TradingEngine.Risk.PositionSizer` + `DrawdownScaler`; repoint any remaining callers. Finish **AntiMartingale (H5)** so it drives the multiplier/steps off the recent-trade streak (`profile.AntiMartingaleMultiplier`/`AntiMartingaleMaxSteps` + `StrategyStats`), not plain PercentRisk. Give the **per-strategy cap** a real limit (don't reuse global `MaxConcurrentPositions`).
**Gate:** `grep -rn "PositionSizer\|DrawdownScaler" src` → only `KernelSizing`; AF0 + golden green.

### AF8 — One journal (A3 finish) — *delete the `DropOldest` sinks*
**Do:** make `StepRecord` the single journal in production (the `KernelDriver` already emits one per event). Thread the **gate verdict reason** into `StepRecord.DecisionReason` (today `null` — `KernelDriver.cs:139`; surface it from `PreTradeGate`/`RecordDecisionEvent`). **Demote `PipelineEventWriter` + `BarEvaluationHandler` to projections over `StepRecords`, or delete them** (both use `DropOldest`). Give the NDJSON export **stable polymorphic JSON** (a shared `JsonSerializerOptions` with a typed `EngineEffect` discriminator) — part of the determinism contract. Fix the normalizer nits in passing: M11 (`OrderCancelled`→`ENTRY_EXPIRED` vs `CANCELLED` by reason), M12 (`TRAIL`/`BREAKEVEN`/`PARTIAL`).
**Test-first:** the **lossless burst test** the plan demanded — write N+1 `StepRecord`s into a capacity-N writer → all N+1 persist; make the sink throw once → the batch is retried, not lost (`ChannelJournalWriter.DroppedBatches == 0`).
**Gate:** exactly one journal writer in `src` (grep); `grep -rn "DropOldest" src/...Events src/...Persistence src/...Caching` → 0 (market-data streams excepted, documented); burst test green.

### AF9 — Real replay engine (A4 finish) — *kill the fork*
**Do:** rewrite `ReplayRunner` so it drives the kernel through the **same effect executor and the same fill source as a fresh backtest** — fills come from the **tape's bar prices** at the right sim-time, with the order's real symbol/lots, **not** `EURUSD`/0/`MinValue`. Remove the `ReplaySinkRead` cast; it must persist through the real `SqliteStepRecordSink`. Wire "Re-run with a different ConfigSet over the same `DatasetId`" → creates a new `Run` row (same `DatasetId`, new `ConfigSetId`), executes, and is saved/listed as a normal backtest.
**Test-first:** the **bit-identical determinism test over a real position-opening replay** (re-run `(DatasetId, ConfigSetId, Seed)`, assert byte-identical journal **and** trades); "re-run with a different risk profile" produces a new listed run over the same dataset hash.
**Gate:** determinism test green on a run that opens/closes positions; no replay-only effect executor remains (the fake `ReplayEffectExecutor` is deleted); replay persists to SQLite.

### AF10 — A1 finish (the untested seam)
**Do:** add the **dataset-hash stability** test (same bars → same hash; one changed bar → different hash) and a `ConfigSet` round-trip test (a `Run` reloads its `DatasetRef` + `ConfigSet`).
**Gate:** both tests green.

### AF11 — A5 indicators (not started)
**Do:** make indicator state **incremental** (update per new bar; no full recompute over the buffer — `IndicatorSnapshotService`); compute the **union** of indicators for the **active** strategies once per `(symbol, tf, bar)` and expose a shared snapshot; `BuildStrategyIndicatorValues` becomes a projection (no per-strategy `Dictionary` copy); honor `CancellationToken` (M9, `IndicatorSnapshotService.cs`). Replace `TradingLoop.cs:59 list.RemoveAt(0)` with an **O(1) ring buffer** sized ≥ max `RequiredBarCount` (H8/H9). One canonical indicator-key scheme.
**Test-first:** a strategy requesting X,Y gets correct values with **one** computation/bar (counting fake); a >500-bar warm-up strategy still evaluates; every active strategy's requested keys resolve (the iter-29 prefix-mismatch guard).
**Gate:** `grep -n "RemoveAt(0)" src/TradingEngine.Host` → 0; key-resolution test green; AF0 + golden green.

### AF12 — Tighten the contract + final Kill-List
**Do:** make `EngineState.Protection`/`Account` **required** positional params now that the imperative state is gone (A2h); update the positional `new EngineState(...)` test sites. Update `docs/reference/SYSTEM-AUDIT.md` + `CODE-MAP.md` + `SYSTEM-MODEL.md §3.2` to describe the finished kernel (drop the "RiskManager is authoritative / frozen at Empty" notes).
**▶ PART A GATE (must be green before Part B closes):** golden + AF0 equivalence + determinism (positions open) + burst tests green; Kill-List greps **all → 0** (`RiskGate`, `UNWIRED`, `SimulateBarExitsAsync`, `DispatchAsync` in Host, `DropOldest` in journal sinks, `PositionSizer`/`DrawdownScaler`, two governors); the engine runs end-to-end through the kernel with the `StepRecord` journal.

---

## 3. Part B — finish toggles + venue (after the Part A gate)

### BF1 — Toggles "without faff" (the feature, not just flags)
**Do:** `GET/PUT /api/prop-firms` + `/api/risk-profiles` (+ governor) over the existing `UpsertAsync`; fix **M18** (drop the stale `GovernorOptions` singleton — read DB) + **M19** (no bare `catch{}`). Angular **Settings page** with real toggle switches + numeric inputs bound to the 9 `ProtectionToggles` + limits; DB is source of truth. `POST /api/config/export` writes DB config back to `config/**.json` (one-way, on demand). **Wire the 3 dead toggles**: add the profit-target gate (and fix **M6** — use equity, not balance) behind `ProfitTargetEnabled`; add news/weekend checks behind their flags, or remove those flags if out of scope — no dead toggles.
**Test-first:** `weeklyDd:false`→no violation at threshold, `true`→violation; `forceCloseOnBreach:false`→protection-no-flatten, `true`→flatten; **`PUT` then a new run resolves the mutated `ConstraintSet`** (assert via the journal risk snapshot — now possible because AF2–AF4 put the kernel in the run path).
**Gate:** toggle tests green; a run after `PUT` reflects new limits in its `StepRecord` risk snapshot.

### BF2 — Venue money/fill correctness (the remaining ~8)
**Do:** **C6** (partial close: `ComputeCosts` + balance update + cost-stamped exec + `AccountUpdate` — `SimulatedBrokerAdapter.ClosePartialPositionAsync:171-189`); **H13** (`FilledLots>0` on full close, `BacktestReplayAdapter:267`); **H14/H15** (align fill ts/price — `BacktestReplayAdapter:177,227`); **H16** (directional bid/ask for floating PnL, not mid — `:346`); **H11** (synthetic close uses last price, not 0 — `CTraderBrokerAdapter:448`); **C8 residual** (filter the session range to **today's** date, not every day's time-of-day window — `SessionBreakoutStrategy.cs:56-62`).
**Test-first:** partial close moves balance by net PnL with costs stamped; replay full close `FilledLots>0`; SessionBreakout range = one day's window on a 3-day fixture.
**Gate:** venue/simulation tests green; `grep -n "FilledLots = 0" src/.../BacktestReplayAdapter.cs` → 0.

### BF3 — cTrader limit/cancel (code + contract test only; live = owner follow-up)
**Do:** **C1** (cBot honors `orderType`/`limitPrice`/`expiryBars`/`maxSlippagePips` — `TradingEngineCBot.cs:298`), **C2** (`cancel_order` handler — `:236`), **M1** (partial-close reads commission/swap **after** close). Prove with a **`FakeTransport` contract test**; do **not** attempt live cTrader verification (no cTrader in sandbox — see [[project-test-harness-gotchas]]).
**Gate:** FakeTransport contract test green; the three fixes recorded as **owner live-verification follow-ups** in HANDOVER.

---

## 4. Sequencing
```
AF0 equivalence oracle ─► AF1 determinism ─► AF2 order ─► AF3 exits ─► AF4 equity ─► AF5 resets
        ─► AF6 governor ─► AF7 sizing ─► AF8 one-journal ─► AF9 real-replay ─► AF10 A1 ─► AF11 indicators ─► AF12 tighten
                                   ▼ PART A GATE (all greens + Kill-List → 0) ▼
                       BF1 toggles+UI ──┐   BF2 venue ──┐   BF3 cTrader ──┐
                                        └───────────────┴────────────────┴──► (then C/D in the next plan)
```
- AF0 → AF9 strictly ordered; AF10/AF11 may overlap AF8. BF1 depends on AF2–AF4 (kernel in run path). BF2/BF3 are independent and can start once the Part A gate is green.

## 5. Definition of Done
- The **real Part A gate** above is green; Unit/Arch/Golden/Determinism/Simulation suites green.
- **Kill-List fully executed** (every grep → 0); **no decision has two authorities**; the kernel is the production path for orders, exits, equity/breach, resets, governor, sizing, and the journal.
- Replay is **real and bit-identical** on a position-opening run and persists to SQLite; determinism + purity tests actually exercise the id/clock paths.
- B1 toggles are editable from the UI with DB as source of truth and **no dead flags**; B2 venue fixes complete (sim venue); cTrader C1/C2/M1 are code+contract-tested with live verification flagged.
- `docs/OPEN-ISSUES.md` reconciled (resolved → `RESOLVED-ISSUES.md`); SYSTEM-AUDIT/CODE-MAP/SYSTEM-MODEL updated; HANDOVER records per-phase deltas, every golden re-baseline + reason, and the cTrader follow-ups.

## 6. Risks
- **This is the cutover the last round deferred — it is the hard part.** The guard is AF0 (full-run equivalence) + golden-green-after-each-phase + delete-the-twin-in-the-same-phase. If a phase can't stay green, **stop and reconcile** before proceeding — do not advance with both authorities live.
- **Re-baselining the golden journal** is expected (kernel reject vocabulary differs). That is allowed **only** with a reviewed diff and a recorded reason; trades + risk must match the old baseline unless a real bug is being fixed.
- **Out of scope here:** all of Part C/D (next plan), new strategies, live cTrader end-to-end.
