# R3 session 1 — variant scoreboard (baseline-sv1-prime cells, one knob each)

- Pre-registration: `docs/iterations/iter-alpha-loop/LEDGER.md` §"R3.1 — session 1 — pre-registration"
- 6 cells from the R1' top-20, 12 variants (pack swap or risk-profile swap, one at a time),
  same window as baseline (2025-07-04 -> 2026-05-05), tape venue.
- Coverage: 12/12 ExperimentRuns persisted (100%), 11 scored, 1 null-with-reason (v2b, below the
  20-trade floor). Truth gate PASS (§R3: ExperimentRuns grew by exactly 12; every pre-registered
  variant has a result).

**Read composite score with care.** `Expectancy`/`Drawdown`/`Consistency` are individually capped
at 100 inside the composite, so two variants both scoring 100 can hide a real difference in raw
edge. Compare `ExpectancyR` (uncapped) for the real signal, composite for the ranking.

## Results vs. each cell's own R1' baseline

| Variant | Cell | Knob | Composite | ExpectancyR | DD% | Consistency | Trades | Verdict |
|---|---|---|---|---|---|---|---|---|
| **baseline** | trend-breakout/XAUUSD/H4 | (standard) | 100 | 0.689 | 0.03 | 100 | 39 | — |
| v1a | " | pack=runner-aggressive | 100 | **0.912** | 0.03 | 100 | 63 | **WIN** — raw edge +32%, DD/consistency held |
| v1b | " | risk=aggressive | 95 | 0.566 | 0.07 | 71.4 | 44 | edge degrades ~18% at 4x size |
| **baseline** | rsi-divergence/AUDUSD/H1 | (standard) | 92.0 | 0.642 | 1.94 | 54.5 | 47 | — |
| v2a | " | pack=scalp-tight | 58.7 | **0.040** | 0.13 | 50 | 26 | **LOSS** — edge nearly wiped out (-94%) |
| v2b | " | risk=conservative | — | — | — | — | 14 | **FAIL** — fell below 20-trade floor (D3) |
| **baseline** | macd-momentum/XAGUSD/H1 | (standard) | 78.4 | 0.352 | 3.63 | 45.5 | 92 | — |
| v3a | " | pack=scalp-tight | 58.8 | **-0.008** | 0.74 | 66.7 | 48 | **LOSS** — edge goes negative; consistency up but net negative |
| v3b | " | risk=conservative | 60 | 0.089 | 1.72 | 37.5 | 56 | DD cut 53%, but edge cut 75% — not risk-adjusted-positive |
| **baseline** | rsi-divergence/BTCUSD/H1 | (standard) | 80.8 | 0.311 | 0.59 | 66.7 | 56 | — |
| v4a | " | pack=runner-aggressive | 74.1 | 0.234 | 1.17 | 60 | 74 | edge down 25%, DD up 2x — ride doesn't help here |
| v4b | " | risk=aggressive | 75.8 | 0.300 | 0.85 | 42.9 | 23 | edge ~flat, but trade count collapsed 56→23 (unexplained, see caveat) |
| **baseline** | bb-squeeze/XAGUSD/H4 | (standard) | 81.2 | 0.347 | 0.15 | 54.5 | 42 | — |
| v5a | " | pack=scalp-tight | 64.6 | 0.089 | 0.15 | 63.6 | 35 | edge down 74%; consistency up slightly |
| v5b | " | risk=conservative | 75.1 | 0.303 | 0.08 | 37.5 | 27 | DD nearly halved for only 13% edge cost — mild risk-adjusted win |
| **baseline** | ema-alignment/EURJPY/H1 | (standard) | 88.6 | 0.384 | 0.75 | 81.8 | 39 | — |
| v6a | " | pack=runner-aggressive | 96.8 | **0.698** | 0.75 | 81.8 | 58 | **BIGGEST WIN** — edge +82%, DD/consistency held exactly |
| v6b | " | risk=aggressive | 88.6 | 0.384 | 1.50 | 81.8 | 39 | edge and consistency IDENTICAL at 4x size — scale-invariant |

## Reading the results

**Two real wins, both `runner-aggressive` (BE + relaxing ATR trail + 50%-partial at 1R):**
`ema-alignment/EURJPY/H1` (v6a, +82% raw edge, DD/consistency untouched) and
`trend-breakout/XAUUSD/H4` (v1a, +32% raw edge, DD/consistency untouched). Both are trend-style
strategies — letting winners run fits the strategy's own thesis, and the DD budget wasn't being
spent under the baseline SL/TP anyway.

**`scalp-tight` (early BE + tight step trail) lost on every cell it was tried on** (v2a, v3a, v5a)
— sometimes badly (rsi-divergence/AUDUSD/H1 lost 94% of its raw edge; macd-momentum/XAGUSD/H1
went net-negative). All three of these strategies' theses depend on the trade having room to
develop (RSI divergence resolving, MACD momentum continuing) — a tight trail cuts them off before
the thesis plays out. Consistent, not cherry-picked: 3/3 tight-trail variants regressed.

**`aggressive` risk profile (4x standard) is scale-neutral on one cell, degrading on two:**
`ema-alignment/EURJPY/H1` (v6b) held its edge and consistency exactly flat at 4x size — a genuine
scale-invariance result, useful for R4 sizing. `trend-breakout/XAUUSD/H4` (v1b) and
`rsi-divergence/BTCUSD/H1` (v4b) both degraded at scale (consistency and/or trade count fell).

**`conservative` risk profile did not produce a clean risk-adjusted win anywhere it was tried.**
It cut DD substantially in both cases (macd-momentum -53%, bb-squeeze -48%), but cut expectancy by
more (macd-momentum -75%) or fell below the trade floor entirely (rsi-divergence/AUDUSD/H1, v2b).
Only bb-squeeze (v5b) came out plausibly ahead on a risk-adjusted basis.

## Caveats

- **Trade-count shifts are not all the same phenomenon.** `runner-aggressive`'s trade-count jumps
  (v1a 39→63, v6a 39→58, v4a 56→74) are explained by its `PartialTp` add-on: a partial close plus
  the final close both post as separate `TradeResult` rows, so `ExpectancyR`'s per-trade average
  blends full-size and half-size closes for these three variants — read the composite/edge
  direction, not the raw trade count, as the comparable number. `scalp-tight`'s trade-count *drops*
  (v2a 47→26, v3a 92→48, v5a 42→35) are a different, unexplained mechanism — not chased further
  this session; worth a dedicated look if `scalp-tight` resurfaces as a candidate.
- **v4b's trade collapse (56→23) under `aggressive` risk is unexplained** — plausibly the
  larger per-trade risk hits the profile's daily/total drawdown circuit breakers sooner on a
  volatile symbol (BTCUSD), engaging protection mode and skipping subsequent signals, but this is
  a hypothesis, not confirmed by log inspection. Flagging rather than asserting.
- All scores are `sv1-partial` — no OOS/robustness component yet (that's what walk-forward adds).

## Next: walk-forward the best 3

Per plan (§R3 step 4), walk-forward the session's best 3 (6 rolling windows, train 60d / test
30d) to upgrade them to full `sv1` and get a real OOS ratio before treating any of this as a
candidate:

1. **v6a** — ema-alignment/EURJPY/H1 + runner-aggressive (biggest genuine edge gain)
2. **v1a** — trend-breakout/XAUUSD/H4 + runner-aggressive (edge gain on the #1 cell)
3. **v6b** — ema-alignment/EURJPY/H1 + aggressive risk (validates the v6a edge holds at real size)

Blocked on: each strategy's tunable indicator `ParamGrid` for `POST /api/walk-forward/start` has
not been looked up yet (separate lookup, not covered by this session's knob-inventory research).
