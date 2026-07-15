# iter-alpha-loop — HANDOVER (iteration close-out)

**Closed:** 2026-07-15 · **Branch:** `iter/alpha-loop` · **Status:** DONE — all agent-driven stages
complete. The only remaining items are owner decisions / next-iteration scope; none block the close.

**Read order for a fresh session:** this file → `R5-AUDIT.md` (audited stage-by-stage state +
bugfix queue) → `TRACKER.md` (checkpoint table + Handoff block) → `LEDGER.md` (append-only session
narrative, read the R3/R4/R5 tail) → `evidence/candidate-cards.md` (the R4 result).

---

## 0. Decisions (owner call, delegated to Claude's vote — 2026-07-15)

These are decided, not open. Recorded here so the next session inherits a direction, not a question.

1. **Close `iter-alpha-loop` now** as a *trustworthy negative result*. The machinery is proven
   end-to-end; the current strategy-bank × knob space does not produce challenge-ready alpha under
   honest constraints. That is real, banked knowledge — not a wasted iteration.
2. **Objective clarified: a durable edge is the north star; 30-day challenge velocity is a hard
   constraint to be solved by *aggregation*, not by hunting faster (noisier) setups.** This is the
   load-bearing call — it rejects the instinct to chase shorter-TF setups just because they trade more.
3. **Next direction = portfolio-of-cells, NOT the trade-frequency search.** R4 proved you already
   *have* safe, positive-expectancy, walk-forward-survived edges (OOS ratios 1.58–4.32) that are
   individually too slow. Aggregating 5–8 of them onto one account attacks the actual finding
   (velocity) by *using* what you found, instead of abandoning it to re-search for individually-fast
   edges. Higher expected value than option (B). This becomes its own iteration with its own PLAN.md.
4. **Gate the portfolio iteration behind an OOS-honesty check (its Phase 0).** The individual edges
   are *plausibly real but unproven on fresh time* (R4's n=3–7 is too thin to claim more). Before
   investing in multi-cell engine machinery — cross-cell correlation, aggregate heat/risk caps,
   N-concurrent-strategy governor behavior — validate the edges on a second unseen window (as data
   accrues past 2026-07-05) or paper-forward. **If they don't hold OOS, the portfolio is built on
   sand → stop there.** This is the cheap insurance against concentrating noise.
5. **F63: do NOT backfill the 250 census rows.** Wire the real `ChallengeSimulator` into
   `SetupScoreService` as *task #1 of the portfolio iteration* (so aggregate challenge-survival is
   scored honestly from the start), not retroactively over a census that's now a dead-end direction.
6. **Defer F48 (until an XAUUSD-class candidate goes live) and s2a (one session, only if
   `runner-aggressive` re-enters the search).** Neither gets decision energy now.

**Net:** close today, banking the negative. The follow-up, when opened, is a *portfolio* iteration
that starts by re-proving the edges out-of-sample before building anything — not more knob-tuning and
not a faster-setup hunt.

---

## 1. What this iteration was

Build and run a trustworthy **alpha-discovery loop**: prove byte-level fill/cost parity against a
real venue (cTrader), run a full strategy×symbol×timeframe census, refine survivors with a
pre-registered overfit cull, then dress-rehearse the finalists on data none of the machinery had
ever seen. Plan: `PLAN.md`. The point was never "find one winning strategy" — it was to make the
*search itself* honest end to end, so a negative result is a trustworthy negative result.

## 2. What shipped (the arc)

- **Parity (P0–P4, F38–F48)** — the deepest, best-evidenced work here. Every fill/swap/commission
  model is pinned against **recorded venue output**, not modelled assumptions (`VenueFillModelTests`
  = 6 real cTrader fills; `VenueSwapModelTests` = 3 real swap charges). **EURUSD `VERDICT: PASS`**,
  tolerance budget untouched. `F47` (venue prices commission at one reference spot) deliberately NOT
  chased — it's the venue's own artifact, owner-accepted, and the gate earns the exemption from data.
- **X0–X5** — run queue + concurrency (5 bugs F49–F53 found by actually load-testing it), progress
  truth, Runs-page rework, trade-chart rework, data-manager auto-sync (isolated worktree, live cTrader
  verified), and the god-class refactor merge. All live-verified.
- **R1'** — the valid 252-cell baseline census (74 scored / 178 null-with-reason), D13-conformant.
  Superseded the original R1 (voided by F5, strategies commingled) — the plan working as designed.
- **R3** — 2 refinement sessions, 24 pre-registered variants, real 6-fold walk-forward with a working
  OOS-ratio cull (F62 — it had been a stub) that **parked 2 cells on real evidence, never deleted**.
  Found a clean generalizing pattern: `runner-aggressive` raises edge on every trend cell tried (8/8).
- **R4** — FTMO dress rehearsal on the **embargoed** 2026-05-06→2026-07-05 window (first and only
  touch). This is the finding that matters — see §3.
- **R5** — final audit (zero uncorrected deviations; every gap found was self-corrected inside the
  iteration by a later session's QA pass) + this close-out.

## 3. The headline finding (this IS the deliverable, not a setback)

**All 4 full-year survivors (composite 90–100/100) stalled or went net-negative on the one unseen
60-day window.** 0/12 rolling 30-day challenge windows reached a +10% target — **but 0/12 breached
any daily or max-loss cap** either (worst single day 1.47%).

→ **This is a return-*velocity* problem, not a *safety* problem.** The high-scoring cells are safe
but trade too infrequently (3–7 trades / 60 days) to clear a challenge target. The embargo window
exists precisely to catch full-year survivors that don't generalize to fresh time — and it caught
something real. Full per-candidate tables: `evidence/candidate-cards.md`.

The 4 candidates (all default governor/prop-rules ON, exploration OFF, tape venue):

| # | Cell | Full-yr | Embargo NetPnL | Verdict |
|---|------|---------|----------------|---------|
| 1 | trend-breakout / XAUUSD / H4 + `runner-aggressive` | 100 (OOS 2.15) | +$401 (3 trades) | safe, ~never reaches target |
| 2 | mean-reversion / AUDUSD / H1 + `conservative` | 98.3 (OOS 1.58) | +$109 (4 trades) | safe, ~never reaches target |
| 3 | ema-alignment / EURJPY / H1 + `runner-aggressive` | 97.3 (OOS 4.32) | **−$1,921** (6 trades) | net negative |
| 4 | ema-alignment / EURJPY / H1 + `aggressive` risk | 90.3 (OOS 3.23) | **−$810** (7 trades) | net negative |

## 4. What's open (nothing blocks the close — all owner-decision / next-iteration)

Ranked bugfix queue lives in `R5-AUDIT.md` §3. Items #3 and #4 were **fixed in the close-out**
(see §6). Remaining:

- **#1 — F63: `ComputeFtmoSurvival` is a placeholder** (25% weight in every R1'/R3/R4 composite).
  It's a crude equity-drawdown proxy, never a real challenge sim. The correct replacement
  (`ChallengeSimulator`, 14 tests, live-verified in R4) exists but isn't wired into scoring. **This
  is a decision, not a bug fix:** wiring it in means rescoring ~250 `ExperimentRuns`. Do that only
  if you trust composite ranking to drive the *next* search — otherwise explicitly declare "composite
  is a first-pass filter; real judgment happens at the R4 dress-rehearsal stage" and move on.
- **#2 — F48: XAUUSD tape-vs-venue PnL diverges 1.37%** (prices/lots/swap all exact; only the pip
  cross-rate *timing* differs). Harmless while the search stays tape-only; becomes load-bearing the
  moment an XAUUSD-class candidate (like #1) moves toward a live cTrader account.
- **#5 — s2a anomaly, unconfirmed** (`trend-breakout/NZDUSD/H4` + `runner-aggressive`: MaxDD
  0.01%→4.77%, trades +165%, both far outside every other `runner-aggressive` variant). One
  session's investigation before trusting `runner-aggressive` as uniformly understood.

## 5. If the owner continues — the recommended direction

**Do NOT re-tune the same 4 cells against the embargo window** — the plan explicitly forbids it, and
it would very likely reproduce the same result. The finding is *not* "these strategies are bad," it's
"high-full-year-score strategies trade too infrequently to clear a 30-day target." That points at a
**different search than R1'–R3 ran** (`R5-AUDIT.md` §5 point 2):

- higher trade-frequency setups — shorter timeframes, more symbols per strategy, or
- a portfolio-of-cells approach (combine several low-frequency edges into one account) — which is
  *not* in the current plan and would need its own PLAN.md.

Either way, **decide F63 first** — you don't want to run another census ranked by a placeholder
survival component. And more embargo-style evidence (a longer or second unseen window, once data is
available) beats any amount of re-tuning on the window already touched.

## 6. What the close-out session changed (2026-07-15)

Two cheap DB-hygiene items from R5's bugfix queue, so the iteration closes clean:

- **#3 — "no lies" invariant false-alarming.** `PLAN.md` §6's owner health-check query returned 9
  (not 0). Diagnosed: all 9 are non-completed runs (3 pre-migration empty-status + 6 `Status='running'`
  test/proof artifacts) — an interrupted run legitimately shows a stats-vs-trades skew because summary
  stats are written before trades settle. **Fix:** scoped the query to `Status='completed'` (matching
  the D13 scoring gate, which only ever reads completed runs). No rows deleted; `research doctor`
  never ran this check so no code change. **Verified 0 live.**
- **#4 — two stuck-`Running` experiments.** Backfilled truthfully (not blanket-Completed): the
  adopted 252-run `075d5240` → `Completed` at its last run's real completion time (2026-07-15
  13:59:12); the F59 crash-and-leak orphan `96fa9214` (0 runs) → `Failed:` (it crashed at creation —
  it did not complete). Root cause was already fixed by F59, so this was a one-time data backfill.
  **Verified 0 experiments remain `Running` live.**

**Tree touched:** `PLAN.md`, `R5-AUDIT.md`, `TRACKER.md`, `AGENTS.md`, and this file. The DB write
(`trading.db`) is a gitignored local research artifact and correctly does not appear in the tree —
so anyone cloning fresh will need the local DB to see the backfilled rows; the *doc* fix (#3) travels
with the repo.

## 7. State for the next session

- **Gate baseline** (unchanged — no product code changed in close-out): build 0err/5warn ·
  Unit 766/0/6 · Integration 148/0/0 · Sim-fast 144/0/0.
- **App:** NOT running (port 5134 verified free before the DB write).
- **Uncommitted:** the doc edits above are in the working tree, not yet committed — the owner asked
  to "call this iteration done," commit when ready.
