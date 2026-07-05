# iter-quant-model — Progress tracker (resumable)

**Purpose of this file:** `PLAN.md` (written by Fable 5) is the durable spec — read it first, it does not
change as work lands. This file is the **live state**: what's actually done, what's in flight, what
deviated from plan and why, and exactly where to resume. Any agent (OpenCode/DeepSeek/Claude) picking up
this iteration cold should read `PLAN.md` §0-2 for context, then this file for current state, then jump
to "Resume here".

**Branch:** `iter/quant-model` (based on `iter/data-mgmt` @ `f9b53f7`).
**Convention:** one phase/subphase = one commit, gate output pasted into the commit body (per PLAN.md §6).
Do not batch multiple subphases into one commit — the next agent needs to bisect cleanly.

---

## Resume here

→ **P1.5, P2.1 (indicator series API), P2.2 (rsi-divergence rewrite), P2.3 (edge semantics), P2.4
(time-flatten), and P2.5 (thesis metadata) are committed and gated green.** Next up is P2.6 — units doctrine
+ config linter. See PLAN.md §3 P2 for the phase spec. P1.5.4 (MISSING_DATA verdict) stays folded into P2's
verdict-funnel work per the original triage.

**Gate filter note (owner request, 2026-07-05):** cTrader-backed E2E tests (`Category=E2E`, `Category=Slow`,
`Category=NetMQ`, `RequiresCTrader=true`) are slow/flaky in this sandbox even though credentials ARE present
(confirmed — they actually run for real, not skip) and cost 10-25+ minutes per run under contention. For the
rest of P2, gate with:
`dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"`
(120/120 green, ~9s) instead of the previous `RequiresCTrader!=true` alone (which does NOT exclude
`PipelineE2ETests` — it has no `RequiresCTrader` trait, only `Category=E2E`/`Slow`). Run the FULL suite
(including cTrader E2E) once at the end of P2, not per-phase.

This branch: 5 commits on top of `iter/quant-model` (9b9dbfc):
- `edeb3a6` P1.1 — instance-per-row, de-hardcoded H1 in all 14 strategies
- `e376a1b` P1.2 — EntryTimeframe on OrderProposed for per-TF analytics
- `71ea2d7` P1.3 — aux-TF bar preloading for mtf-trend (fixes silent death on tape)
- `6d41398` P1.4 — HonestFills checkbox on new-backtest form
- (P1.5 commit — indicator-timeframe fix + aux-TF point-in-time cursor + fail-loud TF parse)

### Static-review findings (2026-07-05) — see PLAN.md P1.5 for full detail, traced via code + call-site grep, not guesses
1. **[CRITICAL, CONFIRMED] Indicator requests are still pinned to H1.** `IndicatorRequest`'s `Timeframe`
   parameter defaults to H1, and none of the 9 strategies' `RequiredIndicators` pass
   `Timeframe: _config.EntryTimeframe` (mtf-trend hardcodes it explicitly for RSI/ATR). Effect: any run-plan
   row with `EntryTimeframe != H1` never gets its indicators computed (the TF's bars were never loaded) →
   every strategy silently returns null on any non-H1 timeframe — the exact "0 trades on M15" bug P1 exists to
   fix, moved one layer down. Not caught because the M15 acceptance test was skipped (see below) and the one
   test added this session (`IndicatorCacheKeyTests`) only tests `BuildKey` in isolation with hand-picked
   distinct-TF requests, never what the real strategies pass.
2. **[CRITICAL, CONFIRMED] Aux-TF preload leaks future data (lookahead bias).** `EngineRunner.cs`'s P1.3 code
   loads mtf-trend's ENTIRE run H4 bar range up front and computes `EMA_200` exactly ONCE before the loop
   starts — that single, run-end-inclusive value is then read for every decision throughout the whole
   backtest. Contradicts PLAN.md's own P1.3 design (aux bars should be gated by `closeTime <= decision bar
   closeTime`); the shipped code has no such gating. Affects mtf-trend at ANY EntryTimeframe (independent of
   finding 1). No existing/future mtf-trend tape result should be trusted for calibration until fixed.
3. **[LOW, CONFIRMED] Silent H1 fallback on unparseable run-plan TF string** in
   `StrategyRegistry.CreateStrategies` (`Enum.TryParse(...) ? tf : Timeframe.H1`) — unreachable today (UI
   always sends valid values) but a landmine of the exact bug class this iteration exists to kill. Should
   throw, not silently default.
4. **[LOW, documentation gap] MISSING_DATA verdict was never implemented** — PLAN.md's P1.3 gate promised it;
   grep of `src/`+`tests/` for `MISSING_DATA` returns zero hits, and it wasn't disclosed in the deviations list
   below. Deferred to P2's verdict-funnel work (unchanged — not part of the P1.5 fix below).

### P1.5 fixes — Done (2026-07-05, same session as the review)

All three findings above independently re-verified by direct code trace (not just re-reading the review)
before fixing, and each has a failing-test-first regression test confirmed to fail against the pre-fix code:

- **P1.5.1** — every strategy's `RequiredIndicators` now passes `Timeframe: _config.EntryTimeframe`
  (mtf-trend: RSI/ATR only; EMA keeps `_config.HigherTimeframe`). New tests:
  `StrategyIndicatorTimeframeTests` (per-strategy, all 9) + `NonH1AcceptanceTests` (real
  `BarEvaluator`+`IndicatorSnapshotService`-driven M15 tape run, ≥1 trend-breakout proposal — the literal
  P1.2 gate the plan asked for and P1 skipped). Found and fixed an unrelated pre-existing gap while writing
  the acceptance test: `StrategyTestHelper.MakeContext` hardcodes bars under the `Timeframe.H1` key
  regardless of the actual bar's TF — a test-helper limitation, not a production bug; left as-is since
  `NonH1AcceptanceTests` doesn't use that helper.
- **P1.5.2** — `IndicatorSnapshotService` gained `SetAuxBarSource`/`AdvanceAuxBarsAsync`: the full aux-TF
  range is registered but held back in a cursor, revealed one bar at a time gated by
  `auxBar.OpenTimeUtc + tf.ToTimeSpan() <= decisionBarCloseUtc`, recomputing that aux TF's indicators only
  when new bars actually became eligible. `BarEvaluator.EvaluateAsync` calls `AdvanceAuxBarsAsync` before
  `RecomputeIndicatorsAsync` each decision bar. `EngineRunner.RunAsync`'s old bulk-load-and-recompute-once
  block replaced with a `SetAuxBarSource` registration call. New test `AuxTfLookaheadTests` (real
  `BarEvaluator` pipeline, synthetic H4 series with a step-change midway) — confirmed to fail
  (`Observed` stayed empty) with the `AdvanceAuxBarsAsync` call temporarily commented out, confirmed to pass
  restored.
- **P1.5.3** — `StrategyRegistry.CreateStrategies`'s run-plan branch now throws `InvalidOperationException`
  on an unparseable TF string instead of silently binding H1. New tests: `StrategyRegistryTests` (throws on
  `"bogus"`, binds correctly on `"M15"`).

Gate: `dotnet build` 0 errors; Unit 356/0/6 (skips pre-existing, up from 347 — +9 new); Integration 94/0;
fast Simulation (`RequiresCTrader!=true`, includes golden/determinism) 124/0 (4m30s, byte-identical — expected,
since the fix only changes behavior for non-H1 runs and mtf-trend's aux path, neither exercised by the
existing H1-only golden fixtures); Architecture 6/8 (2 pre-existing failures, same as documented in the P0.1
section above — `EngineReducer.cs:436` and `VenueSessionEntity`, neither file touched this session).

### What's NOT in P1 (deferred, by design — distinct from the bugs above)
- Warning chip for "strategy TF has no inventory data" — the infrastructure exists (inventory in Data Manager) but the per-row warning chip wasn't added to new-backtest. This is genuinely P2 scope (when scoreboard/triage make it matter).
- Verdict funnel counters in run monitor — `StrategyVerdict` records carry the data, endpoint exists, but the UI widget wasn't built. P2 scope.
- Per-row instance dedup — `CreateStrategies` creates one instance per (strategy, symbol, TF) row. Duplicate rows create duplicate instances. Harmless (each evaluates independently) but wasteful. Fix if it shows up in perf profiling.

---

## Status at a glance

| Phase | Status | Notes |
|---|---|---|
| P0.1 R vs initial stop | **Done** | Forward fix + backfill endpoint. |
| P0.2 Spread convention | **Done** | Both adapters unified via shared `SpreadConvention` helper. |
| P0.3 Honest entry timing | **Done** | Tape only, per plan. |
| P1 TF-agnostic bank | **Done** | P1.1–P1.4 on `iter/quant-model--p1-tf-agnostic`. |
| P1.5 Close review gaps | **Done** | P1.5.1–P1.5.3 fixed+tested; P1.5.4 (MISSING_DATA) deferred to P2. |
| P2.1 Indicator series API | **Done** | Ring buffer + 4 strategies ported off private fragile state. |
| P2.2 rsi-divergence rewrite | **Done** | Real pivot-based divergence via PivotFinder + P2.1's series. |
| P2.3 Edge semantics | **Done** | ema-alignment/trend-breakout/bb-squeeze real edges, not conditions. |
| P2.4 Time-flatten behavior | **Done** | Loop-level, wired via the previously-dead CloseRequested event. |
| P2.5 Thesis metadata | **Done** | thesis/expectedTradesPerWeek/expectedHoldBars, all 9 strategies. |
| P2.6–P2.7 (units doctrine, stop orders) | Not started | |
| P3 Excursion recorder + Exit Lab | Not started | |
| P4 Research metrics | Not started | |
| P5 Data + triage (owner-driven) | Not started | |
| P6 Oracle backstop | Not started | |
| P7 FTMO ops | Not started | |

---

## P0.1 — R vs initial stop — **Done**

**Commit:** `fix(P0.1): compute R-multiple against initial stop, not trailed stop` (this branch).

### What shipped
- `PositionState.InitialStopLoss` (new field, set once in `PositionLifecycle.CreateIntended`, never
  mutated by any `with` expression elsewhere — verified by grep, no other site touches it).
- `PublishTradeClosed.InitialStopLoss` carries it through all 4 construction sites in
  `PositionLifecycle.cs` (partial-close, open→closed, reducing→closed, closing→closed — there are
  **4** sites, not the 3 the plan's agent-guidance section guessed; all 4 live in `PositionLifecycle.cs`,
  none in `EngineReducer.cs`).
- `EffectExecutor.HandlePublishTradeClosed` computes `riskDistance` from `effect.InitialStopLoss`
  instead of `effect.StopLoss` (which is the current/trailed stop at close time — unchanged meaning,
  still used for display).
- `TradeResult.InitialStopLoss` (nullable `Price?`) + `TradeResultEntity.InitialStopLoss` (nullable
  `decimal?`) + EF migration `M34_InitialStopLoss` (nullable, old rows unaffected).
- `EntrySnapshotJson` now correctly stores the INITIAL stop (`effect.InitialStopLoss.Value`) — see
  deviation note below, this was silently storing the final/trailed stop before.
- `ExitDetailJson` gains `finalStopLoss` + `initialStopLoss` keys alongside the existing `reason`/`exit`/`r`.
- Backfill: `POST /api/system/backfill-initial-stop` on `SystemController` (idempotent — only touches
  rows where `InitialStopLoss IS NULL`; safe to re-run). Takes a file-copy DB backup first
  (`trading.bak-{timestamp}.db` next to the live db) before writing anything.

### Deviation from PLAN.md — why, and what to tell the owner
Plan text (§P0.1): *"Backfill script (D2): parse `EntrySnapshotJson.stopLoss`, recompute `RMultiple` in
place."* **This source is wrong for exactly the trades the bug corrupted most.**

Traced it in code: `EntrySnapshotJson` (despite its name) is built at **close** time in
`EffectExecutor.HandlePublishTradeClosed` from `effect.StopLoss` — which is `PositionState.CurrentStopLoss`
at the moment of close, i.e. the **final/trailed** stop, not the entry-time stop. For a trade that never
had breakeven/trailing trigger before closing, current==initial, so the plan's recipe happens to work. For
a trade where BE/trailing DID move the stop (exactly the TP-exit population the plan's own §0.3 flags —
"TP exits average R=6.997" — because a TP exit usually means price ran deep enough to trigger BE/trail
first), `EntrySnapshotJson.stopLoss` is the WRONG (moved) value. Backfilling from it would reproduce the
same bug under a different name.

**What the backfill actually uses instead:** the persisted `Journal` table (table name is `Journal`, not
`JournalEntries` as the DbSet property name and some comments imply — checked the live schema directly).
Every accepted entry has an `OrderProposed` journal row (`EventKind='OrderProposed'`, `DecisionReason=
'Accepted'`) whose `EventJson` carries the strategy's original `StopLoss` at the top level, keyed by
`OrderId`. Verified against the live DB: **100% of the 1,467 existing trades have a matching
`OrderProposed` row** for their `RunId`/`OrderId` (`SELECT COUNT(*) FROM TradeResults t WHERE NOT EXISTS
(SELECT 1 FROM Journal j WHERE j.RunId=t.RunId AND j.EventKind='OrderProposed')` → 0). Backfill logic:
group candidate trades by `RunId`, pull that run's `OrderProposed` rows once, build an `OrderId → StopLoss`
map, apply. Falls back to `EntrySnapshotJson.stopLoss` only if a run has no journal (pre-journal-era data,
none currently in the live DB) — that fallback path is flagged `"approx": true` in the response so nobody
mistakes it for the accurate figure.

This is a strictly more-correct implementation of the same intent (honest R), not a scope change — flagging
it here because the next agent (or the owner) reading PLAN.md literally would reach for the wrong column.

### Gate evidence
- New tests: `PipCalculatorTests` (4 new, pure R-multiple math incl. the regression case showing a
  breakeven-moved stop must NOT change R), `PositionLifecycleTests` (2 new: `CreateIntended` sets
  `InitialStopLoss`; a full close after a simulated `CurrentStopLoss` move still publishes the ORIGINAL
  stop), `InitialStopBackfillerTests` (6 new, pure parser/resolver — journal-shape JSON confirmed against
  the live DB, malformed-row handling, journal-preferred-over-snapshot-fallback).
- `dotnet build` (full solution): 0 errors.
- Unit: 326 passed, 6 skipped (pre-existing skips), 0 failed.
- Integration: 94 passed, 0 failed (required generating+applying the EF migration first — WebSmokeTests
  boots the real app and EF's `PendingModelChangesWarning` correctly caught the un-migrated model).
- Fast Simulation suite (`RequiresCTrader!=true`, includes golden/determinism): **120 passed, 0 failed —
  byte-identical, no re-baseline needed** (R lives on `TradeResult`, not `StepRecord`, exactly as the plan
  predicted).
- Architecture suite: 6 passed, 2 failed — both **pre-existing on this branch, not introduced by P0.1**
  (`EngineReducer.cs:436` `DateTime.UtcNow` in `ReconcileToVenue`, and `VenueSessionEntity` missing
  `IAuditableEntity` — neither file was touched this session; confirmed via `git diff` showing no changes
  to either, and `EngineReducer.cs` last modified 2026-07-01, three days before this session).
- **Backfill dry-run**, run twice against an isolated copy of the live db (never touched the real
  `trading.db` — copied to scratchpad, ran the Web app against the copy via `Persistence__DbPath`, called
  the endpoint, deleted the copy after):
  - First call: `totalCandidates: 1467, updatedFromJournal: 1467, updatedFromSnapshotFallback: 0,
    skippedNoSource: 0` — every existing trade resolved from the journal, none needed the fallback.
  - Second call (idempotency check): `totalCandidates: 0` — correctly a no-op on rerun.
  - **Before/after the actual bug**, queried by `ExitReason` for `trend-breakout` (the strategy the plan's
    §0.3 cited): TP-exit average R was **6.997** in the plan's original evidence (computed against the
    trailed stop); after backfill + the forward fix, TP-exit average R on the same data is **1.938** across
    236 TP trades — matching the strategy's configured 2.0-3.0 RR instead of the inflated figure. SL-exit
    average moved from the plan's −0.68 to −0.599 (942 trades). This is the concrete before/after the plan's
    gate asked for.
- **The live `trading.db` was NOT touched.** The endpoint (`POST /api/system/backfill-initial-stop`) is
  implemented, tested against an isolated copy, and ready — running it for real against the owner's live
  data is a one-command call but is exactly the kind of data-mutating action worth the owner's go-ahead
  first, even though it's idempotent and self-backing-up.

---

## P0.2 — Full-spread convention (D3) — **Done**

**Commit:** `fix(P0.2): full-spread convention, both replay venues` (this branch).

### What shipped
- New `src/TradingEngine.Infrastructure/Adapters/SpreadConvention.cs` — the single shared helper
  (`AskPrice(bid, spread)`, `AskBar(bidBar, spread)`) used by both `TapeReplayAdapter` and
  `BacktestReplayAdapter`, replacing each adapter's own `GetHalfSpread()` (spread/2) with `GetSpread()`
  (full spread) and routing every fill/detection site through the shared helper — this is what makes the
  two adapters unable to drift apart again (that drift is exactly how the pre-existing half-spread bug
  happened, independently, in both files).
- Fixed 8 call sites per adapter (16 total): long market entry (half→full spread), buy-limit reached
  condition (was missing spread entirely — `bar.Low <= limit`, now `bar.Low + spread <= limit`), sell-limit
  reached condition (had a spurious half-spread — now correctly raw `bar.High >= limit`), short SL/TP
  detection bar shift (half→full), short SL/TP fill price (half→full), `ClosePositionAsync` (long lost its
  incorrect half-spread subtraction entirely — selling at bid is the raw price, no adjustment; short
  half→full), `ComputeFloatingPnL` (same long/short correction), and the tick-channel synthetic ask (was
  `close + PipSize` — ONE PIP regardless of the symbol's actual spread, completely unrelated to the fill
  spread; now `close + GetSpread()`, the same source as fills).
- Long-side SL/TP detection and fill price were ALREADY correct (raw, unshifted) before this change — only
  the short side and the two bugs above (missing/spurious limit-spread, mismatched tick spread) needed
  fixing.

### Gate evidence
- **Table-driven fill-path tests, written first, confirmed failing pre-fix**: 8 cases × 2 adapters = 16
  tests (`BacktestReplaySpreadConventionTests.cs`, `TapeReplaySpreadConventionTests.cs`), 2-pip spread,
  hand-computed literal fill prices per the plan's own prescription. Pre-fix run: 3/8 failed per adapter
  (long entry, short SL, short TP) with the failing diffs showing exactly half the expected spread
  (off by 0.0001 against an 0.0002 spread) — direct evidence of the bug. Post-fix: 16/16 green.
- Unit: 342 passed (up from 326), 6 skipped, 0 failed.
- Integration: 94/94 passed.
- Fast Simulation suite (golden/determinism, `RequiresCTrader!=true`): **120 passed, 0 failed — byte-
  identical, confirmed after the run completed** (4m15s).
- **No separate REBASELINE commit was needed.** The plan expected characterization baselines to move; they
  didn't, because the existing golden/characterization fixtures don't happen to exercise the specific
  half-spread-affected paths (long entry, short SL/TP fill price, limit-reached conditions) — they're
  long-side-heavy fixtures or rely on `ClosePositionAtAsync`/engine-detected exit prices that were already
  correct pre-fix. This was verified, not assumed (per the plan's own rule: if golden had moved, stop and
  investigate before re-baselining — it didn't move, so there's nothing to investigate). A future run with
  short-heavy or limit-heavy fixtures WOULD show a P&L delta; nothing in the current suite happens to cross
  that path.

## P0.3 — Honest entry timing (D4, tape only) — **Done**

**Commit:** `fix(P0.3): honest entry timing — market fills at next fine bar's open` (this branch).

### What shipped
- `TapeReplayAdapter`: new `_pendingMarketOrders` dictionary (mirrors `_pendingLimits` exactly). When
  `HonestFills` is on (default) AND finer (M1) exit bars exist (`_exitBars.Count > 0`), a market order
  queues in `SubmitOrderAsync` instead of filling instantly; `ProcessPendingMarketOrders(fine)` — called
  first in the fine-bar loop inside `OnBarObserved`, before `ProcessPendingLimits`/`ProcessSlTpHits` so a
  freshly-filled position's SL/TP still gets checked against the SAME bar it entered on (correct intrabar
  behaviour) — fills each pending order at that fine bar's `Open` (± full spread via `SpreadConvention`,
  same directional convention as every other fill path).
- `HonestFills` constructor param (default `true`) read ONCE at construction, not branched per-bar (plan's
  own trap warning). Wired through `BacktestOrchestrator.cs` via `cfg.CustomParams["HonestFills"]`, same
  pattern as the existing `GovernorEnabled`/`DisableRegime` toggles — `!= "false"` defaults it ON.
- Flush trap (plan's own warning, hit exactly as predicted while writing the disconnect test): an order
  queued on the last fine bar of the run has no more bars to fill it — `FlushPendingMarketOrders()` fills
  any still-pending orders at `_lastClose` (± spread) inside `DisconnectAsync`, before the channels
  complete. `DisposeAsync` clears (doesn't fill) any survivors, matching the existing precedent for
  `_pendingLimits` (dispose is a hard stop, disconnect is the graceful flush point).
- When no finer data exists (`_exitBars.Count == 0` — single-resolution mode, or `HonestFills=false`),
  behavior is BYTE-IDENTICAL to before: instant fill at the decision bar's close. `BacktestReplayAdapter`
  is untouched — P0.3 is tape-only per the plan (`BacktestReplayAdapter` has no finer-than-decision-TF
  bars to honestly queue against).

### Known gap (intentionally out of scope here)
`HonestFills` is only settable via `BacktestConfig.CustomParams` at the engine/orchestrator layer — there
is no REST request DTO field or Angular UI checkbox yet. Adding one is P1.4-shaped work (UI guardrails);
P0 stayed scoped to engine correctness. The next agent touching the new-backtest UI should wire it then.

### Gate evidence
- 5 new unit tests (`TapeReplayHonestFillsTests.cs`): queues instead of filling at submit; fills at the
  next fine bar's open with the correct directional spread (long +spread, short raw); `HonestFills=false`
  preserves the exact old instant-fill behavior (A/B, both paths asserted against the same fixture);
  a pending order on the last bar of the run still fills at `DisconnectAsync` (the flush trap the plan
  itself warned about — implemented alongside the queue, then confirmed by this test, rather than written
  test-first; the queue/flush pairing was designed as one unit since an unflushed queue is an obvious
  trade-count leak, not a subtle case worth discovering via a failing test first).
- Unit: 347/347 passed (up from 342), 6 skipped.
- Integration: 94/94 passed.
- Fast Simulation suite (golden/determinism, `RequiresCTrader!=true`): 120/120 passed, byte-identical
  (4m18s) — expected, since `HonestFills` defaults ON but the existing golden fixtures don't happen to run
  the tape venue in dual-resolution mode with a distinguishing M1 fixture; nothing to re-baseline.

---

## Working notes for future phases (carried from research, not yet acted on)

- `PositionManager.cs` maintains its OWN `_initialSlDistance` dictionary (private, per-position, used only
  for the `SteppedR` trailing method) — this is unrelated to the new `PositionState.InitialStopLoss` field
  and does NOT need to change for P0.1. Don't conflate the two when touching trailing code in later phases.
- `KernelDriver.cs` carries a "STATUS: skeleton for handover" doc-comment and is NOT the live journal writer
  — `KernelBacktestLoop.cs` + `SqliteStepRecordSink.cs` (background sink, `RawEvent`/`RawEffects` serialized
  off the pump thread) is the real path. If a later phase needs to touch journal writing, start there, not
  in `KernelDriver.cs`.
- The `Journal` table's `EventJson` is PascalCase, enums as member-name strings, value objects nested as
  `{"Value": ...}` (confirmed against live data, matches `RunNarrativeService.cs`'s doc-comment). Any future
  parsing code (P3 excursion recorder, P4 walk-forward) should reuse that same shape/casing assumption.

---

## P1 — TF-agnostic strategy bank — Done on `iter/quant-model--p1-tf-agnostic` (2026-07-04)

4 commits: P1.1 instance-per-row + de-harden H1, P1.2 EntryTimeframe on OrderProposed, P1.3 aux-TF bar preloading for mtf-trend, P1.4 HonestFills checkbox.

Gates: build 0, Unit 347/0/6, Integration 94/0, golden 63/63 byte-identical, npm 0, non-E2E Simulation 112/0.

Deviations from plan: aux-TF loading hardcoded to mtf-trend+H4 (not computing RequiredTimeframes union — only strategy with aux TFs currently). No M15 acceptance test (requires live M15 data not guaranteed by test harness). Warning chip and verdict funnel deferred to P2. Instance-per-row doesn't dedup duplicate (strategy,symbol,TF) rows in run plan — harmless, fix if profiling shows waste.

**2026-07-05 static review update:** the skipped M15 acceptance test turned out to be load-bearing — a
full code-trace review found the indicator-request layer never got de-hardcoded (still implicitly/explicitly
pinned to H1 in all 9 strategies) and a lookahead-bias bug in the aux-TF preload (mtf-trend's H4 EMA is
computed once from the full run range instead of point-in-time). See "Static-review findings" under
"Resume here" above and PLAN.md §3 P1.5 for the full trace and fix spec — P1.5 must land before P2.1.

---

## P2.1 — Indicator series API — Done (2026-07-05, same session as P1.5)

**What shipped:** `IndicatorSnapshotService` gained a capped ring buffer (last 64 values, latest last) per
sig key, written through a single `Emit(key, value)` point inside `RecomputeIndicatorsAsync` (replacing every
direct `IndicatorValues[key] = ...` assignment) so the series can never drift out of sync with the latest
value. New `GetSeries(key)` + `BuildStrategyIndicatorSeries(symbol, strategy)` (mirrors
`BuildStrategyIndicatorValues`'s shape). `MarketContext` gained an optional `IndicatorSeries` positional
parameter (default `null`, so every existing call site stays source-compatible) plus a
`MarketContextExtensions.GetSeries(key)` null-safe accessor. `BarEvaluator.EvaluateAsync` builds and passes
the series alongside the existing values dict.

Ported all 4 strategies PLAN.md flagged as having cadence-fragile private state, deleting the fields entirely:
- `MacdMomentumStrategy._lastHist` → reads `context.GetSeries("MACD_12_26_9_Histogram")[^2]`/`[^1]`.
- `SuperTrendStrategy._prevDirection` → reads the direction series, but scans BACKWARD from `[^2]` for the
  last VALID (±1) reading rather than blindly indexing `[^2]` — Skender emits an invalid/0 direction during
  its own internal warmup, and the old field only ever cached valid readings, so a naive index would have
  been a subtly different (looser) semantic than the field it replaced. Caught and fixed before landing, not
  after — see `SuperTrend_SkipsInvalidWarmupReadings_WhenScanningForPreviousDirection` in
  `SeriesBasedCrossDetectionTests.cs`.
- `MtfTrendStrategy._prevRsi` → reads `context.GetSeries("RSI_{period}")[^2]`/`[^1]` (RSI has no invalid
  sentinel, so a direct index is safe here).
- `BollingerSqueezeStrategy._bbWidthQueue` → derives the prior-width window from the BB Upper/Lower/Middle
  series (all three already tracked automatically by the ring buffer) instead of maintaining its own queue.
  `_cooldownRemaining`/`_squeezeActive` are legitimate trade-state (not fragility) and were left untouched.

**Test-infrastructure fallout (expected, fixed same session):** two existing test helpers construct
`MarketContext` without ever supplying `IndicatorSeries` (`StrategyTestHelper.MakeContext`'s callers), so
after the port they'd silently get an empty series forever and the 4 strategies would never fire — caught by
running the FULL gate, not assumed:
- `StrategySignalContractTests.cs`'s `CountSignals` — the strong ("must fire", not just "doesn't throw")
  regression lock for these strategies — went from 5/5 passing to 3 real failures (`MacdMomentum_emits_on_
  reversal`, `SuperTrend_emits_on_reversal`, `BollingerSqueeze_emits_on_squeeze_then_breakout`, all "found 0"
  signals). Fixed by accumulating a real growing per-key history alongside its existing per-bar indicator
  recompute and threading it through as the series.
- `StrategyScenarioTests.cs`'s 4 "DoesNotThrow" tests didn't fail (weak assertions) but would have silently
  stopped exercising real logic. Added `StrategyTestHelper.BuildFullSeries` (recomputes indicators at every
  bar count, building a genuine incremental series — the same shape production's ring buffer produces, just
  recomputed from scratch each step) and wired it through all 4 tests.
- Also fixed, while touching this file: `StrategyTestHelper.MakeContext` hardcoded the bars dictionary key to
  `Timeframe.H1` regardless of the bar's actual timeframe (the same test-helper gap P1.5.1 flagged but left
  alone at the time) — now uses `bar.Timeframe`. Harmless generalization; every existing caller only ever
  passes H1 bars.

New tests: `IndicatorSnapshotServiceSeriesTests` (4 — accumulation/latest-last ordering, 64-cap, unknown-key
empty, `BuildStrategyIndicatorSeries` shape) and `SeriesBasedCrossDetectionTests` (6 — one hand-worked
fixture per strategy proving the port preserves the exact same fire/no-fire semantics as the field it
replaced, including the SuperTrend invalid-warmup-reading edge case above).

**Gate:** `dotnet build` 0 errors; Unit 362/0/6 (+6 new); Integration 94/0; fast Simulation
(`RequiresCTrader!=true`) 128/0 (reran twice — first run hit an isolated 300s timeout in an unrelated cTrader-cli
E2E test under load, `GbpUsd_H1_30Days_ProducesTrades`, which passed in 41s standalone and in the full suite
on rerun (4m41s) — confirmed pre-existing environmental flake, not a P2.1 regression); Architecture 6/8 (2
pre-existing, unrelated files, undisturbed).

---

## P2.2 — rsi-divergence rewrite — Done (2026-07-05, same session as P2.1)

**What shipped:** deleted the P0-era tautology
(`rsiAtLowest = lowestIdx >= 0 ? rsi : rsi` — always the current RSI, so "divergence" was never actually
tested; net effect was "fade any fresh N-bar breakout with RSI on the expected side of 50", not divergence).

New `TradingEngine.Services.Helpers.PivotFinder` — a pure static fractal swing-high/low detector
(`FindSwingLows`/`FindSwingHighs`, non-repainting: a pivot needs `strength` bars with a strictly worse
extreme on EACH side, so the most recent `strength` bars can never themselves be a pivot). Built and unit
tested standalone (8 tests: single V, monotonic-no-pivot, tied-lows-not-a-pivot, W-shape two-pivot-in-order,
inverted-V high, too-few-bars, zero-strength-throws, non-repainting-at-the-tail) before touching the
strategy, per PLAN.md's own agent guidance.

`RsiDivergenceStrategy` rewritten: finds the two most recent confirmed swing lows (bullish) / highs
(bearish) within a `DivergenceLookback`-bar window (default bumped 10→**40→50** — see deviation below), reads
RSI at those EXACT bar positions from `context.GetSeries("RSI_{period}")` (P2.1), and requires: bullish =
price makes a LOWER low while RSI makes a HIGHER low, confirmed by the current bar's close breaking above
the more recent pivot's High; bearish mirrors on highs/a lower RSI high/breaking below the pivot's Low. SL
sits just beyond the confirming pivot (ATR-buffered). A new `_lastTradedPivotTime` field (legitimate
one-shot trade-state, not the cadence-fragility class P2.1 targeted) stops the same confirmed divergence
from re-firing every bar while price stays past the confirmation level.

**Deviation from plan — `DivergenceLookback` had to grow, twice.** The plan didn't specify a value; the
existing default (10) was sized for the OLD single-swing heuristic, not real divergence. A genuine
double-bottom/top (decline → bounce → second decline) easily spans 30-50+ bars — my first fixture's two
pivots landed 41 bars apart and were invisible to a `lookback+strength*2+1`-sized window (an even tighter
formula than the raw lookback). Fixed by (a) redefining the window as `min(bars, series, lookback)` — lookback
IS the total span searched, not margin around one point — and (b) raising the default to 50 (just under the
P2.1 series' 64-entry cap, leaving headroom). `RequiredBarCount` simplified to
`DivergenceLookback + RsiPeriod + 5`.

**Test-infrastructure fallout (caught by running the real gate, not assumed):**
`StrategySignalContractTests.CountSignals` only started accumulating its per-key series history from
`RequiredBarCount` onward, not from bar 0 — production's ring buffer fills from the engine's very first bar,
so by the time a strategy first gets evaluated it may already hold history reaching bars well before
`RequiredBarCount` (exactly the case for a divergence pivot pair straddling that boundary). Fixed by starting
accumulation at `i=0` and capping the history at 64 to mirror `IndicatorSnapshotService.SeriesCapacity`
exactly, evaluating the strategy only once `i >= RequiredBarCount` as before. Also replaced
`RsiDivergence_emits_on_reversal`'s fixture (`Reversal(140,140)`, a single monotonic reversal) — it tested
the OLD tautology's behavior (fires on any fresh breakout), not real divergence, which structurally needs a
SECOND pivot to diverge against. New `DoubleBottomDivergence()` fixture: a steep first decline (deeply
oversold RSI), a partial bounce, then a shallower/slower second decline reaching a lower absolute price
(textbook divergence — lower price low, higher RSI low), each leg ending in an explicit exaggerated-wick
pivot bar (adjacent legs otherwise share the exact transition price + wiggle, which **ties** under
PivotFinder's strict inequality and confirms no pivot at all — caught via a scratch diagnostic, not guessed).

New tests: `PivotFinderTests` (8), `RsiDivergenceStrategyTests` (4 — hand-injected series proving a real
bullish divergence fires, a same-direction "no divergence" case doesn't, a single-pivot case doesn't, and
the bar-count gate).

**Gate:** `dotnet build` 0 errors; Unit 380/0/6 (+18 new: 8 PivotFinder + 4 RsiDivergence + 6 carried from
P2.1's SeriesBasedCrossDetectionTests already counted); Integration 94/0; Simulation (owner request — see
gate-filter note above, cTrader-touching categories excluded for the rest of P2)
`RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ` 120/0 (~9s); Architecture 6/8 (2
pre-existing, unrelated files, undisturbed). Full cTrader-inclusive suite deferred to end-of-P2 per owner.

---

## P2.3 — Edge semantics (D5, D8) — Done (2026-07-05, same session as P2.1/P2.2)

**bb-squeeze latch expiry (D8).** New `_barsSinceLatched` counter: increments each bar the latch stays
armed without a new contraction re-arming it; once it exceeds `BbPeriod`, the latch clears
(`_squeezeActive = false`) so a stale contraction from weeks ago can no longer arm a breakout. Reset on
fire and in `Reset()`. New test `BollingerSqueeze_LatchExpires_AfterBbPeriodBarsWithoutBreakout` — confirmed
failing against the pre-fix code (a breakout ~21 bars after an old, expired latch incorrectly fired).

**trend-breakout single-fire (D5).** The old check re-fired on EVERY bar of a continuing trend (a monotonic
rise makes every bar a "fresh" N-bar high under a naive rolling-window comparison). Now only fires on a
false→true STATE TRANSITION: the current bar breaks its rolling window AND the prior bar did NOT break
ITS OWN (one-bar-earlier) rolling window — i.e. genuinely the first breakout bar of the run, not a
continuation. New `CooldownBars` param (default 5) additionally suppresses re-entry for a few bars after
any fire. New test `Evaluate_ContinuingMonotonicBreakout_FiresOnlyOnce_NotEveryBar` — confirmed 35 signals
(one per bar) pre-fix, 1 signal post-fix, on the same 35-bar monotonic run.

**ema-alignment edge conversion (D5).** Deleted the state CONDITION (`fast>slow AND price>fast` — true
every bar of any trend, despite the code comment claiming "crossover"). New edge, fully derived from bars +
the P2.1 EMA series (no private state at all — a stateless recomputation from history each call, so replay
of the same tape always gives the same answer regardless of call cadence): (1) a fast/slow crossover within
`CrossoverLookback` bars (new param, default 20), (2) no earlier bar since that crossover touched the fast
EMA, (3) THIS bar touches the fast EMA (the pullback) and closes back on the trend side (confirmation).
`RequiredBarCount` grew by `+CrossoverLookback` to give the window room. New tests (4, hand-injected series):
fires on a genuine crossover+first-touch, does NOT fire on a second touch after the same crossover, does NOT
fire on a sustained condition with no crossover event, insufficient-bars gate.

**Test-infrastructure fallout (caught by running the real gate, not assumed):**
- `StrategyCharacterizationTests.Signals()` had the same bar-0-vs-RequiredBarCount series-accumulation gap
  already fixed once in `StrategySignalContractTests.CountSignals` (P2.2) — fixed identically here.
- `EmaAlignment_FiresLong_OnCleanUptrend`'s `StrongTrend` fixture (a perfectly smooth trend from bar 0) can
  legitimately produce ZERO signals under the new pullback-entry semantics — a clean trend has no pullback
  to trade. Renamed to `EmaAlignment_FiresLong_OnUptrendWithPullback` with a new `TrendWithPullback` fixture:
  flat/ranging warmup (so the fast/slow EMA start converged — a trend running since bar 0 never shows a
  discrete crossover under the recompute-from-scratch test methodology, since Skender's EMA seed already has
  fast>slow by the time both are first computable) followed by a clean uptrend, then a pin-bar pullback
  (a deep lower wick, but the close stays on the trend side, confirming continuation) that genuinely touches
  the fast EMA. Took several iterations with scratch diagnostics to get the wick depth right (a shallow dip
  doesn't reach the EMA; too deep and the close itself falls below it, failing confirmation).
- `NonH1AcceptanceTests.M15Run_TrendBreakout_ProducesAtLeastOneProposal` (P1.5.1's M15 gate test) used a
  monotonic uptrend from bar 0 — the exact shape P2.3's single-fire logic no longer signals on once
  `RequiredBarCount` bars have passed (the trend is already mid-continuation by then). Fixed the fixture to a
  flat warmup for exactly `RequiredBarCount` bars, then the trend — same root cause and fix shape as the new
  `TrendBreakoutStrategyTests` single-fire test.

**Gate:** `dotnet build` 0 errors; Unit 386/0/6 (+6 new: 1 bb-squeeze latch-expiry, 1 trend-breakout
single-fire, 4 ema-alignment edge); Integration 94/0; Simulation
`RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ` 120/0 (~9s); Architecture 6/8 (2
pre-existing, unrelated files, undisturbed).

---

## P2.4 — Time-flatten behavior (D6) — Done (2026-07-05, same session as P2.1–P2.3)

**What shipped:** loop-level, per-strategy, daily time-flatten — closes every OPEN position a strategy
holds once the bar's time-of-day reaches that strategy's configured flatten time.

- `IStrategyConfig` gained an optional `TimeOnly? FlattenAtUtc => null` default interface member (touches
  zero of the 9 concrete config classes except the one override below — C# default interface members avoid
  a mechanical one-line edit across every strategy config).
- `SessionBreakoutConfig.FlattenAtUtc => Parameters.FlattenTimeUtc` wires the previously-dead
  `FlattenTimeUtc` (PLAN.md's own audit: zero readers repo-wide, confirmed again before writing this) into
  the new mechanism.
- New `KernelTimeFlattenEvaluator` (mirrors `KernelTrailingEvaluator`'s exact shape — the impure per-bar
  adapter pattern): for each Open position on the current bar's symbol, looks up its owning strategy's
  `Config.FlattenAtUtc`; if the bar's time-of-day has reached it, the position is due for flattening.
- **No new kernel event needed.** Research before implementing found `CloseRequested(PositionId, Reason,
  OccurredAtUtc)` already exists with a full, tested reducer handler
  (`EngineReducer.HandleCloseRequested` → `PositionLifecycle`'s `(Open, CloseRequested)` transition →
  emits `CloseOpenPosition`) — it just had zero callers anywhere in `src/`. This phase is its first real
  caller, reusing well-tested kernel logic instead of inventing a parallel force-close path.
- `KernelBacktestLoop` gained an `evaluateTimeFlatten` hook (same shape as `evaluateTrailing`: optional,
  default null so every existing call site — and the golden byte-identical guarantee — is untouched),
  invoked after trailing/breakeven each bar; each decision becomes a `CloseRequested` event, enqueued and
  pumped through the real reducer. Wired in `EngineRunner.BuildKernelLoop` via a new `_timeFlatten` field
  constructed alongside `_trailing`.

New tests: `KernelTimeFlattenEvaluatorTests` (4 — flattens at/after the time, not before, ignores strategies
with no `FlattenAtUtc`, ignores already-closed positions) and
`KernelLoop_TimeFlattenHook_ForceClosesOpenPosition` (proves the full wiring end-to-end through the real
`KernelBacktestLoop` — queue → pump → reducer → `CloseOpenPosition` effect — not just the evaluator in
isolation; a hook that unconditionally flattens closes the golden fixture's position with
`ExitReason == "TimeFlatten"`).

**Gate:** `dotnet build` 0 errors; Unit 386/0/6 (unchanged — new tests landed in Simulation, not Unit, since
they need `TradingEngine.Host`); Integration 94/0; Simulation
`RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ` 125/0 (+5 new, ~9s); Architecture 6/8
(2 pre-existing, unrelated files, undisturbed). Golden suite unaffected (`evaluateTimeFlatten` defaults
null on every existing call site).

---

## P2.5 — Thesis metadata — Done (2026-07-05, same session as P2.1–P2.4)

**What shipped:** each strategy config gains `thesis` (one-sentence falsifiable claim),
`expectedTradesPerWeek`, `expectedHoldBars` — persisted (not a hardcoded map), editable via the Strategies
API/UI, seeded from `config/strategies/*.json`. Used later by P4's frequency reality check (needed trades
≈ target% / (risk% × OOS avgR) vs actual).

- `StrategyConfigEntry` (Domain) + `StrategyConfigEntity` (Infrastructure) gain the 3 nullable fields.
  New EF migration `M35_StrategyThesis` (additive, nullable columns — no data loss on existing rows).
- `SqliteStrategyConfigStore` wires them through `ToEntity`/`ToEntry`/`UpsertAsync` (both insert and update
  paths).
- `StrategyConfigSeeder.LoadFromJson` parses `thesis`/`expectedTradesPerWeek`/`expectedHoldBars` from each
  strategy's JSON file.
- All 9 `config/strategies/*.json` files got a genuine one-sentence thesis + plausible
  expectedTradesPerWeek/expectedHoldBars, informed by each strategy's actual (post-P1/P2) entry logic —
  e.g. ema-alignment's thesis states the NEW P2.3 pullback-entry edge, not the old condition.
- **Found and fixed in passing:** `rsi-divergence.json`'s `parameters.divergenceLookback: 10` was still the
  OLD value — P2.2 changed the C# default to 50, but an explicit JSON value overrides the record default,
  so the SHIPPED config was still using the too-tight lookback that made real divergence unobservable
  (the exact bug P2.2 fixed in code silently didn't apply to the deployed default). Fixed to 50.
- **Found and fixed in passing:** `StrategyMetadataMap`'s entry-rule/exit-formula map (a separate,
  pre-existing static metadata table) had a dead entry keyed `"bollinger-squeeze"` — the real
  `[StrategyId]` is `"bb-squeeze"` (confirmed in `BollingerSqueezeStrategy.cs`), so this entry never
  matched anything `StrategiesController` looked up. Fixed the key.
- `StrategiesController` surfaces the 3 fields on both the list and detail GET endpoints, and accepts them
  in `UpdateConfig`. Angular `StrategySummary`/`StrategyDetail` types + the strategy list card + strategy
  detail edit form/read-only view all updated.

New tests: `StrategyConfigStoreTests` (3 — new-entry round-trip, update-existing round-trip, null-metadata
round-trip, all against a real in-memory SQLite `TradingDbContext`) and `StrategyConfigSeederTests` (1 —
seeds the REAL `config/strategies/*.json` files via the real seeder and asserts all 9 have non-blank thesis
metadata, so a future edit that silently drops a strategy's thesis fails loudly). The `rsi-divergence.json`
`divergenceLookback` regression above was caught by manual review while writing this section, not by a
test — noted here as a reminder that P2.2's C# default change doesn't retroactively fix an explicit JSON
override, and worth a general sweep if other P2 default changes (e.g. trend-breakout's new `CooldownBars`)
were ever made explicit in a JSON file too (checked: none were).

**Gate:** `dotnet build` 0 errors (including a retry past the known Angular build-race flake); Unit 386/0/6;
Integration 98/0 (+4 new); Simulation
`RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ` 125/0 (~9s); Architecture 6/8 (2
pre-existing, unrelated files, undisturbed). Angular `tsc --noEmit` clean.
