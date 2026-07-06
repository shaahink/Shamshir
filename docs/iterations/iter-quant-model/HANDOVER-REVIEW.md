# iter-quant-model — Handover Review

**Date:** 2026-07-06
**Branch:** `iter/quant-model--p1-tf-agnostic`
**Scope:** Full phase-by-phase audit of PLAN.md vs delivered code vs mid-session feedback
**Method:** Every promise, gate, and decision traced through code, git log, and test suite

---

## 1. Executive Summary

**The iteration's structural goal is met.** The quant model has a shape: TF-agnostic strategy bank, honest
tape venue, entry surgery on all 9 strategies, excursion recorder + exit lab, research metrics (P(pass),
walk-forward, scoreboard), data coverage report, triage program, per-bar spread recording, and FTMO ops
guards. All gates on the fast simulation filter (`RequiresCTrader!=true`) are green:
- **Unit:** 504 passed, 0 failed, 6 skipped
- **Integration:** 92 passed (9 pre-existing WebSmokeTest 404s from Angular chunk hash rebuild)
- **Simulation (fast):** 127 passed, 0 failed, byte-identical

**However, three things were never done that the plan required, and one was never done correctly:**

1. **P6.1 Compare-both reconcile** — infrastructure bugs fixed (B1, B2, B3, B4) but never successfully
   executed end-to-end. Blocked on owner's cTrader credentials.
2. **P3.6 Proposal ledger + entry-tactic lab (D10)** — never implemented. Zero code, zero tests.
3. **MISSING_DATA verdict (P1.5.4)** — never implemented. Zero hits repo-wide.
4. **P3.3 ExitReplayer validation gate** — the deferred gate that was meant to force fidelity was never
   executed. P4.5.3 fixed 4 bugs in the replayer, but the end-to-end validation gate (replay a real run's
   exit rule over its recorded paths and assert the replayed exits match) was never written. The replayer
   is fixed but still untrusted.

**The iteration has 1,313 lines of uncommitted changes across 59 files** representing all P5, P6, and P7
delivery (plus P4.5 carry-forward cleanup). This review is written before committing those changes.

---

## 2. Phase-by-Phase Audit

### P0 — Truth Repair

| Sub-phase | Plan promise | Delivered? | Gate | Gate met? |
|-----------|-------------|-----------|------|-----------|
| P0.1 R vs initial stop | InitialStopLoss, R vs initial stop, backfill | Yes | Unit+Integration green, golden byte-identical | Yes (Unit 326→347, Sim 120/0) |
| P0.2 Spread convention | Full-spread both adapters, 8 fill-path tests | Yes | 16/16 fill-path tests | Yes |
| P0.3 Honest entry timing | M1 next-bar open fills, HonestFills toggle | Yes | 5 unit tests, golden byte-identical | Yes |

**Deviation from plan (P0.1):** Backfill source was `Journal.OrderProposed` rows, not `EntrySnapshotJson.stopLoss`.
The plan's prescribed source was wrong for the trades most affected (TP exits where BE/trail had already moved the
stop). This was a correctness improvement, not a scope reduction. Verified: 1,467 trades resolved from journal, 0
needed fallback. Live `trading.db` was NOT touched — endpoint ready, needs owner go-ahead.

**Verdict: PASS.** All 3 sub-phases shipped, gates green, golden byte-identical.

---

### P1 — TF-Agnostic Strategy Bank (inc. P1.5 review fixes)

| Sub-phase | Plan promise | Delivered? | Gate | Gate met? |
|-----------|-------------|-----------|------|-----------|
| P1.1 Instance-per-row (D1) | One instance per (strategy×symbol×TF) row | Yes | golden H1 byte-identical | Yes |
| P1.2 De-hardcode H1 | EntryTimeframe → _config.EntryTimeframe | Yes | Unit 347/0/6 | Yes |
| P1.3 Aux-TF feed | Union of RequiredTimeframes, interleaved in time order | Yes (but buggy—see P1.5) | M15 acceptance test | **SKIPPED in P1**, fixed in P1.5 |
| P1.4 UI guardrails | Timeframe inventory warning chip, verdict funnel counters | Partially (HonestFills checkbox only) | — | **Deferred to P2** |
| P1.5.1 Indicator-TF fix | All strategies pass Timeframe: EntryTimeframe | Yes | 9 per-strategy tests + M15 acceptance | Yes |
| P1.5.2 Lookahead fix | Aux bars gated by closeTime ≤ decision bar close | Yes | AuxTfLookaheadTests | Yes |
| P1.5.3 Fail-loud TF parse | Throw on "bogus", not silently H1 | Yes | StrategyRegistryTests | Yes |
| P1.5.4 MISSING_DATA verdict | Journal shows MISSING_DATA, not silence | **NO** | — | **Never implemented** |

**Static-review finding (critical):** P1 shipped without its M15 acceptance test. A full code trace found
two bugs that made every non-H1 strategy silently produce zero signals — the exact defect P1 was built to fix.
P1.5.1 and P1.5.2 fixed them. This is the strongest evidence for the plan's rule:
> "A deferred gate means the phase is NOT done."

**Verdict: PASS with gaps.** P1.5.1–P1.5.3 fixed and tested. P1.5.4 (MISSING_DATA) deferred to P2 but
never landed anywhere. Warning chip and verdict funnel UI also deferred.

---

### P2 — Entry Surgery

| Sub-phase | Plan promise | Delivered? | Gate | Gate met? |
|-----------|-------------|-----------|------|-----------|
| P2.1 Indicator series API | Ring buffer (64), port 4 strategies | Yes | Unit 362/0/6 | Yes |
| P2.2 rsi-divergence rewrite | Real pivot-based divergence | Yes | Unit 380/0/6 | Yes |
| P2.3 Edge semantics | bb-squeeze latch expiry, trend-breakout single-fire, ema-alignment edge | Yes | Unit 386/0/6 | Yes |
| P2.4 Time-flatten (D6) | Loop-level via CloseRequested | Yes | Unit 386/0/6 (+5 Sim) | Yes |
| P2.5 Thesis metadata | thesis/expectedTradesPerWeek/expectedHoldBars | Yes | Unit 386/0/6, Integration 98/0 | Yes |
| P2.6 Units doctrine (D9) | Normalize raw-pip fields, config linter | Yes | Unit 397/0/6 | Yes |
| P2.7 Stop orders (D10 partial) | OrderType.Stop end-to-end | Yes | Unit 416/0/6 | Yes |

**P2.7 kernel-path discovery:** While implementing Stop orders, a pre-existing gap was found: kernel-path
limit orders always reach cTrader as `"Market"` because `isLimit` derives from `entryOpts` which is null
on the kernel path. Documented, deliberately deferred. Registered as **F5** in `RECONCILE-FINDINGS.md`.

**Verdict: PASS.** All 7 sub-phases shipped. P2.7 Stop plumbing works. F5 gap is pre-existing, not
introduced by this iteration.

---

### P3 — Excursion Recorder + Exit Lab (inc. P4.5 fixes)

| Sub-phase | Plan promise | Delivered? | Gate | Gate met? |
|-----------|-------------|-----------|------|-----------|
| P3.1 Excursion recorder | Tape-only per-trade path capture | Yes | Unit 419/0/6 | Yes |
| P3.2 Exploration mode | One-click preset | Yes | Unit 434/0/6 | Yes |
| P3.3 ExitReplayer | Pure static, validation gate | Yes (logic) | **Validation gate deferred** | **NO — see P4.5.3** |
| P3.4 Calibration tables | Store + wire to AddOnResolver | Yes (write path only initially) | — | **P4.5.4 wired consumption** |
| P3.4b Reference scales | Measured per-symbol×TF ATR | Schema only | **14/84 populated** | **PARTIAL** |
| P3.5 Exit Lab UI | Heatmap, plateau highlighting | Yes | **Evaluate returned 0 cells (P4.5.2)** | **P4.5.2 fixed** |
| P3.6 Proposal ledger (D10) | Record proposals + venue outcomes | **NO** | — | **Never implemented** |

**Critical finding (P3.6):** The plan's D10 decision said "Treat entry execution as a sweepable dimension
measured with a missed-fill counterfactual ledger." P3.6 was the implementation slot for this. It was never
started. Zero code, zero tests. This is the plan's only decision that remains completely unimplemented.

**Verdict: PASS with significant gaps.** The excursion recorder works and records paths. The ExitReplayer
works after 4 P4.5.3 fixes. The Exit Lab UI evaluates grids correctly after P4.5.2's JSON format fix. But:
- P3.3 validation gate was never executed (no end-to-end replayer-fidelity contract)
- P3.6 (proposal ledger) never implemented
- P3.4b (ReferenceScales) only partially populated

---

### P4 — Research Metrics (inc. P4.5 fixes)

| Sub-phase | Plan promise | Delivered? | Gate | Gate met? |
|-----------|-------------|-----------|------|-----------|
| P4.1 P(pass) everywhere | Wire PassProbabilityEstimator | Yes | — | **P4.5.5 fixed category errors** |
| P4.2 Walk-forward harness | Rolling windows, plateau pick | Yes | **Test window never ran (P4.5.1)** | **P4.5.1 fixed** |
| P4.3 Scoreboard | Matrix strategy×symbol×TF | Yes | **In-sample vibes (P4.5.6)** | **P4.5.6 fixed** |
| P4.4 Frequency check | needed-trades math, red-flag | Yes | **Formula 30× wrong (P4.5.6)** | **P4.5.6 fixed** |
| P4.5.1 Walk-forward test leg | Run test window, PlateauPicker | Yes | 6 PlateauPicker tests | Yes |
| P4.5.2 Exit Lab JSON format | Shared codec | Yes | 11 codec tests | Yes |
| P4.5.3 ExitReplayer fixes | 4 divergences fixed | Yes | 17 replayer tests | Yes |
| P4.5.4 Calibration bind-time | Wire SL/TP consumption | Yes | (folded into P4.5) | Yes |
| P4.5.5 P(pass) fresh-challenge | Correct framing | Yes | (folded into P4.5) | Yes |
| P4.5.6 Scoreboard fixes | Symbol filter + frequency | Yes | (folded into P4.5) | Yes |
| P4.5.7 Smaller fixes | Path cap, fetch-by-run, plateau, swallowed exceptions | Yes | Unit 459/0/6 | Yes |
| Post-P4.5 cleanup | 6 items (DynamicSlTp, EntryTimeframe, 5 HIGH swallowed exceptions, EngineReducer DateTime, scoreboard formula) | Yes | Unit 488/0/6, Integration 101/0 | Yes |

**Verdict: PASS.** P4 shipped with three CRITICAL defects (walk-forward test leg missing, exit lab dead,
P(pass) inputs wrong) — but all were fixed in P4.5. The meta-lesson holds: deferred gates produce bugs.

---

### P5 — Data + Triage

| Sub-phase | Plan promise | Delivered? | Gate | Gate met? |
|-----------|-------------|-----------|------|-----------|
| P5.1 Data verification + ReferenceScales | Quality validator, populate ReferenceScales | Yes | Quality report written | Yes (14 rows) |
| P5.2 Non-FX correctness tests | Per-category unit tests | Yes (17 pre-existing) | 17/17 pass | Yes |
| P5.3 Exploration triage | 126-cell sweep, kill/park/keep | Yes | Triage report written | Yes (but M15 excluded) |
| P5.4 Portfolio assembly | Exposure groups + caps + report | Yes | 6 PreTradeGate tests | Yes |

**Partial deliveries within P5:**
- **ReferenceScales:** 14 rows populated (one per symbol from P5.1's validator run), but full 84-cell
  (14 symbols × 6 TFs) population deferred — M1 data takes too long for HTTP request timeout.
  `POST /api/data-manager/compute-reference-scales` exists but needs CLI invocation.
- **Triage scope:** 126 cells (9 strategies × 7 symbols × {H1, H4}), but M15 excluded (would have been
  63 more cells, ~3× more bars). 1-month window (Jun 2026) — insufficient for H4 strategies (need 6m–1y).
- **Only 3 cells survive:** session-breakout EURUSD H1, macd-momentum EURUSD H1, macd-momentum USDJPY H1.
  All XAUUSD cells dead.

**Verdict: PASS with scope reductions.** Quality validator works. Triage methodology works (and found a
real sweep-runner bug — content-address skip query didn't filter by StrategyId). But longer windows +
M15 data are needed for decision-grade rankings.

---

### P6 — Oracle Backstop

| Sub-phase | Plan promise | Delivered? | Gate | Gate met? |
|-----------|-------------|-----------|------|-----------|
| P6.1 Execute V4 | Compare-both, reconcile output | **Infrastructure fixed, never executed** | Reconcile output committed | **NO** |
| P6.2 Per-bar spread | Record spread at bar close | Yes | cBot→adapter→shards wired | Yes (backward-compatible) |
| P6.3 Weekly drift habit | Health endpoint + dashboard chip | Yes | Endpoint + chip | Yes |

**P6.1 status — the most important gap:**
Four infrastructure bugs were found and fixed across 4+ debug cycles:
- **B1:** Compare-both config deserialization ignored dates (lowercase JSON fields) → fixed with `PropertyNameCaseInsensitive`
- **B2:** cTrader stuck-running deadlock (channel completion circular dependency) → fixed in ReadRouterLoop/ReadSubLoop + orchestrator safety net
- **B3:** Compare-both recursive invocation (child inheriting Compare="both") → fixed by removing the CustomParam + manual state registration
- **B4:** NetMQPoller disposal race (every cTrader run crashed with ExitCode=1) → fixed by unsubscribing event handlers before poller disposal

**But a successful compare-both run was never achieved.** The fixes are structurally correct (each traced
through the full call chain) but require a clean end-to-end verify with the owner's cTrader credentials.
The plan's P6.1 gate — "reconcile endpoint output committed" — is **not met**.

**F6 (new finding):** Trade count divergence observed in early test runs (tape produces 34-83% more trades
than cTrader for same config). Hypothesis: HonestFills delays tape entries to next M1 bar → different
cooldown windows → more re-entry opportunities. Registered in `RECONCILE-FINDINGS.md`.

**F5 (pre-registered):** Kernel-path limit orders reach cTrader as Market. Confirmed by code trace at
`CTraderBrokerAdapter.cs:459-474`. Awaiting owner's V4 run to confirm impact.

**Verdict: PARTIAL.** P6.2 and P6.3 are done. P6.1 is blocked — the compare-both infrastructure is
believed correct but has never been verified end-to-end.

---

### P7 — FTMO Ops

| Sub-phase | Plan promise | Delivered? | Gate | Gate met? |
|-----------|-------------|-----------|------|-----------|
| P7.1 Intraday DD guard | Engine-level CloseRequested on breach | Yes | `KernelDailyDdGuardEvaluator.cs` (31 lines) | Yes (code only — no test) |
| P7.2 Weekend flatten | Flatten before weekend, news placeholder | Yes | `KernelWeekendFlattenEvaluator.cs` (29 lines) | Yes (code only — no test) |
| P7.3 Phase tracker | Live P(pass) page + API | Yes | `PhaseTrackerController.cs` + Angular component | Yes (code only — no test) |

**Note:** P7 sub-phases shipped without dedicated tests. The evaluators follow the exact pattern of
`KernelTimeFlattenEvaluator` (P2.4) which has tests. The phase tracker was built as specified but
lacks integration tests against the API.

**P7.2 news placeholder:** `FlattenBeforeNewsMinutes` exists on the config interface but has no runtime
behavior. Future: integrate a news calendar.

**Verdict: PASS (code complete, test-light).**

---

## 3. Decision Status (D1–D11)

| # | Decision | Implemented? | Where | Verdict |
|---|----------|-------------|-------|---------|
| D1 | Instance-per-row | Yes | P1.1 | Per-row instances, cross-symbol pollution fixed |
| D2 | R-multiple history | Yes | P0.1 | InitialStopLoss + Journal backfill |
| D3 | Spread convention | Yes | P0.2 | `SpreadConvention` helper, both adapters |
| D4 | HonestFills default ON | Yes | P0.3 | Tape-only, M1 next-open fills |
| D5 | ema-alignment edge | Yes | P2.3 | Crossover+first-pullback, not condition |
| D6 | flattenTimeUtc | Yes | P2.4 | Loop-level via CloseRequested |
| D7 | Data purchase | Yes | P5.1 | Majors 6 + XAUUSD, 4 TFs downloaded |
| D8 | bb-squeeze latch expiry | Yes | P2.3 | Expires after BbPeriod bars |
| D9 | Units doctrine | Yes | P2.6 | Normalized + config linter |
| D10 | Entry tactic as sweep dimension | **NO** | — | **P3.6 never implemented** |
| D11 | Reference scales | Partial | P3.4b + P5.1 | Schema + 14 rows; full 84-cell pending |

**10 of 11 decisions implemented.** D10 (entry tactic lab) is the only unimplemented decision. It was
the broadest decision — requiring proposal recording, missed-fill counterfactuals, and entry-tactic
sweep dimensions — and had no dedicated phase (folded into P3.6 which was never started).

---

## 4. Gate Verification Matrix Audit

| Phase | Plan gate | Actual | Status |
|-------|-----------|--------|--------|
| P0.1 | R vs initial stop test; golden byte-identical | Unit + Integration green; golden 120/0 | ✓ |
| P0.2 | 8 fill-path price tests; characterization re-baseline | 16/16; no re-baseline needed (fixtures didn't hit affected paths) | ✓ |
| P0.3 | A/B characterization run | 5 tests; golden byte-identical | ✓ |
| P1 | golden H1 identical; M15 acceptance run ≥1 proposal | golden H1 identical; **M15 was skipped** (fixed in P1.5) | Fixed ✓ |
| P1.5 | M15 acceptance test; lookahead test; bad-TF-string throws | All 3 green | ✓ |
| P2 | divergence fixtures; per-change A/B tape runs; config linter | All green | ✓ |
| P3 | replayer-reproduces-actual-run test; proposal ledger | **Neither executed** | **NOT MET** |
| P4 | stitched OOS curve; trials counter displayed | **Neither worked pre-P4.5** (fixed) | Fixed ✓ |
| P4.5 | exit-lab round-trip; replayer gate; walk-forward test-leg; PlateauPicker; frequency formula | All green | ✓ |
| P5 | non-FX cost tests green; coverage view shows downloads | 17/17 pass; quality report written | ✓ |
| P6 | reconcile endpoint output committed | **Never produced** | **NOT MET** |

**Standing gates (all phases):** `dotnet build` 0 errors; Unit 504/0/6; Integration 101 (9 pre-existing flaky from SPA chunk hash); fast Simulation 127/0 byte-identical; Architecture 6/8 (2 pre-existing failures — `EngineReducer.cs:436` fixed in P4.5 cleanup, `VenueSessionEntity` still failing).

---

## 5. Carried-Forward Debts

These are NOT solved but are documented and not blockers for iteration completion:

| # | Item | Source | Current status (verified) |
|---|------|--------|---------------------------|
| 1 | **MISSING_DATA verdict** | P1.5.4 | **Not implemented.** Zero hits in `src/` or `tests/`. Deferred three times (P1 → P2 → P4). Still deferred. |
| 2 | **ReferenceScales full population** | P3.4b / P5.1 | **14/84 cells populated.** `POST /api/data-manager/compute-reference-scales` wired. Full population needs CLI invocation to bypass HTTP timeout on M1 data. |
| 3 | **Kernel-path limit orders → cTrader as Market (F5)** | P2.7 | **Confirmed still present.** Code at `CTraderBrokerAdapter.cs:459-474` with 15-line doc-comment. `isLimit` derived from `entryOpts` which is always null on kernel path. Pre-registered in `RECONCILE-FINDINGS.md`. Needs P6.1 compare-both run to investigate. |
| 4 | **AddOnResolver.Ride Calibrated** | P3.4 | **Explicitly deferred** (line 88 comment, "P3 slot"). Calibrated mode exists for BE/trailing/partial but not DynamicSlTp ride. |
| 5 | **VenueSessionEntity missing IAuditableEntity** | Pre-existing | **Still failing.** Architecture test pre-existing failure, never touched. |
| 6 | **M15 triage data** | P5.3 | **Excluded.** 126-cell sweep used only H1+H4. M15 has ~3× more bars per cell and was excluded for time. |
| 7 | **1-month triage window** | P5.3 | **Insufficient for H4.** 1-month window gives ~720 H1 bars but only ~30 H4 bars. 6m–1y re-run needed for decision-grade H4 rankings. |
| 8 | **ExitReplayer P3.3 validation gate** | P3.3 / P4.5.3 | **Never executed.** The deferred gate that would prove replayer fidelity (replay a real run's exit rule over recorded paths, assert outputs match). P4.5.3 fixed 4 replayer bugs found by static review, but the contract test was never written. Replayer is code-complete but untrusted. |
| 9 | **P3.6 Proposal ledger (D10)** | P3.6 | **Never implemented.** No proposal recording, no missed-fill counterfactuals, no entry-tactic lab. The plan's D10 decision is the only unimplemented decision. |
| 10 | **P6.1 Successful compare-both run** | P6.1 | **Never executed.** 4 infrastructure bugs fixed (B1-B4), but no end-to-end verify. Blocked on owner's cTrader credentials. |
| 11 | **P7 news flatten placeholder** | P7.2 | **Placeholder only.** `FlattenBeforeNewsMinutes` exists on `IStrategyConfig` but has zero runtime behavior. Needs news calendar integration. |

---

## 6. Session Bug Register (P6 Debug Cycle)

| Bug | Description | Files | Fixed? | Verified? |
|-----|-------------|-------|--------|-----------|
| B1 | Compare-both config deserialization ignored lowercase JSON field names | `RunsController.cs:211` | Yes (`PropertyNameCaseInsensitive = true`) | Build only |
| B2 | cTrader stuck-running deadlock: `BarStream.Completion` waited before `DisconnectAsync` could complete channels | `CTraderBrokerAdapter.cs:257-261`, `BacktestOrchestrator.cs:1321-1333` | Yes (2 layers: channel TryComplete in finally + 30s safety timeout in orchestrator) | Build only |
| B3 | Compare-both recursive invocation: child cTrader config inherited `Compare="both"`, called `RunCompareBothAsync` recursively | `BacktestOrchestrator.cs:817-836` | Yes (remove Compare CustomParam + manual state registration instead of Start()) | Build only |
| B4 | NetMQPoller disposal race: every cTrader run crashed with ExitCode=1 | `NetMqMessageTransport.cs:85-87` | Yes (unsubscribe handlers before stopping poller) | DB evidence (55+ prior failures, none after fix) |

**All 4 bugs are fixed in code but only B4 has runtime evidence (zero new NetMQPoller errors). B1, B2, B3 require a real compare-both run to confirm end-to-end.**

---

## 7. What's Verified vs Untested

### Verified (test suite green)
- P0.1: R-multiple computed against initial stop (unit tests + Journal backfill dry-run)
- P0.2: 16 fill-path price tests, both replay adapters
- P0.3: 5 HonestFills tests (queue, fill, flush, off-mode)
- P1.5.1: 9 per-strategy indicator-TF tests + M15 acceptance test
- P1.5.2: AuxTfLookaheadTests (point-in-time H4 EMA)
- P1.5.3: StrategyRegistryTests (throws on "bogus")
- P2.1: 4 IndicatorSnapshotServiceSeriesTests + 6 SeriesBasedCrossDetectionTests
- P2.2: 8 PivotFinderTests + 4 RsiDivergenceStrategyTests
- P2.3: 6 tests (squeeze latch, single-fire, ema edge ×4)
- P2.4: 5 Simulation tests (TimeFlattenEvaluator + loop hook)
- P2.5: 3 StrategyConfigStoreTests + 1 StrategyConfigSeederTests
- P2.6: 11 UnitConversionTests + 6 ConfigLinterTests + 1 ConfigLinterRealFilesTests
- P2.7: 10 fill-path tests (BacktestReplay + TapeReplay) + 8 EntryPlannerTests + 1 kernel type test + 1 cTrader wire test
- P3.1: 3 TapeReplayExcursionRecorderTests + 1 TradeRepositoryTests
- P3.2: 3 ExplorationPresetTests
- P3.3: 9 ExitReplayerTests + 3 ExitGridEvaluatorTests
- P4.5.1: 6 PlateauPickerTests
- P4.5.2: 11 ExcursionPathCodecTests
- P4.5.3: 17 ExitReplayerTests (rewritten with spread + cadence + MAE asserts)
- P5.2: 17 NonFxCalculatorsTests (pre-existing)
- P5.4: 6 PreTradeGateGroupExposureTests
- Golden/determinism: 127/0 byte-identical (all phases)

### NOT verified (no test, or test never executed)
- P6.1: Compare-both end-to-end (B1, B2, B3 fixes)
- P3.3: ExitReplayer validation gate (replay real run → assert match)
- P3.6: Proposal ledger (never implemented)
- P1.5.4: MISSING_DATA verdict (never implemented)
- P7.1: Intraday DD guard (code only, no test)
- P7.2: Weekend flatten (code only, no test)
- P7.3: Phase tracker (code only, no test)
- P6.2: Per-bar spread end-to-end (cBot change compiles, adapter parses it, but no E2E test with real cTrader)

---

## 8. File Inventory of Uncommitted Changes

**Modified files (59):** See `git diff --stat HEAD` for full list. Key modifications:
- `AGENTS.md` — session handover notes, P6 bug register
- `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` — B2 channel completion + P6.2 spread parsing
- `src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs` — B4 NetMQPoller fix
- `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` — B2 safety timeout, B3 compare-both fix, P6.2 spread
- `src/TradingEngine.Web/Api/RunsController.cs` — B1 case-insensitive deserialization, compare-both endpoint
- `src/TradingEngine.Web/Api/SystemController.cs` — P6.3 reconcile-health endpoint
- `src/TradingEngine.Web/Api/DataManagerController.cs` — P5.1 quality report + ReferenceScales endpoints
- `src/TradingEngine.Infrastructure/MarketData/` — DataQualityValidator, ReferenceScalePopulator, MarketDataShardIo spread
- `src/TradingEngine.Host/KernelDailyDdGuardEvaluator.cs` — P7.1
- `src/TradingEngine.Host/KernelWeekendFlattenEvaluator.cs` — P7.2
- `src/TradingEngine.Domain/` — ExposureGroup, ConstraintSet, IStrategyConfig additions, Bar spread
- `web-ui/` — phase-tracker component, dashboard reconcile-health chip, data-manager quality report, exit-lab, scoreboard fixes

**New files (not tracked):** `config/compare-both/` (2 config files), `config/exposure-groups.json`,
`docs/iterations/iter-quant-model/reports/` (3 reports), P5/P6/P7 test files, PhaseTrackerController,
CompareBothRequest, ReferenceScalePopulator, DataQualityValidator, etc.

---

## 9. What the Owner Must Do (Blocked Items)

### Immediate (unblock the iteration's core promise)
1. **Run compare-both:** `POST /api/runs/compare-both` with body `{"configName": "eurusd-h1-7d.json"}`
2. After completion, run reconcile: `GET /api/backtest/analytics/reconcile?left={tapeRunId}&right={ctraderRunId}`
3. Record findings in `RECONCILE-FINDINGS.md` using the V4 template
4. If RawMoney diverges → bug hunt (F5 is the prime suspect)
5. If trade counts differ → investigate F6 (HonestFills hypothesis)

### Run backfill against live DB
- `POST /api/system/backfill-initial-stop` — idempotent, self-backing-up. Fixes R-multiple for 1,467 existing trades.
  Dry-run against copy confirmed: 1,467 updated from journal, 0 fallbacks, 0 failures.

### Future (non-blocking, quality of life)
- Full 84-cell ReferenceScales: `dotnet run --project src/TradingEngine.Host -- compute-reference-scales`
- M15 triage sweep: 63 more cells, ~3× more bars
- 6-month triage re-run for H4 decision-grade rankings
- P3.6 proposal ledger: implement D10 entry-tactic lab
- P7 news calendar integration
- Revisit kernel-path `isLimit` gap (F5) — deliberate fix, not a bug-swallow

---

## 10. Assessment

**What went well:**
- P0 truth repair landed cleanly — golden byte-identical throughout
- P1.5 review process worked — found + fixed 2 critical bugs in already-"done" code
- P4.5 review process worked — found + fixed 3 CRITICAL bugs in P3/P4 that made every research surface
  produce garbage silently
- The meta-rule "deferred gate = not done" was vindicated 4 times (P1.5.1, P3.3, P4.5.2, P4.5.3)
- The plan's "failing-test-first" methodology caught regressions that would otherwise have shipped
- Unit test suite grew from 314 to 504 — 190 new tests
- 127/127 byte-identical golden fixtures — kernel purity maintained

**What went wrong:**
- P6.1 compare-both — the iteration's headline validation — was never executed despite 4+ debug cycles
- P3.6 / D10 was completely dropped — no proposal ledger, no entry-tactic lab
- P1.5.4 MISSING_DATA deferred 3 times, still absent
- P7 shipped without tests (code-complete but no gate evidence)
- P5.3 triage used insufficient window (1 month vs needed 6m–1y) and skipped M15
- The "done" labels in PROGRESS.md for P3, P4, P6.1, and P7 overstate completion relative to PLAN.md gates
- 1,313 lines of uncommitted changes — the entire P5/P6/P7 delivery was never committed to git

**Bottom Line:** The quant model has a shape. The engine is correct (golden 127/0). The measurement
machine (excursion recorder, exit lab, scoreboard, walk-forward) works after P4.5 fixes. But the
model has not been validated against the oracle (cTrader), the entry-tactic dimension (D10) is
unimplemented, and the triage rankings need longer windows before they're decision-grade.
