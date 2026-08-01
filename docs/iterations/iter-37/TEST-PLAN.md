# Iter-37 — Pressure & Reality Test Plan (prove the kernel does what we claim)

**Status:** PLAN WRITTEN — 2026-06-20 (companion to `iter-37/PLAN.md`; written after a static audit of the
iter-36 cutover)
**Branch base:** `iter/36-kernel-cutover` → cut `iter/37-frontend-finish` (this plan's G0/E/B prerequisites
land first, then the frontend phases consume real data)
**Audience:** the implementation agent (OpenCode/DeepSeek).

> **Why this plan exists.** iter-36 flipped the kernel on, with gates proven on the **single-day,
> single-symbol, zero-cost golden fixture**. That fixture is too small to exercise the things the owner now
> wants to test: multi-day FTMO pressure, governor/drawdown resets, the new journal as the single source of
> truth, per-bar equity, and the chart. The static audit (see `OPEN-ISSUES.md` → "iter-36 cutover
> follow-ups", **K-GAP-1..6**) found three correctness gaps that only a *bigger* test would catch — most
> importantly **K-GAP-1: the production loop never rolls the day, so multi-day runs never reset** (silently
> reintroducing C4 + H7). This plan builds the pressure/reality suites **failing-test-first** so those gaps
> go red, then the fixes turn them green.

---

## 0. Discipline & ground rules

- **Failing-test-first.** Each suite below names tests that should be RED on the current `iter/36` HEAD
  (because the code gap exists) and GREEN after the linked K-GAP fix. Write the test, watch it fail for the
  *expected* reason, fix the code, watch it pass.
- **Fast where possible.** Reducer/governor/drawdown/journal-shape tests are pure → `[Trait("Speed","Fast")]`
  in `TradingEngine.Tests.Unit`. End-to-end "real backtest" tests go in `TradingEngine.Tests.Simulation`
  (reuse `KernelLoopHarness` / `KernelLoopHarness`-style wiring from `GoldenReplay/`). Never add an IHost
  test where a pure reducer test proves the same thing (the `ReplayTestHarness` ~60s floor is the last
  resort — see `project-test-harness-gotchas`).
- **Sim-time only.** Every fixture stamps events with sim-time; no test may depend on wall-clock (the K0
  weekend-flakiness lesson). Multi-day fixtures advance sim-time across the prop-firm reset boundary.
- **Don't re-baseline golden to make a test pass.** A divergence is a bug (plan §5 stall signal). New
  fixtures get their **own** committed snapshots (`*-snapshot.json`), kept separate from `golden-snapshot.json`.
- **One assertion idea per test**, named for the behaviour (`Governor_DailyReset_ClearsProfitLock`, not
  `Test7`). The histogram of test names IS the spec.

---

## Phase G0 (CODE PREREQ) — Emit the day/week/month roll in the kernel loop (fixes K-GAP-1)

**This is a code change, not just a test — and it gates the whole G/F suite.** Without it every multi-day
governor/DD/FTMO test below is meaningless (the kernel never resets).

**Do:** in `KernelBacktestLoop.ProcessBarAsync`, before evaluating the bar, detect a sim-time boundary
crossing of `bar.OpenTimeUtc` against the prop-firm reset clock and enqueue the roll event(s):
- daily: crossed `PropFirmRuleSet.DailyResetTime` in `DailyResetZone` since the last processed bar → `DayRolled`;
- weekly: crossed the configured week boundary → `WeekRolled`;
- monthly: crossed the month boundary → `MonthRolled`.
Carry the boundary sim-time on the event. The reducer already handles all three
(`HandleDayRolled/WeekRolled/MonthRolled`) + `Kernel.DecideReset` applies the protection-exit policy.
Thread the reset clock from `KernelConfig` (it already carries `Constraints`/`ruleSet`).

**Test-first (RED → GREEN):**
- `KernelLoop_MultiDay_EmitsOneDayRolledPerBoundary` — a 3-day H1 fixture produces exactly 2 `DayRolled`
  StepRecords at the reset sim-time (not the UTC midnight unless that's the configured reset).
- `KernelLoop_MultiDay_DailyDrawdownRebasesEachDay` — daily DD on day 2 is measured from day-2's start
  equity, not the run's initial balance (assert `Risk.DailyDrawdown` resets to ~0 at each `DayRolled`).

**Gate:** `grep -rn "new DayRolled" src` → ≥1; both tests green; golden (single-day) **unchanged** (no roll
fires inside one day).

> **STATUS — G0 BACKBONE DELIVERED (skeleton + tests) 2026-06-20, pending build/suite verify by the agent.**
> The sensitive parts were written directly (the boundary math + the reducer re-base are the easy-to-get-
> silently-wrong bits); the agent builds, runs the full suite, and re-baselines any multi-day fixture if
> needed. Changes:
> - **`src/TradingEngine.Engine/ResetClock.cs`** (NEW, pure) — `ResetClock.Crossed(prev, cur, ResetConfig)`
>   → `RollFlags(Day,Week,Month)` for boundaries crossed in `(prev, cur]`; inclusive boundary; weekend gaps
>   collapse to one crossing per kind; UTC-deterministic, unknown-zone → UTC fallback. `+ ResetConfig`
>   (`FromRuleSet(timeStr, tz)`).
> - **`src/TradingEngine.Engine/EngineReducer.cs`** (FIX) — `HandleDayRolled/WeekRolled/MonthRolled` re-base
>   to `state.Account.Equity` (the authoritative current equity, available post-cutover) instead of the
>   stale previous start equity. (A second latent bug behind K-GAP-1 — the old code re-based a period to its
>   own old start, so it never moved.)
> - **`src/TradingEngine.Host/KernelBacktestLoop.cs`** (WIRE) — optional `ResetConfig? resetConfig` ctor
>   param + `_prevBarSimUtc`; `ProcessBarAsync` emits Month/Week/Day rolls (after the prior-feedback drain
>   refreshes `Account.Equity`, before evaluation). `null` config = no-op ⇒ existing harnesses byte-identical.
> - **`src/TradingEngine.Host/EngineRunner.cs`** (WIRE) — passes `ResetConfig.FromRuleSet(ruleSet.
>   DailyResetTimeUtc, ruleSet.DailyResetTimezone)` so production multi-day runs roll.
> - **Tests:** `tests/.../Unit/Kernel/ResetClockTests.cs` (10, pure, BCL-guarded dates) +
>   `ResetReducerTests.cs` (4, pure re-base + governor reset) — **high confidence**; `tests/.../Simulation/
>   GoldenReplay/KernelResetMultiDayTests.cs` (2, end-to-end via the extended `KernelLoopHarness`) — **agent-
>   verify** (depends on harness internals). Added `TradingEngine.Engine` ProjectReference to the Unit csproj.
> **Agent checklist:** (1) `dotnet build`; (2) `grep -rn "new DayRolled" src` ≥1; (3) Unit Kernel +
> KernelAcceptance green; (4) golden/determinism **unchanged** (single-day → no roll); (5) re-baseline any
> committed multi-day fixture (none today) with a recorded reason.

---

## Phase G — Governor & Drawdown edge cases (pure reducer + small loop tests)

Pure tests against `GovernorMachine`, `DrawdownReducer`, `Kernel.DecideEquity`, `Kernel.DecideReset` —
fast, no IHost. These are the "break the rules" tests for the protective machinery.

### G1 — Governor state machine (Unit, Fast)
- `Governor_LossStreak_EntersCoolingOff` — N consecutive losing closes (from config threshold) flips
  `GovernorState` to cooling-off; the next `OrderProposed` is rejected with the governor reason on the
  StepRecord `DecisionReason`.
- `Governor_CoolingOff_DecrementsPerBar_AndExpires` — cooling-off counter decrements each bar (the BUG-09
  fix) and trading resumes after the window.
- `Governor_ProfitLock_BlocksNewRisk_UntilDailyReset` — once the daily profit-lock trips, new entries are
  blocked; **`DayRolled` clears it** (this is H7 — depends on G0).
- `Governor_DailyReset_ResetsLossStreakAndProfitLock` — `GovernorMachine.ApplyDailyReset` zeroes the
  per-day counters; assert via a 2-day loop fixture (depends on G0).

### G2 — Drawdown floors & bases (Unit, Fast)
- `Drawdown_Trailing_FloorTracksPeakNotCurrent` — trailing max-DD floor uses `PeakEquity` (the C3/H4 bug);
  drive equity up then down and assert the floor doesn't drop with current equity.
- `Drawdown_Fixed_FloorUsesInitialNotGrownBalance` — fixed max-DD floor anchored to initial balance even
  after realized profit grows balance (the H1 bug).
- `Drawdown_DailyBase_Configurable` — `DailyDdBase` "BalancePlusFloating" vs "Balance" changes the daily
  floor (the H3 path).
- `Drawdown_Weekly_Monthly_Enforced` — weekly/monthly DD limits reject a candidate that fits daily but
  breaches weekly/monthly (the H2 path) — through `PreTradeGate`, the production gate.

### G3 — Protection entry/exit watchdog (Unit, Fast + small loop)
- `Protection_EntersOnce_OnDailyDdBreach` — a 6%+ equity drop via `EquityObserved` enters protection
  **exactly once** (idempotent while protected — the K2 guard) and emits one force-close-all.
- `Protection_FlatBook_NoFalseBreach` — `Equity==Balance` on a flat book does not trip the watchdog (the C5
  regression guard).
- `Protection_MaxDd_AutoExitsOnReset` — protection caused by MaxDD **clears** on the next `DayRolled` per
  `ProtectionResetPolicy` (the C4 bug; depends on G0). Assert protection persists day 1, clears day 2.
- `Protection_DailyDd_AutoExitsOnReset` — daily-DD protection clears on `DayRolled`.

**Gate (G1–G3):** all green; each names the OPEN-ISSUE it guards in an xUnit `[Trait("Guards", "C4")]` (or a
comment) so a future regression is traceable to the issue.

---

## Phase F — FTMO rule pressure (Simulation, end-to-end multi-day loop)

Real backtests through `KernelBacktestLoop` over purpose-built multi-day fixtures that **deliberately
breach each FTMO-style rule**, asserting the engine reacts correctly. Reuse the `GoldenReplay` harness
wiring (FakeVenue + real `EffectExecutor`); each fixture gets its own committed snapshot.

### F1 — Daily loss limit
- `Ftmo_DailyLossLimit_HaltsTradingForTheDay_ResumesNextDay` — a fixture that loses just over the daily
  limit on day 1: assert protection/no-new-entries for the rest of day 1, and **trading resumes day 2**
  (proves the reset; depends on G0).

### F2 — Max (overall) loss limit
- `Ftmo_MaxLossLimit_HaltsRunPermanently` — cumulative loss crosses the overall max: assert protection does
  **not** clear on the daily reset (overall breach is terminal, unlike daily).

### F3 — Profit target
- `Ftmo_ProfitTarget_MetByEquityNotBalance` — open profitable position pushes **equity** over target while
  balance is under (the M6 bug): assert the target-met signal uses equity. (Pure `PropFirmRuleValidator`
  test is fine if the loop path is too heavy.)

### F4 — Minimum trading days / consistency (if modelled)
- `Ftmo_MinTradingDays_Tracked` — a multi-day run records distinct trading days; assert the count is
  exposed on the run summary / journal (skip if not yet modelled — leave a `[Fact(Skip="not modelled")]`
  breadcrumb so it's visible).

**Gate:** F1–F3 green; the daily-vs-overall distinction is proven (F1 resumes, F2 doesn't).

---

## Phase J — Journal "is it really the one source of truth?" (Unit fast + Simulation)

Prove the StepRecord journal meets the iter-36 K5 promise so iter-37 F1/F2/F4 can build on it without
fabricating.

### J1 — Completeness (Simulation)
- `Journal_EveryKernelEvent_ProducesExactlyOneStepRecord` — over a position-opening run, the count of
  StepRecords equals the count of events the kernel decided; `Seq` is gap-free and strictly increasing.
- `Journal_OrderAndFill_ShareOrderId` — an `OrderProposed`→`SubmitOrder` and its `OrderFilled` carry the
  **same** order id in `EventJson`/`EffectsJson` (the F1 join key). This is the test iter-37 F1 relies on.
- `Journal_Close_ExposesCosts` — a CLOSE StepRecord exposes commission/swap/gross/net (non-null) — even on
  the zero-cost FakeVenue they must be present (=0), not absent.
- `Journal_Reject_ExposesNamedViolation` — a rejected proposal's StepRecord `DecisionReason` is a readable
  name (e.g. `WEEKEND_RESTRICTION`, `MAX_DD`), never null / `[object Object]`.

### J2 — Per-bar "why" carried (Simulation)
- `Journal_BarClosed_CarriesPerStrategyVerdicts` — each `BarClosed` StepRecord carries one `StrategyVerdict`
  per active strategy with `SignalFired`, `Reason`, and (when fired or on the sampling stride) `Indicators`.
  This is the data iter-37 F2's funnel reads — assert it's populated, not empty.
- `Journal_Funnel_TotalsMatchBarCount` — a funnel projection built from the journal has per-bar rows
  totalling the run's bar count (the F2 gate, computed off StepRecords not the dead BarEvaluations).

### J3 — Lossless under pressure (Unit, Fast — extend `JournalLosslessTests`)
- already covered: burst N+1 into capacity-N → 0 dropped; failed batch retried → `DroppedBatches==0`. Add:
- `Journal_Determinism_ByteIdenticalAcrossRuns` — already in `DeterminismTests`; extend to the **multi-day**
  fixture so the day-roll events are in the determinism scope.

### J4 — "Can we get more data, consolidated?" (Simulation — read-path)
- `Journal_Export_Ndjson_RoundTrips` — `GET /api/runs/{id}/journal/export` yields one valid JSON object per
  line, parseable back into StepRecord-shaped objects, in `Seq` order. Proves the consolidated download.
- `Journal_Query_Paged_StableAcrossPages` — `GET /api/runs/{id}/journal?afterSeq=&limit=` paginates by
  `Seq` with no overlap/gap across pages.

**Gate:** J1–J4 green; the funnel + join + costs + violations all read **only** the StepRecord journal (no
test may assert against `PipelineEvents`/`BarEvaluations`).

---

## Phase E — Per-bar equity & account snapshots (fixes K-GAP-2)

### E0 (CODE PREREQ) — Persist backtest equity
Flush `BufferedEquitySink` → `EquitySnapshots` on run completion (preferred), or register
`PersistentEquitySink` for backtest. Fix `PersistentEquitySink` `EngineMode.Live` hard-code and the dropped
open-position/governor fields.

### E1 — Tests (Simulation, RED before E0)
- `Backtest_PersistsOneEquitySnapshotPerBar` — after a finished N-bar backtest, `GET /api/runs/{id}/equity`
  returns N (±warmup) points with sim-time stamps, monotonic in time.
- `EquitySnapshot_MapsAuthoritativeState` — each snapshot's equity/peak/daily-DD/max-DD equal the
  authoritative `EngineState` at that bar (extend the existing `KernelEquitySnapshotTests.From_Maps...`).
- `Backtest_EquityCurve_DerivesDrawdownCurve` — the persisted snapshots reconstruct the run's max-DD that
  the report header shows (so the curve and the headline number agree).

**Gate:** `GET /api/runs/{id}/equity` non-empty for a finished backtest; E1 green.

---

## Phase B — Bars in DB & per-trade chart (K-GAP-3 / K-GAP-5)

### B1 — Chart data exists for backtests (Simulation/Integration)
- `Backtest_OverCatalogData_ChartBarsServed` — after a backtest, `GET /api/bars?symbol=&timeframe=` returns
  the bars covering the run window (from catalog bars the replay adapter read). Proves the long-standing
  "no bars for chart" bug is resolved **for backtests**.
- `Bars_DedupByTimestamp` — catalog + any per-run bars collapse to one bar per timestamp (the
  `BarQueryService` guard lightweight-charts needs).

### B2 — Trade carries timeframe (Unit + code: add `Timeframe` to `TradeResultEntity` / DTO)
- `Trade_CarriesTimeframe_ForChartFetch` — a closed trade exposes its `Timeframe`; the per-trade chart can
  fetch the right bars without the `|| 'H1'` fallback (iter-37 F6).

**Gate:** B1 green; `Trade.Timeframe` present end-to-end (entity → DTO → API).

---

## Phase C — "What we expect from each backtest" (per-strategy characterization)

Lock in current behaviour so future changes are diffs, not surprises. One small deterministic fixture per
strategy (or per strategy family), a committed snapshot of `{trade count, net, win-rate, max-DD, first
entry bar+direction+lots}`. These are **characterization** tests (they record reality, not a target), so a
change to a strategy/engine must consciously re-baseline with a reason.

### C1 — Per-strategy smoke (Simulation, one `[Theory]` over the strategy bank)
- `Strategy_OnFixture_ProducesExpectedTradeShape` — for each active strategy, run a fixed fixture and assert
  the committed snapshot. Catches the iter-29-class "5/9 strategies were silently dead" regression: a
  strategy that suddenly produces **0** trades on its own fixture fails loudly.
- `Strategy_AllActive_AtLeastOneSignalEach` — over a representative multi-strategy fixture, every active
  strategy fires at least one verdict-with-signal in the journal (no silently-dead strategy).

**Gate:** every active strategy has a committed snapshot; the "≥1 signal each" test is green or the dead
strategy is explicitly quarantined with a reason.

---

## Phase D — Multi-symbol, replay & duplicate integrity

### D1 — Multi-symbol (Simulation) — exercises K-GAP-6
- `MultiSymbol_FeedbackResolvesCorrectSymbol` — a 2-symbol fixture (e.g. EURUSD + USDJPY) where both have
  open positions: assert every fill/close StepRecord attributes to the **correct** symbol (not the
  `ResolveSymbol` first-position/`EURUSD` guess). RED today → fix by carrying symbol on the exec event.
- `MultiSymbol_PerStrategyProfileSizing` — two strategies with different `RiskProfileId` size off their own
  profile (the K4 gap-1 fix); assert distinct lot sizes on the same bar.

### D2 — Replay & duplicate (Simulation/Integration)
- `Replay_SameDatasetConfigSeed_ByteIdentical` — re-running `(DatasetId, ConfigSetId, Seed)` reproduces the
  prior run's StepRecord journal + trades byte-identically (extend to a position-opening, **multi-day** run).
- `Duplicate_DifferentRiskProfile_NewConfigSameDataset` — `POST /api/runs/{id}/duplicate` with a changed
  `RiskProfileId` yields a new run, **same `DatasetId`**, **different `ConfigSetId`**, `ParentRunId`=source,
  run through `BacktestReplayAdapter` (assert the venue, not a fake).

**Gate:** D1 multi-symbol attribution correct; D2 determinism + duplicate-lineage green.

---

## Sequencing

```
G0 (roll-event code) ─► G (governor/DD) ─► F (FTMO pressure)        ← correctness spine (needs G0)
J (journal)  ─┐
E0+E (equity)─┤ (mostly independent; J unblocks iter-37 F1/F2/F4)
B (bars/chart)┤
C (per-strategy characterization)
D (multi-symbol/replay/duplicate)
```
**G0 first** — it's the prerequisite that makes every multi-day governor/DD/FTMO test meaningful. J and E
are the highest-value for iter-37's frontend (they prove the journal + equity data the UI will render is
real). C and D are regression insurance.

## Definition of Done
- The multi-day reset path is live (G0) and proven (G/F suites); C4 + H7 are **provably** closed in the
  production path (not just "kernel is authoritative" — a test fires the reset and asserts the clear).
- The StepRecord journal is proven complete/lossless/joinable/costed/violation-named (J) — iter-37 F1/F2/F4
  build on green tests, not hope.
- Backtest equity persists per bar and the report curve agrees with the headline max-DD (E).
- The per-trade chart has real bars + a real timeframe (B).
- Every active strategy has a characterization snapshot (C); multi-symbol attribution + replay/duplicate are
  proven (D).
- `OPEN-ISSUES.md` K-GAP-1..6 each have a guarding test; resolved ones move to `RESOLVED-ISSUES.md`.

## Risks
- **G0 may shift behaviour on existing multi-day runs** (resets that never fired now fire) — that's the
  *fix*, but re-snapshot any committed multi-day fixture with a recorded reason; single-day golden must stay
  byte-identical (no roll inside one day) as the guard that G0 didn't over-fire.
- **Equity persistence perf** — prefer the on-completion batch flush over per-bar DB writes.
- **Don't let these become slow IHost tests** — keep the spine in Unit/Simulation; the cTrader e2e stays the
  separate, credentialed live proof (`ctrader-e2e` skill).
