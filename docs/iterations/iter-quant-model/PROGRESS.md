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

→ **P1 is committed on `iter/quant-model--p1-tf-agnostic` — gates green, BUT a 2026-07-05 static review found
2 CRITICAL bugs that mean P1's own headline claim ("non-H1 strategies now trade") is not actually true yet for
tape runs.** Do **P1.5 first** (small — two targeted fixes + their failing tests, roughly half a session),
THEN P2 — Entry surgery. See PLAN.md §3 "P1.5 — Close the P1 review gaps" for the full findings/fix spec, and
§3 P2 for what follows it.

**Why this matters before touching P2.1:** P2.1 (indicator series API) extends
`IndicatorSnapshotService`/`IndicatorCache` — the exact pipeline P1.5.1 patches. Building the new ring-buffer
series on top of the unfixed H1-pinning bug would silently bake the same defect into the new API.

This branch: 4 commits on top of `iter/quant-model` (9b9dbfc):
- `edeb3a6` P1.1 — instance-per-row, de-hardcoded H1 in all 14 strategies
- `e376a1b` P1.2 — EntryTimeframe on OrderProposed for per-TF analytics
- `71ea2d7` P1.3 — aux-TF bar preloading for mtf-trend (fixes silent death on tape)
- `6d41398` P1.4 — HonestFills checkbox on new-backtest form

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
   below. Fold into P2's verdict-funnel work.

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
| P1 TF-agnostic bank | **Done, but review found 2 critical bugs** | P1.1–P1.4 on `iter/quant-model--p1-tf-agnostic`. See P1.5 below/PLAN.md — headline claim not yet true for tape. |
| P1.5 Close review gaps | **Not started — do next, before P2.1** | 2 critical + 2 low findings, see PLAN.md §3 P1.5 |
| P2 Entry surgery | Not started | |
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
