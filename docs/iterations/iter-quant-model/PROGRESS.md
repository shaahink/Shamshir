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

→ **P0.1 and P0.2 are committed. Start P0.3 (honest entry timing, tape only) next.** See "P0.3 — Not
started" below for the concrete approach (pending-order queue mirroring `_pendingLimits`).

---

## Status at a glance

| Phase | Status | Notes |
|---|---|---|
| P0.1 R vs initial stop | **Done** | Forward fix + backfill endpoint. Deviated from plan's literal backfill source (see log). |
| P0.2 Spread convention | **Done** | Both adapters unified via shared `SpreadConvention` helper; 16 new fill-path tests |
| P0.3 Honest entry timing | Not started | |
| P1 TF-agnostic bank | Not started | |
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

## P0.3 — Honest entry timing (D4, tape only) — Not started

`TapeReplayAdapter`: pending-market-order queue mirroring `_pendingLimits`, fills at next fine (M1) bar's
open ± spread. `HonestFills` toggle on run config, default ON, read once at construction (not per-bar).
Flush pending orders at disconnect using `_lastClose` so end-of-run orders still fill. A/B characterization
run, delta table in the PR body.

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
