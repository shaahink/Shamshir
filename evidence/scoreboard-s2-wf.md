# R3.2 — walk-forward raw results (v6a, v1a, v6b)

Walk-forward jobs run against the 3 best R3.1 variants, 6 folds each (train 2:1 ratio via
`TrainFraction=0.7`, actual per-fold span ~35d train / ~15d test — see Caveats), window
2025-07-04 → 2026-05-05 (same non-embargoed range as the baseline; embargo starts 2026-05-06,
untouched per D12/R4).

**Code change required to get here:** `WalkForwardSpec`/`WalkForwardRequest` had no way to carry a
`PackId` or `RiskProfileId` into the train/test windows — every walk-forward silently reverted to
each strategy's default config, which would have validated the WRONG variant. Added optional
`PackId`/`RiskProfileId` fields threaded through `BuildSweepRequest` (train) and
`RunTestWindowAsync` (test) via the same `CustomParams["UsePackId"]`/`["RiskProfileId"]` mechanism
already used everywhere else. Verified the override actually reaches real execution (not just the
request) by inspecting `TradeResults` directly for a partial-close signature (`PartialTp` is
`runner-aggressive`-only) — found it, confirming the fix works. Full trace in `LEDGER.md`.

## v6a — ema-alignment/EURJPY/H1 + runner-aggressive (job `07adfa7a`)

| Window | Train | Test | Chosen fastPeriod | Test NetPnL | Test trades | Test win rate |
|---|---|---|---|---|---|---|
| 0 | 07-04→08-07 | 08-08→08-23 | 15 | +1,966.68 | 10 | 80.0% |
| 1 | 08-24→09-27 | 09-28→10-13 | 15 | +1,708.42 | 7 | 85.7% |
| 2 | 10-14→11-17 | 11-18→12-03 | 15 | +2,022.41 | 11 | 72.7% |
| 3 | 12-04→01-07 | 01-08→01-23 | 15 | +1,100.83 | 11 | 72.7% |
| 4 | 01-24→02-27 | 02-28→03-15 | 15 | **-1,530.56** | 14 | 42.9% |
| 5 | 03-16→04-19 | 04-20→05-05 | 15 | +2,414.56 | 21 | 66.7% |

5/6 test windows profitable. Cumulative test NetPnL: **+$7,682.34**.

## v1a — trend-breakout/XAUUSD/H4 + runner-aggressive (job `84d1e232`)

| Window | Train | Test | Chosen lookbackBars | Test NetPnL | Test trades | Test win rate |
|---|---|---|---|---|---|---|
| 0 | 07-04→08-07 | 08-08→08-23 | 15 | +2,515.39 | 17 | 58.8% |
| 1 | 08-24→09-27 | 09-28→10-13 | 15 | +4,509.34 | 28 | 67.9% |
| 2 | 10-14→11-17 | 11-18→12-03 | 15 | +5,315.63 | 16 | 81.3% |
| 3 | 12-04→01-07 | 01-08→01-23 | 15 | +1,488.48 | 16 | 62.5% |
| 4 | 01-24→02-27 | 02-28→03-15 | 15 | +280.40 | 3 | 66.7% |
| 5 | 03-16→04-19 | 04-20→05-05 | 15 | 0 | **0** | — |

5/5 active test windows profitable (window 5 produced zero trades — not a loss, but worth a look:
either the chosen fastest-lookback params found nothing in that specific 16-day window, or a
data-coverage edge effect near the window boundary; not investigated further). Cumulative test
NetPnL: **+$14,109.24**.

## v6b — ema-alignment/EURJPY/H1 + aggressive risk (job `44d93952`)

| Window | Train | Test | Chosen fastPeriod | Test NetPnL | Test trades | Test win rate |
|---|---|---|---|---|---|---|
| 0 | 07-04→08-07 | 08-08→08-23 | 15 | +4,413.15 | 6 | 66.7% |
| 1 | 08-24→09-27 | 09-28→10-13 | 15 | +3,296.61 | 4 | 75.0% |
| 2 | 10-14→11-17 | 11-18→12-03 | 15 | +5,821.77 | 7 | 57.1% |
| 3 | 12-04→01-07 | 01-08→01-23 | 15 | +2,191.99 | 7 | 57.1% |
| 4 | 01-24→02-27 | 02-28→03-15 | 15 | **-2,533.75** | 9 | 22.2% |
| 5 | 03-16→04-19 | 04-20→05-05 | 15 | +5,025.67 | 14 | 50.0% |

5/6 test windows profitable — **the same window (4, Feb 28–Mar 15) that lost for v6a also lost
here**, consistent with both being the same underlying strategy/symbol/signal timing, just scaled
differently (4x risk turned a -$1,530 loss into -$2,534 — roughly 1.65x, not the full 4x, since
losses aren't purely linear in position size once SL/exit timing differs). Cumulative test
NetPnL: **+$18,215.44**.

## Reading this honestly — what this is and isn't

**All 3 candidates: 5 of 6 test windows profitable, positive cumulative out-of-sample PnL.** That's
a genuinely encouraging directional read — these aren't curve-fit train-only results falling apart
out of sample.

**This is NOT the plan's formal "OOS ratio < 0.5 → park" cull, because that scoring path does not
exist yet.** `SetupScoreService.ScoreRunAsync` hardcodes `oosRatio = null` unconditionally (line
105, comment: *"OOS robustness: null until walk-forward runs in R3"*) — this is R3, and it was
never wired up. `VersionKind` can therefore never become full `sv1`, only `sv1-partial`, regardless
of how much walk-forward data exists. This is a real, pre-existing gap in the scoring pipeline, not
something broken by this session's work — flagged as **F62**.

**The fix is scoped, not started from zero**: `WalkForwardWindowResultEntity` already stores
`PlateauValue` (the winning train-window cell's net profit) alongside `TestNetProfit` — a real
Walk-Forward Efficiency ratio (`sum(TestNetProfit) / sum(PlateauValue)`, or a per-fold average) is
computable from data already being persisted. It needs: a query from `ScoreRunAsync` matching
`WalkForwardWindowResultEntity` rows to the run's (StrategyId, Symbol, Timeframe, PackId/RiskProfileId),
a decision on the exact ratio formula and how to aggregate across folds, and threading the result
into the existing `oosRatio` variable. Not done this session — deliberately not rushed in as a
tail-end addition to an already-large session.

**Also flagged, harmless: F61** — `RunConfigAssembler.ResolveEffectiveConfigJsonAsync` (the method
that produces the `effectiveConfigJson` shown via `GET /api/runs/{id}`) never applies
`UsePackId`/`PerStrategyPackIds`/`StripAddOns`/per-row packs — it only stamps the risk profile.
This makes the API's displayed "what actually ran" wrong whenever a pack was used via the legacy
`UsePackId` path (row-based `Rows[].PackId` wasn't checked, may share the bug — not verified).
Confirmed via `TradeResults` inspection that REAL EXECUTION is correct (a `runner-aggressive` run
showed the `PartialTp` two-rows-per-position signature) — this is a display/audit-trail bug only,
does not affect trading behavior, scores, or this session's results.
