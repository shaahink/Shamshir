# R4 — FTMO Dress Rehearsal — Candidate Cards

**Date:** 2026-07-15
**Embargo window (first and only touch):** 2026-05-06 → 2026-07-05 (60 calendar days), tape venue.
**Config for every run:** governor ON (default), prop-rule set ON (`ftmo-standard` via each
candidate's risk profile), exploration OFF (default), HonestFills default (true). No knob was
re-tuned after seeing embargo results — this is the plan's "first and only touch," not a search.

**Owner call executed:** the R3-close handoff flagged #3/#4 as the same cell
(`ema-alignment/EURJPY/H1`) under two knobs and left "test both or pick one" open. Both were run —
the marginal cost is one extra tape run + one extra challenge-sim call, and comparing how the same
edge responds to 4× risk scaling under real prop constraints is exactly what a dress rehearsal is
for.

## Headline result

**0 of 4 candidates reached a 30-day +10% challenge target in any of the 3 rolling windows. 0 of
12 windows breached the 5% daily or 10% max-loss limits either.** That is not "all failed" in the
FTMO sense (a breach) — it is "too slow," a different and arguably worse finding: every candidate
that scored 90–100/100 on the full-year census stalled to near-flat or net-negative on this
specific unseen 60-day slice. Two of the four (both `ema-alignment/EURJPY/H1` variants) are net
**negative** on the embargo window. Per the plan's own instruction, this is reported as-is — the
embargo window is not being re-touched or reinterpreted to look better.

**Trade counts on the embargo window are thin (3–7 trades over 60 days).** Every number below is
real and traceable to a RunId, but treat the individual verdicts as directional, not statistically
conclusive — a 60-day slice this sparse can look this way from ordinary variance alone, not
necessarily because the full-year edge is fake. That said, "the edge might still be real" is not
the same as "ready for a live challenge," and the data as measured says no candidate is.

## Method

- **Challenge-sim windows** are NOT the existing `SetupScoreService.ComputeFtmoSurvival` component
  (that function is a crude equity-drawdown proxy — see **F63** below, filed, not fixed, this
  session). They are a new, separate mechanism built for R4:
  `ChallengeSimulator.SimulateWindow` (`src/TradingEngine.Risk/Compliance/ChallengeSimulator.cs`) +
  `ChallengeSimulationService` (`src/TradingEngine.Web/Services/ChallengeSimulationService.cs`),
  exposed at `GET /api/runs/{runId}/challenge-sim?windows=3&windowDays=30`. 7 unit tests
  (`ChallengeSimulatorTests.cs`) + 7 integration tests (`ChallengeSimulationServiceTests.cs`) pin
  the FTMO-standard semantics (target/daily-cap/max-cap/min-trading-days) and the daily-bucketing
  edge case (a flat multi-day stretch must not collapse into one bucket).
- Each rolling window replays the account's REAL historical daily-equity sequence day-by-day from a
  fresh $-equity baseline at the window's own start — not a Monte Carlo resample (that's what
  `PassProbabilityEstimator` already does for a different question, forward risk projection). This
  answers "if a challenge had started here, using what actually happened next, would it have
  passed" — the correct question for a backward-looking dress rehearsal.
- 3 windows are spread evenly across each run's available trading-day buckets (start offsets
  `0, maxStart/2, maxStart` for a 30-day window), so they overlap but sample the start, middle, and
  end of the embargo period.
- `Verdict`: **Pass** = reached +10% of window-start equity with ≥4 trading days elapsed; **Fail**
  = breached the 5%-of-window-start daily cap or the 10% fixed max-loss floor; **Incomplete** =
  neither happened within the 30-day window (this is what every window below resolved to).

## Candidates

### #1 — trend-breakout / XAUUSD / H4 + pack=`runner-aggressive`

| | |
|---|---|
| Full-year score (2025-07-04→2026-05-05) | Composite **100**, OOS ratio **2.15** (RunId `9c98ce41`, 63 trades, +$12,450) |
| Embargo run | RunId **`e319c25a`** — 3 trades, 2 winners, NetPnL **+$401.42**, MaxDD 0.15%, 0 warnings |
| Challenge-sim (3× 30-day windows) | All **Incomplete**. Trading days used: 2, 2, 1. Worst daily loss: 0.34%. Final return at window end: +0.40%, +0.40%, −0.49% |
| Extrapolated days-to-target | ~1,495 days at the embargo run's realized $/day rate (essentially never, in practice) |
| Data note | XAUUSD/H4 market data ends 2026-07-03 13:00 (2 days short of the embargo's 07-05 end) — immaterial given only 3 trades total; noted for completeness |

### #2 — mean-reversion / AUDUSD / H1 + risk=`conservative`

| | |
|---|---|
| Full-year score | Composite **98.3**, OOS ratio **1.58** (RunId `baf739ad`, 31 trades, +$4,892) |
| Embargo run | RunId **`fdf0ae70`** — 4 trades, 2 winners, NetPnL **+$108.61**, MaxDD 0.15%, 0 warnings |
| Challenge-sim | All **Incomplete**. Trading days used: 2, 2, 1. Worst daily loss: 0.53%. Final return: +0.64%, +0.64%, −0.53% |
| Extrapolated days-to-target | ~5,525 days at realized rate |

### #3 — ema-alignment / EURJPY / H1 + pack=`runner-aggressive`

| | |
|---|---|
| Full-year score | Composite **97.3**, OOS ratio **4.32** (RunId `38b4d82f`, 58 trades, +$5,341) |
| Embargo run | RunId **`94090b6f`** — 6 trades, **1** winner, NetPnL **−$1,920.70**, MaxDD 1.92%, 0 warnings |
| Challenge-sim | All **Incomplete**. Trading days used: 4, 3, 1. Worst daily loss: 0.51%. Final return: **−1.44%, −1.42%, −0.49%** |
| Extrapolated days-to-target | N/A — net negative on the embargo window |

### #4 — ema-alignment / EURJPY / H1 + risk=`aggressive`

| | |
|---|---|
| Full-year score | Composite **90.3**, OOS ratio **3.23** (RunId `6d8c8fa0`, 39 trades, +$13,110) |
| Embargo run | RunId **`0dc27c9f`** — 7 trades, 3 winners, NetPnL **−$810.38**, MaxDD 2.07%, 0 warnings |
| Challenge-sim | All **Incomplete**. Trading days used: 1, 4, 5. Worst daily loss: **1.47%** (the single worst day of any candidate, still well inside the 5% cap). Final return: −1.76%, −0.01%, +0.97% |
| Extrapolated days-to-target | N/A — net negative on the embargo window |

**#3 vs #4 (same cell, two knobs):** 4× risk scaling turned a −$1,921 embargo result into −$810 (less
bad, not good) while more than doubling the worst single-day loss (0.51%→1.47%) and the full-year
MaxDD (0.75%→1.50%, both still comfortably under the 10% ceiling). Scaling risk did not fix the
underlying return-velocity problem on this window — both variants of this cell are negative here.
Neither is a stronger R4 carry than the other; if forced to choose one, #3 (standard risk) has the
better full-year OOS ratio (4.32 vs 3.23) and the smaller embargo loss magnitude is not a
meaningful tiebreaker given n=6 vs n=7 trades.

## F63 (filed, not fixed this session) — `SetupScoreService.ComputeFtmoSurvival` is a placeholder

The FtmoSurvival score component (25% weight in every R1'/R3 composite, including all 4 candidates
above showing `FtmoSurvival: 100`) is explicitly commented as a placeholder: it checks only whether
equity ever dipped >10% from its own start within naive 30-day *stage* slices of the full-year run
— no profit-target check, no ruleset-specific daily-loss cap, no min-trading-days gate. It has never
been a real challenge simulation. `ChallengeSimulator`/`ChallengeSimulationService` (built this
session) is the correct replacement, but wiring it into `SetupScoreService` would retroactively
rescore all ~250 R1'/R3 `ExperimentRuns` — a separate, larger undertaking outside R4's scope.
Flagging now because this session's embargo results suggest the current FtmoSurvival=100 readings
across the census may be systematically overstated for low-frequency cells; a future session should
decide whether to rescore.

## Bottom line for the owner

All 4 candidates are **safe** (no daily/max-loss breach in 12/12 rolling windows, worst single day
1.47%) but **not currently challenge-ready** by return velocity — 0/12 windows reached +10% in 30
days, and 2 of 4 are net negative on the one unseen window they've ever touched. This is the
deliverable, not a setback to iterate away: the embargo window exists precisely to catch
full-year-census survivors that don't generalize to fresh time, and it caught something. Before any
of these four goes near real capital, the honest next step is more embargo-style evidence (a
longer or second unseen window, once available) or a search for higher-trade-frequency variants of
the same theses — not re-tuning against this window, which the plan explicitly forbids.
