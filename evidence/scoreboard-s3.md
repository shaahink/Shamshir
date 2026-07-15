# R3 session 2 — variant scoreboard (testing session 1's patterns for generalization)

- Pre-registration: `docs/iterations/iter-alpha-loop/LEDGER.md` §"R3 session 2 — pre-registration"
- 12 variants, 9 cells from the R1' top-20 NOT touched in session 1, same window
  (2025-07-04 -> 2026-05-05), tape venue.
- Coverage: 12/12 ExperimentRuns persisted (100%), **12/12 scored** — every variant cleared the
  20-trade floor this time (session 1 had 1 null). Truth gate PASS.

## Results vs. each cell's own R1' baseline

| Variant | Cell | Knob | Composite | ExpectancyR | DD% | Consistency | Trades | Verdict |
|---|---|---|---|---|---|---|---|---|
| **baseline** | trend-breakout/NZDUSD/H4 | (standard) | 83.7 | 0.353 | 0.01 | 66.7 | 43 | — |
| s2a | " | pack=runner-aggressive | 88.5 | **0.550** | 4.77 | 60 | 114 | edge +56%, but DD jumped 0.01%→4.77% and trades +165% — see Caveats |
| s2k | " | risk=aggressive | 80.8 | 0.353 | 0.03 | 50 | 43 | **edge exactly flat**, DD flat, trades flat — consistency down only |
| **baseline** | trend-breakout/ETHUSD/H4 | (standard) | 82.7 | 0.326 | 0.31 | 71.4 | 48 | — |
| s2b | " | pack=runner-aggressive | 95.0 | **0.574** | 0.31 | 71.4 | 74 | **CLEAN WIN** — edge +76%, DD/consistency held exactly |
| **baseline** | trend-breakout/XAGUSD/H1 | (standard) | 76.8 | 0.172 | 0.96 | 100 | 35 | — |
| s2c | " | pack=runner-aggressive | 91.2 | **0.552** | 1.84 | 50 | 50 | edge **+221%**, biggest of the whole program — but consistency 100→50, DD ~2x |
| **baseline** | trend-breakout/USDCAD/H4 | (standard) | 72.0 | 0.204 | 0.04 | 60 | 37 | — |
| s2d | " | pack=runner-aggressive | 88.1 | **0.482** | 0.04 | 40 | 52 | edge +136%, DD flat, consistency down |
| **baseline** | mtf-trend/EURJPY/H1 | (standard) | 87.6 | 0.387 | 1.36 | 75 | 31 | — |
| s2e | " | pack=runner-aggressive | 95.6 | **0.595** | 1.02 | 75 | 45 | **CLEAN WIN** — edge +54%, DD improved, consistency held exactly |
| **baseline** | super-trend/AUDUSD/H1 | (standard) | 74.4 | 0.263 | 0.54 | 50 | 34 | — |
| s2f | " | pack=runner-aggressive | 91.2 | **0.503** | 0.68 | 50 | 49 | edge +91%, consistency held exactly, DD modestly up |
| **baseline** | mean-reversion/GBPUSD/H1 | (standard) | 96.5 | 0.519 | 0.28 | 80 | 32 | — |
| s2g | " | pack=scalp-tight | 77.0 | 0.274 | 0.28 | 60 | 31 | **LOSS** — edge -47% |
| s2j | " | risk=aggressive | 96.5 | **0.519** | 0.56 | 80 | 32 | **scale-invariant** — edge/consistency IDENTICAL, DD ~2x for 4x risk |
| **baseline** | mean-reversion/AUDUSD/H1 | (standard) | 96.1 | 0.693 | 0.09 | 77.8 | 33 | — |
| s2h | " | pack=scalp-tight | 65.6 | 0.124 | 1.29 | 55.6 | 32 | **LOSS** — edge -82% |
| s2l | " | risk=conservative | 98.0 | **0.736** | 0.04 | 88.9 | 31 | **CLEANEST WIN OF THE PROGRAM** — edge UP, DD halved, consistency UP |
| **baseline** | mean-reversion/GBPJPY/H1 | (standard) | 90.1 | 0.467 | 0.09 | 57.1 | 22 | — |
| s2i | " | pack=scalp-tight | 52.1 | **-0.063** | 0.47 | 28.6 | 21 | **LOSS** — edge inverted to negative |

## Reading the results

**Pattern A confirmed: `runner-aggressive` raises raw edge on trend-following-family strategies —
now 8/8 across both sessions** (v1a, v6a from session 1; s2a–s2f from session 2, 6/6 this time).
Every single trend-style cell tried gained ExpectancyR, several substantially (s2c +221%, s2d
+136%, s2f +91%). But **the "free lunch" (DD/consistency held flat) only shows up cleanly in about
half** — s2b and s2e match the clean v1a/v6a pattern exactly; s2a, s2c, s2d, s2f trade some
consistency and/or DD for the edge gain. The pattern generalizes; the "no cost" part doesn't always
come with it.

**Pattern B rejected: `scalp-tight` does NOT fit mean-reversion's thesis — if anything it's worse
here (0/3, one edge inversion to negative) than on the trend/momentum strategies it lost on in
session 1.** The hypothesis assumed mean-reversion's "take the quick bounce" thesis would tolerate
an early trail; instead all 3 mean-reversion cells lost more of their edge than session 1's
trend/momentum cells did on average. `scalp-tight` looks like a universally bad pack in this
system regardless of strategy thesis, not a thesis-fit question — worth treating as closed rather
than re-testing again.

**New pattern found: `conservative` risk profile produced its first unambiguous clean win**
(`mean-reversion/AUDUSD/H1`, s2l) — edge up, DD nearly halved, consistency up, all at once. Every
prior `conservative` test (session 1: macd-momentum, bb-squeeze) traded edge for DD relief at a
worse-than-1:1 ratio; this is the first cell where reducing risk improved everything. One data
point, not yet a pattern — worth one more `conservative`+mean-reversion test before generalizing.

**Scale-invariance confirmed a second time**: `mean-reversion/GBPUSD/H1` (s2j) matches v6b's
`ema-alignment/EURJPY/H1` result almost exactly — `aggressive` risk (4x) left ExpectancyR and
Consistency completely unchanged while DD scaled up only ~2x, not 4x. Two high-consistency,
low-baseline-DD cells, two clean scale-invariant results — a real signal for R4 sizing, not a
coincidence.

## Caveats

- **s2a's numbers are an outlier worth a closer look, not chased further this session**: DD jumped
  from 0.01% to 4.77% (a ~477x increase in absolute terms, even though 4.77% is still a modest
  drawdown) and trade count jumped +165% (43→114), both far larger than any other
  `runner-aggressive` variant tried (typical trade-count jump from `PartialTp` row-doubling is
  +40-75%). Something about this specific cell (NZDUSD/H4) interacts differently with the pack —
  flagged, not investigated.
- All scores are `sv1-partial` until walk-forward runs (next).

## Walk-forward + F62 scoring — final result (same session)

6-fold walk-forward run for all 3, then re-scored against their job (F62's real OOS ratio, not a
placeholder). This is the first batch scored with F62 live, and the gate did real work — it
disagreed with the pre-walk-forward composite ranking on 2 of 3 candidates:

| Variant | Cell | Test windows won | Cumulative test PnL | OosRatio | Composite before → after | Verdict |
|---|---|---|---|---|---|---|
| s2c | trend-breakout/XAGUSD/H1 + runner-aggressive | 3/6 | +$2,324 | **0.0** | 91.2 → **77.5** | **PARKED** |
| s2l | mean-reversion/AUDUSD/H1 + risk=conservative | 6/6 | +$7,398 | **1.58** | 98.0 → **98.3** | Survives, strongest candidate |
| s2j | mean-reversion/GBPUSD/H1 + risk=aggressive | 4/6 | +$8,908 | **0.0** | 96.5 → **82.0** | **PARKED** |

**s2c and s2j both parked** (`StrategyCellParks`, not deleted — the D-gate is `OOS ratio < 0.5`, and
both landed at exactly 0.0). In both cases, the walk-forward's own in-sample optimization (the
folds' chosen-param train profit, summed) came out net-negative or zero, even though the ORIGINAL
single-window run that won the pre-registration looked strong. This is exactly the failure mode a
walk-forward gate exists to catch:
- **s2c** already showed the warning sign before walk-forward ran — its consistency dropped from
  100 to 50 and DD roughly doubled even as ExpectancyR looked spectacular (+221%). The OOS data
  confirms that gain doesn't hold: only 3/6 test windows won, with 3 sizeable losses.
- **s2j** is the more surprising case — 4/6 winning test windows and the largest cumulative test
  PnL of the three ($8,908), driven by two very strong windows (w1 +$7,056, w5 +$3,395) covering
  weak middle windows (w2 -$2,662, w3 -$2,721). A naive "5/6 win-window" read (the informal
  heuristic used for R3.2's first batch, before F62 existed) would have called this a clear
  survivor. The formal OOS ratio disagrees, because the fold-by-fold IN-SAMPLE optimization never
  found a net-positive parameter set to begin with — the test-window wins came from parameter
  choices that hadn't earned their place in-sample. This is the concrete case that justifies
  building F62 instead of eyeballing win-window counts.

**s2l is the strongest surviving candidate from either session**: 6/6 test windows profitable,
OOS ratio 1.58 (test outperformed train), composite 98.3 full `sv1`. Combined with `conservative`
risk's clean multi-dimensional win in the pre-walk-forward comparison, this is the best-evidenced
single result of the whole R3 program so far.

Gate re-verified: 3 walk-forward jobs, 18 WindowResults rows (matching the plan's `3×6` truth-gate
shape), 2 `StrategyCellParks` rows created with reasons.
