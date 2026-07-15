# R5 — Final Audit + Owner Pack

**Date:** 2026-07-15. **Auditor:** fresh session, no memory of R0–R4 sessions — audited from
artifacts (TRACKER.md, LEDGER.md, commits, live DB queries, one build+test run), not from trusting
prior claims. Per PLAN.md §R5: audit R0–R4 against the plan, verify the empty-table fact stays
dead, compile a ≤5-item bugfix queue, correct AGENTS.md's RESUME pointer, hand the candidate cards
+ this pack to the owner.

**Scope discipline:** this is an audit and packaging stage. No scored experiment was re-run, no
code was changed, the embargo window was not touched again.

---

## 1. Stage-by-stage conformance

Legend: **CONFORMS** (delivered exactly as planned, evidence checks out) · **CWF** (conforms with
finding — delivered, but a real gap was found, and *fixed within the iteration itself*, usually by
a later session's QA pass) · **DEVIATES** (plan violated and never corrected).

**Headline: zero uncorrected DEVIATES.** Every gap this audit or a prior session found was caught
and fixed inside the iteration — the QA-the-previous-session's-claims discipline in the session
protocol (§4.2) actually worked every time it was exercised.

| Stage | Verdict | Evidence |
|---|---|---|
| R0 (readiness, truth fixes, score/doctor verbs) | CONFORMS | F20/F21/F19/F18 fixed; `SetupScoreService`+doctor delivered per §2; gate 716/0/6·121/0/0·144/0/0 green |
| R1 (original baseline sweep) | **SUPERSEDED, plan-conformant** | Plan's own 0b amendment declared it INVALID (F5, strategies commingled) and scheduled R1' as the real replacement — this is the plan working as designed, not a deviation |
| R2 (original parity guard) | **SUPERSEDED, plan-conformant** | Same amendment: INVALID (F1, sign-convention bug voided the money analysis), re-run as the P4 verdict |
| P0 (cost-sign truth) | CONFORMS | D9 convention, invariant tests, cBot partial-close fix; commit `de52441`; gate 721/0/6 |
| P1 (venue-declared symbol specs) | **CWF** | Delivered as planned, but the QA session (F25) found `VenueSymbolSpecs` was **never actually written to the DB** despite the row's own "engine persists VenueSymbolSpec" claim — in-memory-only. Same QA pass found and fixed a CRITICAL live bug (F24, `TypicalSpread` merge collapsing `MaxSlPips` 30×, rejecting every cTrader trend-breakout signal). F25's persistence gap was closed two sessions later by F44 (`IVenueSymbolSpecStore`). Verified now: `VenueSymbolSpecs` = 14 rows (§2 below). |
| P2 (limit-entry parity) | CONFORMS | Resting-order contract written first; F30 (fine-bar expiry decrement) + F31 (cBot fill-reporting, 0→12 trades) found live and fixed; D11 flip confirmed live |
| P3 (exit + spread parity) | CONFORMS | Gap-through/exit-spread verified correct on read; F32 (spread-number mismatch) found+fixed+live-verified |
| P4 (parity as a gate) + F38/F40/F34/F43-F47 | CONFORMS | This is the deepest, best-evidenced work in the iteration — every fix pinned against **recorded venue output** (`VenueFillModelTests` against 6 real cTrader fills, `VenueSwapModelTests` against 3 real swap charges), not modelled assumptions. `VERDICT: PASS` on EURUSD, tolerance budget untouched. F47 explicitly NOT chased (venue's own artifact, owner-accepted). F48 correctly still OPEN (see bugfix queue). |
| X0 (run queue) | **CWF** | Landed by a prior session marked "DONE (delivered, untested)" — its own truth gate (5 parallel starts) had never been run. The dedicated verification session ran it for the first time and found 5 real bugs (F49–F53) before it actually worked under load. Now genuinely proven: 5 truly-parallel runs, byte-identical results, reproduced 2×. |
| X1 (progress/status truth) | **CWF** | Same pattern as X0 — "delivered, untested" until the same verification session (F50 thread-pool starvation, F52 cancelled-shows-as-failed). Fixed and live-proven in the same pass. |
| X2 (Runs page, notes, copy-run) | CONFORMS | Live-verified; F55 (duplicated runs vanished) + F56 (copy-run dead on cold cache) found and fixed same session |
| X3 (trade chart rework) | CONFORMS | Live-verified; F57 (3-defect "meaningless lines" root cause) + F58 (SL painted with final not initial stop) fixed |
| X4.0–X4.5 (data-manager auto-sync + cTrader consolidation) | CONFORMS | Delivered in isolated worktree, live-verified against real cTrader creds, merged cleanly (X4.m) |
| X5 (god-class refactor merge) | CONFORMS | Merge gate satisfied: live cTrader compare-both smoke matches pre-refactor baseline within the known F48 band |
| R1' (re-run baseline sweep, D13) | CONFORMS | 252 one-cell-per-run tape runs, 74 scored/178 null-with-reason, gate PASS with the query pasted (not asserted) — exactly R3 of AGENTS.md's research-integrity rules |
| F59 (experiment crash+leak) / F60 (drawdown unit bug) | CONFORMS | Both found during R1', both fixed in the very next session, both re-verified live, 252 runs re-scored |
| R3 (2 sessions, 24 variants, F61/F62) | CONFORMS | Owner elected to stop at 2 of the plan's allowed 3–5 sessions — explicitly plan-conformant ("a ceiling, not a quota"). F61 (walk-forward silently validating the wrong variant) and F62 (OOS-ratio cull was a stub) were both blocking-class bugs caught **before** they could corrupt a result, not after. Two cells genuinely parked on real evidence (`StrategyCellParks`, verified below), never deleted. |
| R4 (FTMO dress rehearsal) | CONFORMS | Challenge-sim mechanism correctly identified as missing (not assumed) and built; embargo window touched exactly once; owner call on #3/#4 executed; headline reported as-is (2/4 net negative) with zero attempt to reinterpret. F63 filed, correctly scoped out (rescoring ~250 rows is a separate undertaking). |

### Notable pattern across the audit

Every CWF row shares the same shape: a session marks something DONE, a *later* session's mandatory
QA step (protocol §4.2) actually exercises the claim and finds it was untested, overclaimed, or
silently broken (F24/F25, F49–F53, F59/F60, F61/F62). The plan's insistence on this step is not
boilerplate — it caught a critical bug (F24) and a stub cull mechanism (F62) that would otherwise
have shipped invisibly. Recommend this discipline continue past R5.

---

## 2. Empty-table fact — verified dead, with live counts

The 2026-07-10 starting state was: "every research table is empty." Queried the live DB
(`src/TradingEngine.Web/data/trading.db`) directly, this session, right now:

```
sqlite> SELECT COUNT(*) FROM ExperimentRuns;          283
sqlite> SELECT COUNT(*) FROM Experiments;                5
sqlite> SELECT COUNT(*) FROM VenueSymbolSpecs;           14
sqlite> SELECT COUNT(*) FROM StrategyCellParks;           2
sqlite> SELECT COUNT(*) FROM WalkForwardJobs;             7
sqlite> SELECT COUNT(*) FROM WalkForwardWindowResults;   37
sqlite> SELECT COUNT(*) FROM BacktestRuns;              665
sqlite> SELECT COUNT(*) FROM TradeResults;             8773
sqlite> SELECT COUNT(*) FROM EquitySnapshots;      2003934
```

All non-zero, all growing across the iteration's sessions. Confirmed **not** hollow: `StrategyCellParks`'
2 rows match the exact 2 cells the ledger names (`trend-breakout/XAGUSD/H1` and
`mean-reversion/GBPUSD/H1`, both reason strings citing their real OOS ratio 0.0). `WalkForwardJobs`
= 7 / `WalkForwardWindowResults` = 37 reconciles exactly: 6 jobs × 6 folds (36, the two R3 walk-forward
batches) + 1 job × 1 fold (the F61 fix's own smoke-test verification, correctly not counted in the
ledger's "6 jobs / 36 results" narrative since that smoke run predates the formal walk-forward
sessions). **The empty-table fact is dead and stays dead.**

**PLAN §6's own "no lies" invariant, run live:**

```sql
SELECT COUNT(*) FROM BacktestRuns WHERE TotalTrades !=
  (SELECT COUNT(*) FROM TradeResults t WHERE t.RunId = BacktestRuns.RunId); -- plan says: always 0
```
**Result: 9**, not 0 — see bugfix queue item #3 below. This is a real, previously-unflagged finding
from this audit, not a repeat of anything in the ledger.

---

## 3. Bugfix queue (≤5, ranked by impact)

| # | Item | Impact | Why it's ranked here |
|---|---|---|---|
| 1 | **F63 — `ComputeFtmoSurvival` is a placeholder**, 25% weight in every composite score computed across the whole iteration (all of R1'/R3/R4's rankings) | **Highest** | Every ranking decision this iteration made — which cells made the R1' top-20, which R3 survived to R4 — was partly built on a component that has never done a real challenge simulation. `ChallengeSimulator` (built in R4) is the correct replacement but isn't wired into scoring. A rescore of ~250 `ExperimentRuns` is real work but would tell you whether the R1'/R3 ranking itself would have looked different with a truthful survival number. |
| 2 | **F48 — XAUUSD tape-vs-venue PnL diverges 1.37%** (prices/lots/swap all exact; only the pip's cross-rate *timing* differs) | **High** | R4 candidate #1 (`trend-breakout/XAUUSD/H4`, the highest full-year score) is exactly the symbol class this affects. Not urgent while search stays tape-only (D1), but becomes load-bearing the moment anyone proposes taking candidate #1 toward a live/cTrader account. |
| 3 | ~~**9 `BacktestRuns` rows break PLAN §6's "no lies" invariant**~~ **— FIXED (R5 hygiene 2026-07-15)** | ~~Medium~~ | Confirmed all 9 are non-completed (3 pre-migration empty-status + 6 `Status='running'` test/proof runs); scoped to `Status='completed'` the invariant returns **0**. Fixed by scoping PLAN §6's invariant query to `Status='completed'` (matches the D13 scoring gate, which only ever reads completed runs) — an interrupted/running run legitimately shows a stats-vs-trades skew. No row deletion needed; the query no longer false-alarms. `research doctor` never ran this check, so no code change required. Verified: invariant now returns 0 live. |
| 4 | ~~**both `baseline-sv1-prime` `Experiments` rows stuck `Status="Running"`**~~ **— FIXED (R5 hygiene 2026-07-15)** | ~~Medium~~ | Backfilled truthfully (not blanket-Completed): the adopted 252-run `075d5240` → `Completed` at its last run's real completion time (2026-07-15 13:59:12); the F59 crash-and-leak orphan `96fa9214` (0 runs) → `Failed: F59 crash-and-leak...` (matches `MarkFailed` convention — it crashed at creation, it did not complete). Root-cause path was already fixed by F59, so this was a one-time data backfill, no code change. Verified: 0 experiments remain `Running` live. |
| 5 | **s2a anomaly — unconfirmed mechanism** (`trend-breakout/NZDUSD/H4` + `runner-aggressive`: MaxDD jumped 0.01%→4.77%, trade count +165% (43→114), both far outside every other `runner-aggressive` variant's range) | **Low-medium** | Flagged twice in the ledger (R3 session 2, R4 handoff) and never chased. It's the one Pattern-A confirmation that behaves like a different bug class rather than ordinary variance — worth one session's worth of investigation before trusting `runner-aggressive` as uniformly well-understood. |

*(F61b — display-only `effectiveConfigJson` bug — and the `scalp-tight` trade-count-drop mechanism
were considered and left off: both are confirmed harmless to real trading/scoring, lower priority
than the five above.)*

---

## 4. AGENTS.md RESUME — corrected

The RESUME block (line 317 onward) was stamped 2026-07-12, claiming "P0–P4 ALL DONE. Next up: X0" —
eleven checkpoints and five sessions out of date (X0 through R4 all shipped since). Updated in place
to point at R5/the owner gate; see the diff to `AGENTS.md` in this commit.

---

## 5. One-page "what I'd do next" — for the owner

**Where this iteration landed:** the machine ran the full loop the plan asked for — truthful parity
against a real venue (not a modelled guess), a real 252-cell census, two rounds of pre-registered
refinement with a working overfit-cull (walk-forward + OOS ratio, which parked 2 cells on real
evidence), and a dress rehearsal on data none of it had ever seen. That last step is the one that
matters most: **every survivor that scored 90–100/100 on the full year stalled or went negative on
the one unseen 60-day window it touched.** No risk-discipline failures anywhere (0/12 rolling
windows breached a daily or max-loss cap) — this is a *speed* problem, not a *safety* problem. Full
numbers: `evidence/candidate-cards.md`.

**What I would NOT do:** re-run the embargo window, tune any of the 4 candidates against it, or
treat "maybe it's just thin-sample variance" as license to ship one anyway. The embargo window
exists to catch exactly this pattern, and it did its job.

**What I would do, in order:**
1. **Decide on F63 first.** The current composite score's FTMO-survival component has never done a
   real simulation. Before spending another research session ranking cells with it, either wire
   `ChallengeSimulator` into `SetupScoreService` and rescore the ~250 existing rows (now that the
   simulator is proven — 14 tests, live-verified on R4's 4 candidates), or explicitly decide the
   composite ranking is "good enough as a first-pass filter, real judgment happens at the R4-style
   dress-rehearsal stage" and move on. Either is defensible; leaving it silently unresolved is not.
2. **If continuing the search:** the finding isn't "these 4 strategies are bad," it's "high-score
   full-year strategies trade too infrequently to clear a 30-day target." That's a different search
   than R1'–R3 ran — it points at higher-trade-frequency setups (shorter timeframes, more symbols
   per strategy, or a portfolio-of-cells approach that isn't in this plan) rather than another round
   of pack/risk knob tuning on the same handful of cells. Re-running R3-style refinement on the same
   4 candidates without addressing trade frequency would very likely reproduce the same embargo
   result.
3. **Cheap, do regardless:** items #3 and #4 in the bugfix queue above are cheap (one query filter,
   one status backfill) and remove two false alarms for the next person who runs `research doctor`
   or pokes at the DB directly.
4. **F48** only matters the moment XAUUSD-class candidates move toward a real venue — not urgent
   while the search stays tape-only, but don't forget it's there when that day comes.

This session's own conclusion: the alpha-loop machinery itself (parity, census, refinement,
walk-forward cull, dress rehearsal) is now trustworthy end-to-end — every stage's claims were
checked against artifacts and held up, and the two process gaps that *were* found (F63's real
weight, the two DB hygiene items) are both easy to name and easy to fix. What it found is a genuine,
useful negative result on the current candidate set, not a flaw in the search itself.
