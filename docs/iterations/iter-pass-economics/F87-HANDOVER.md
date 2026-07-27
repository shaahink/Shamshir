# F87 — Cost Truth: HANDOVER (Lane D session, 2026-07-27)

**For the Lane R session that ratifies GE1.** Plan executed: `F87-COST-TRUTH-PLAN.md` P0–P6,
all phases committed on `iter/pass-economics-f87`. Every claim below has a commit, a test, or a
verbatim query output behind it — re-verify outputs, not assertions.

## Commits

| Phase | Commit | Subject |
|---|---|---|
| P0 | (no commit — inventory below, baseline green) | recon |
| P1 | `2ae2c11` | nullable SpreadPips end-to-end, null = recorded per-bar spread |
| P2 | `233a251` | nullable CommissionPerMillion, null = venue-true CommissionType dispatch |
| P3 | `c3d1094` | record per-trade SpreadCostAmount (M55) |
| P4 | `2ea6722` | single spread-number resolver across fills / floating PnL / strategy tick |
| P5 | `1e4f017` | cost_decomposition.py — standing harvest columns + selftest |
| P6 | (this commit) | GE1 probe evidence + handover |

## Gate counts

| Point | Unit | Integration | Sim-fast |
|---|---|---|---|
| Baseline (plan said 805; actual post-E1 main) | 816/0/6 | 156/0/0 | 144/0/0 |
| After P1 | 818/0/6 | 159/0/0 | 144/0/0 |
| After P2 | 820/0/6 | 161/0/0 | 144/0/0 |
| After P3 | 823/0/6 | 161/0/0 | 144/0/0 |
| After P4 (final) | 828/0/6 | 161/0/0 | 144/0/0 |

No failures at any point; goldens byte-identical through P4 (golden fixtures carry no per-bar
spread, so the registry fallback path — behaviour-identical to before — is what they exercise).

## P0 inventory (grep `SpreadPips|CommissionPerMillion`, all accounted for)

| Site | Disposition |
|---|---|
| `StartRunRequest.cs` | both `double?`, default null (P1/P2) |
| `BacktestConfig.cs` (CTraderRunner) | both `double?`, default null (P1/P2) |
| `ReplayVenueRunner.cs:226-236` | unchanged — `(decimal?)` casts pass null through |
| `CTraderVenueRunner.cs` | R4 fail-fast backstop for null spread OR commission |
| `BacktestRunner.cs` / `BacktestCli.cs` | BuildArgs throws on null (R4); `BacktestCliRequest` stays non-nullable (explicit transport type, E2E harness) |
| `SweepRunnerService.cs` | `SweepRequest` both nullable, default null (tape-only research path) |
| `BlockBootstrapController.cs` | `?? 30` / `?? 1` coalesces REMOVED — null passes through (tape-only) |
| `BacktestOrchestrator.cs:126-127,688-689` | `(double)` casts removed; state fields nullable |
| `BacktestRunState.cs` | both `double?` |
| `RunRecordStore.cs` / `IBacktestRunRepository.cs` (`BacktestRunSummary`) | both `double?`, default null |
| `SqliteBacktestRunRepository.cs` / `BacktestRunEntity.cs` | both `double?` (M54) |
| `RunDetailQuery.cs` / `RunDetailResponse.cs` | nullable pass-through |
| `RunsController.cs` | R4 validation (start + duplicate endpoints) |
| `CtraderListenConfig.cs` / `CTraderListenService.cs` | UNCHANGED — keeps explicit 50/1 (drives the cTrader venue, R4) |
| SPA: `api.types.ts`, `new-backtest.component.ts`, `run-report.component.ts`, `runs.service.ts` | blank-default fields (placeholders "venue-true (captured)" / "per-bar (recorded)"), null copies through duplicate/prefill/saved-setups, report chip renders "venue-true"/"per-bar"; `ctrader-sessions.component.ts` keeps explicit numbers |
| `UnitConversion` / `AddOnAutoTuner` / `SpreadVolNoTradeFilter` / `ExitLab` / `EntryFilterOptions` | different concept (TypicalSpread-derived pips / exit-lab replay spread) — out of scope, untouched |

## Migrations — R7 deviation (deliberate, flagged)

R7 named the P3 migration `M54_TradeSpreadCost`. P1's persistence work needed a schema change
the plan didn't budget for (nullable `BacktestRuns.SpreadPips`/`CommissionPerMillion` — a
sentinel like −1 is banned by the plan's own "sentinel-free" language, so NULL columns it is).
Result:

- **M54_NullableRunCostOverrides** (P1): both BacktestRuns columns → nullable. SQLite ALTER =
  **table rebuild of BacktestRuns**, which EF executes OUTSIDE a transaction (it warns loudly).
  BacktestRuns is only run headers (hundreds of rows) — the window is milliseconds — but this is
  the one non-transactional step. It has already been applied to the real trading.db (see P6).
- **M55_TradeSpreadCost** (P3): `TradeResults.SpreadCostAmount` REAL NOT NULL DEFAULT 0 — O(1)
  ADD COLUMN, no rewrite of the 221k-row table.

Both were proven against the REAL schema before the app ever touched it: schema-only clone of
trading.db (55 DDL objects + `__EFMigrationsHistory` at M53) → `dotnet ef database update`
→ history at M55, `SpreadPips`/`CommissionPerMillion` notnull=0, `SpreadCostAmount` default 0.

## SpreadCostAmount semantics (P3)

- NEGATIVE (R2). **Not part of the Net identity** — spread lives inside Gross via fill prices.
  `SignalPnL ≡ GrossPnLAmount − SpreadCostAmount` is the bid-to-bid PnL.
- Exactly one crossing per round trip in this venue's model (P0.2/D3): **Long pays at entry**
  (spread recorded on `OpenTrade.EntrySpread` at the fill), **Short pays at exit** at the spread
  the exit fill actually applied. Same `PipValuePerLot`-at-exit as Gross ⇒ identity exact.
- Partial closes prorate by leg lots (same shape as EntryCommission proration).
- 0 for: `BacktestReplayAdapter` (not the research path), cTrader venue (cBot reports its own
  costs), journal backfill (journal doesn't carry it), and every pre-F87 row.

## Convention doubts / flags for Lane R (R3 — observed, NOT changed)

1. **`ClosePartialPositionAsync` fills at raw `_lastClose` for BOTH directions** — a short's
   partial exit crosses no spread on tape. Pre-existing convention; SpreadCost records 0 for
   those legs accordingly.
2. **`ClosePositionAtAsync` (engine-priced close)**: the venue applies no spread of its own, so
   a short closed via this rare path records SpreadCost 0 (under-report, keeps the Signal
   identity exact w.r.t. prices actually used). Longs are unaffected (entry spread recorded).
3. **R1 tension in P4 (flagged, deliberate)**: floating PnL previously read `TypicalSpread`
   even when a run had an explicit override; it now reads `GetSpread()` (override → per-bar →
   registry). Fills are byte-identical for explicit-value runs, but open-trade equity /
   intrabar DD watermarks for override runs now use the override number instead of
   TypicalSpread. This is the "one number" goal of P4; if Lane R reads R1 strictly it can be
   reverted to `SpreadResolver.FullSpread(_currentSpread, …)` (per-bar → registry, skipping the
   override) in one line at `TapeReplayAdapter.ComputeFloatingPnL`.
4. **Host tick sites**: `BarClosed` gained optional `Spread` (carried from the venue bar) so the
   kernel-path evaluators can see the recorded spread; half-spread offset convention untouched
   at all four sites; per-site historical fallbacks preserved (0.0001 full in Host,
   0.001 full in `HistoricalDataProvider`). The site-local "ResolveHalfSpread failed" warning
   logs were absorbed by the resolver's silent fallback (registry-miss on a trading symbol is
   effectively impossible in a wired run).
5. **Account currency is EUR** (`appsettings.Development.json` `Engine:Currency=EUR`, F34) —
   every money column of the probe is EUR. Anyone eyeballing SpreadCost against a $10/pip
   EURUSD intuition will see a ~1/EURUSD factor; the per-row identity (below) is the correct
   lens.

## P5 — cost_decomposition.py

`python tools/research/cost_decomposition.py --selftest` (verbatim):

```
(a) run selection: prefix=['run1', 'run2'] name=['run1', 'run2']
(b) run1: n=3 gross=1470.00 spread=-93.00 net=1415.76 signal=1563.00
(b) identities: net==gross+comm+swap OK, violations=0
(c) per-cell: session-breakout/EURUSD n=4 ema-alignment/XAUUSD n=1
(d) violation accounting: run3 violations=1
(e) R2 conventions: spread_cost<=0 True, commission<=0 True
SELFTEST PASS
```

## P6 — GE1 probe evidence (run `09c2eb9b`)

Launched the app (which applied M54+M55 to the real trading.db: `__EFMigrationsHistory` last
two rows = `20260727214151_M55_TradeSpreadCost`, `20260727212721_M54_NullableRunCostOverrides`),
then started ONE tape run via `POST /api/runs`:

```json
{"symbols":["EURUSD"],"periods":["H1"],"start":"2023-01-01","end":"2023-03-31",
 "balance":100000,"venue":"tape","strategyIds":["session-breakout"],
 "governorEnabled":false,"maxDdEnabled":false,"disableRegime":true,
 "idempotencyKey":"f87-p6-ge1-probe-1"}
```

(`spreadPips`/`commissionPerMillion` omitted ⇒ null.) Response:
`{"runId":"09c2eb9b","status":"starting","queuePosition":null}` → completed, 68 trades.
Market data: EURUSD M1 Q1-2023 = 92,966 bars, **0 null-spread** (source `dukascopy`,
spreads quantized to 0.1 pip).

Verification (verbatim output; method: the row's own
`pipValue = Gross/(PnLPips×Lots)` recovers the exact EUR conversion shared by Gross and
SpreadCost, so implied pips must match the recorded M1 spread of the paying fill's bar —
entry bar for Long, exit bar for Short, fill minute = M1 bar open + 1 min; 14 near-zero-move
rows skipped because pipValue recovery is numerically unstable there — a verification
artifact, not venue behaviour):

```
== (1) run header (BacktestRuns) ==
(RunId, SpreadPips, Comm/M, Gov, Regime, Venue, Trades, Warnings, Status)
('09c2eb9b', None, None, 0, 0, 'tape', 68, None, 'completed')
currencies: [('EUR', 'EUR', 'EUR')]
trades: 68

== (2) implied spread pips (row-exact pipValue) vs recorded M1 spread at the paying fill ==
idx dir   lots  pipValueEUR  spread(EUR)  implied_pips  side   m1_bar_open          m1_pips  match
  0 Short  2.10     9.4606      -5.960       0.3000  exit   2023-01-03 08:18:00   0.300  OK
  1 Long   1.98     9.4276      -9.333       0.5000  entry  2023-01-04 08:00:00   0.500  OK
  3 Long   2.06     9.4346     -11.661       0.6000  entry  2023-01-05 09:01:00   0.600  OK
  5 Short  2.54     9.2984     -11.809       0.5000  exit   2023-01-11 11:45:00   0.500  OK
  6 Short  2.67     9.2836     -12.394       0.5000  exit   2023-01-12 09:37:00   0.500  OK
  7 Long   1.94     9.2430      -7.173       0.4000  entry  2023-01-13 09:08:00   0.400  OK
  8 Short  1.96     9.2304      -9.046       0.5000  exit   2023-01-16 10:51:00   0.500  OK
  9 Short  2.06     9.2461      -7.619       0.4000  exit   2023-01-16 12:59:00   0.400  OK
matched 54/54 checked (skipped 14 near-zero-move rows, m1 lookups missed 0: [])
distinct implied spread values: 6 (flat-spread world = 1)

== (3) commission at venue-captured 45/M (per-side own notional, EUR-converted) vs flat 30/M ==
idx  lots   comm(EUR)   exp45(EUR)   rel_err   flat30(EUR)
  0  2.10   -18.9848    -18.9516   0.1753%   -12.6344
  1  1.98   -17.8061    -17.8061   0.0000%   -11.8707
  3  2.06   -18.5550    -18.5550   0.0000%   -12.3700
  5  2.54   -22.8511    -22.8375   0.0595%   -15.2250
  6  2.67   -24.0235    -24.0088   0.0613%   -16.0059
  7  1.94   -17.4963    -17.4850   0.0647%   -11.6567
  8  1.96   -17.6262    -17.6198   0.0360%   -11.7465
  9  2.06   -18.5566    -18.5559   0.0037%   -12.3706
commission within 0.5% of 45/M: 54/54 (30/M would be a 33% miss)

== (4) Net = Gross + Commission + Swap, every row ==
max |Net - (Gross+Comm+Swap)| over 68 rows: 0.0000000000

== decomposition summary (EUR) ==
n=68 gross=6029.80 spread_cost=-709.69 comm=-1511.81 swap=0.00 net=4517.99 signal=6739.50
per-pos: net=66.44 signal=99.11 spread=-10.44 comm=-22.23
```

The sub-0.2% commission residuals are cross-rate timing between the two sides' USD→EUR
conversions inside `TradeCostCalculator.Compute` (untouched by F87, R5). EURUSD has a
venue-captured spec ($45/M `UsdPerMillionUsdVolume`, captured 2026-07-15) so the
`COMMISSION_FABRICATED` warning correctly did NOT fire (`WarningsJson` null).

`cost_decomposition.py` on the same run (verbatim):

```
key	n	gross	spread_cost	commission	swap	net	signal	net_per_pos	signal_per_pos
09c2eb9b	68	6029.80	-709.69	-1511.81	0.00	4517.99	6739.50	66.44	99.11
TOTAL	68	6029.80	-709.69	-1511.81	0.00	4517.99	6739.50	66.44	99.11
net-identity violations (>|0.01|): 0
```

Headline for E2's expectations: at TRUE costs this cell paid **0.47 avg pips of spread**
(recorded per-bar) instead of the flat 1 pip every census run charged, and **45/M** commission
instead of 30/M — spread cost roughly HALVED, commission ×1.5, per the audit's direction.

## Definition of Done — status

- [x] All phases committed, fast suites green, counts reported vs baseline (table above).
- [x] `TapeReplaySpreadOverrideTests`: explicit value wins (unchanged), explicit zero valid,
      null ⇒ per-bar recorded spread, per-bar variation reaches fills.
- [x] No scored-run path silently reintroduces a flat spread: API default null, SPA form
      default blank, sweep + block-bootstrap pass null through; cTrader/compare-both REQUIRE
      explicit numbers (validation + backstops + tests).
- [x] This handover.

## Notes for the next sessions

- **Backup remains MANDATORY before E2's scored re-runs** (owner runs
  `tools/ops/backup-offmachine.ps1` with the drive attached). The probe wrote one run + the
  two migrations to trading.db; nothing else was touched (F83/F84 respected; no VACUUM, no
  checkpoint fiddling).
- E2 re-runs should use `SpreadPips:null, CommissionPerMillion:null` + D2 toggles — exactly
  this probe's request shape.
- The 14 near-zero-move rows: if Lane R wants 68/68 spread verification, recompute the
  conversion factor from the commission side instead of PnLPips (or read the cross-rate series
  directly) — the 54 exact matches across 6 distinct spread levels already kill the
  flat-spread hypothesis.
- `ftmo-1step` store upsert (E6) and the E1 items are untouched by this session.
