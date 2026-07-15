# R1' Baseline Sweep -- SetupScore v1 scoreboard (baseline-sv1-prime)

- Experiment: `075d5240-94c4-4e15-bc99-a52a3606d19a` (baseline-sv1-prime)
- Window: 2025-07-04 -> 2026-05-05, tape venue, defaults, one cell per run (D13)
- Grid: 9 strategies x 14 symbols x {H1,H4} = 252 cells
- Coverage: 252/252 ExperimentRuns persisted (100%), 74 scored, 178 null-with-reason
- Gate: >=90% scored-or-null AND ExperimentRuns >= 225 -> **PASS**
- Full census: `evidence/scoreboard-s1p.csv`
- **Rescored 2026-07-15 after the F60 fix** (see Caveats) -- ranking unchanged in the top 20; 18/74
  cells shifted by more than 0.05 points, largest -4.1 (`mtf-trend/BTCUSD/H1`, 47.1 -> 43.0).

## Top 20 scored cells

| # | Cell | Score | Trades | ExpectancyR | MaxDD% | Consistency | RunId | Version |
|---|------|-------|--------|-------------|--------|-------------|-------|---------|
| 1 | trend-breakout/XAUUSD/H4 | 100 | 39 | 0.689 | 0.03 | 100 | 22ca21af | sv1-partial |
| 2 | mean-reversion/GBPUSD/H1 | 96.5 | 32 | 0.519 | 0.28 | 80 | 87cf92c4 | sv1-partial |
| 3 | mean-reversion/AUDUSD/H1 | 96.1 | 33 | 0.693 | 0.09 | 77.8 | 6a8b6459 | sv1-partial |
| 4 | rsi-divergence/AUDUSD/H1 | 92 | 47 | 0.642 | 1.94 | 54.5 | 9c94189b | sv1-partial |
| 5 | mean-reversion/GBPJPY/H1 | 90.1 | 22 | 0.467 | 0.09 | 57.1 | 0157f3b4 | sv1-partial |
| 6 | ema-alignment/EURJPY/H1 | 88.6 | 39 | 0.384 | 0.75 | 81.8 | 05bcfae6 | sv1-partial |
| 7 | mtf-trend/EURJPY/H1 | 87.6 | 31 | 0.387 | 1.36 | 75 | 2f08712c | sv1-partial |
| 8 | trend-breakout/NZDUSD/H4 | 83.7 | 43 | 0.353 | 0.01 | 66.7 | 624050f5 | sv1-partial |
| 9 | trend-breakout/ETHUSD/H4 | 82.7 | 48 | 0.326 | 0.31 | 71.4 | 6e92f06b | sv1-partial |
| 10 | bb-squeeze/XAGUSD/H4 | 81.2 | 42 | 0.347 | 0.15 | 54.5 | 1e65b5a7 | sv1-partial |
| 11 | rsi-divergence/BTCUSD/H1 | 80.8 | 56 | 0.311 | 0.59 | 66.7 | 15148cae | sv1-partial |
| 12 | macd-momentum/XAGUSD/H1 | 78.4 | 92 | 0.352 | 3.63 | 45.5 | b9d4766e | sv1-partial |
| 13 | mean-reversion/GBPUSD/H4 | 77.1 | 21 | 0.247 | 1.49 | 71.4 | c4c3e584 | sv1-partial |
| 14 | trend-breakout/XAGUSD/H1 | 76.8 | 35 | 0.172 | 0.96 | 100 | a314e6a3 | sv1-partial |
| 15 | super-trend/AUDUSD/H1 | 74.4 | 34 | 0.263 | 0.54 | 50 | 0c819ec1 | sv1-partial |
| 16 | mean-reversion/XAUUSD/H1 | 73.3 | 30 | 0.222 | 0.2 | 60 | 608c6f28 | sv1-partial |
| 17 | bb-squeeze/USDCAD/H4 | 72.9 | 44 | 0.216 | 1.68 | 60 | 5baad5d4 | sv1-partial |
| 18 | super-trend/NZDUSD/H1 | 72.7 | 34 | 0.238 | 1.96 | 50 | dfa5bac9 | sv1-partial |
| 19 | trend-breakout/USDCAD/H4 | 72 | 37 | 0.204 | 0.04 | 60 | bd447de6 | sv1-partial |
| 20 | trend-breakout/AUDUSD/H1 | 71.3 | 25 | 0.0929 | 0.36 | 100 | 6f44bf68 | sv1-partial |

## Null-reason breakdown

| Reason (normalized) | Cells |
|---------------------|-------|
| trades=N below floor 20 (D3) | 178 |

## Caveats (read before acting on the ranking)

- **F60 FIXED (2026-07-15) and the 252 cells rescored.** `BacktestRuns.MaxDrawdownPct` stores a
  FRACTION (proof: run `18621a31` is net-losing -$376 yet shows 0.0140, impossible as a percent,
  consistent as 1.40%); `ComputeDrawdownScore` and the persisted `DrawdownPct` component now
  multiply by 100 before scoring/storing, so the MaxDD% column above is a real percent (no more
  "multiply by 100 to read" caveat) and the drawdown component actually discriminates. Ranking
  order in the top 20 is unchanged (these cells all had genuinely low drawdown already); 18/74
  scored cells shifted by > 0.05 points elsewhere in the full census, largest -4.1.
- **All scores are `sv1-partial`:** no OOS/robustness component until R3 walk-forward; FTMO survival
  is the R0 placeholder approximation (rolling 30-day 10% stage check), not the governor-rule sim.
- **F48 (open):** XAUUSD-class PnL conversion diverges ~1.4% tape-vs-venue. This census ranks
  tape-vs-tape, so it biases levels, not ordering.
- One cell (`mean-reversion/USDCHF/H1`, run `18621a31`) was first scored during the finalize race
  (persisted status still 'running') and re-scored after terminal -- the null->scored upsert worked
  as designed (D13).
