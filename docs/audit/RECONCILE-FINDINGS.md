# Reconciliation — methodology + expected divergences

**Status:** harness built + unit-tested; LedgerReconcileService + GET /api/backtest/analytics/reconcile endpoint
built (iter-tape-trust T2). A real end-to-end reconcile is owner-run (needs a cTrader run).

## Pre-registered fidelity gaps — expect these in V4 (tape vs cTrader)

Before running the tape-vs-cTrader reconcile, register these KNOWN modelling gaps so they aren't mis-triaged
as bugs. `RawMoney` divergence from any of these is EXPECTED — the fix is in iter-tape-trust T3.

| # | Gap | Expected effect |
|---|---|---|
| **F1** | **No spread cost on entry/exit fills.** Both replay venues fill at mid; cTrader fills buys at ask, sells at bid, and detects exits on the opposite side. | Per-trade RawMoney divergence ≈ spread × pipValue × lots per round turn. EURUSD ~1 pip ≈ $10/lot. Systematic: tape is optimistic. |
| **F2** | **Intrabar floating equity not snapshotted.** `EmitAccountUpdate` fires per decision bar only, not per exit-TF bar. Intrabar equity troughs are invisible. | Tape MaxDD understates cTrader's floating DD — the "DB MaxDD=0 vs venue 4.6%" pain survives on the fast path. |
| **F3** | **Trailing/breakeven cadence.** Trailing stops update once per decision bar; cTrader trails per-tick. | Trailing exits systematically later/looser than cTrader. Sizing unknown — measure first. |
| **F4** | **Gap-through fills at exact stop price.** A bar opening beyond SL fills at the stop, not the (worse) open. | Optimistic on weekend/news gaps; FTMO daily DD punishes these tails. |
| **F5** | **Commission charged wholly at close.** cTrader charges half at open, half at close. The engine charges 100% at close. | Net P&L is identical either way. Intrabar equity is slightly optimistic while a position is open (commission deduction deferred). **Deferred — needs golden re-baseline.** |
| **F6** | **Limit+SL same-fine-bar ordering.** In dual-resolution tape mode, `ProcessPendingLimits` runs before `ProcessSlTpHits` each fine bar. A limit that fills on fine bar k becomes an open trade immediately SL-checked on the same bar k. `DetectSlTpExit` checks SL before TP (conservative). | Intrabar entry+exit possible within one 1-minute bar. Intra-bar ordering (limit-fill vs SL-touch) is unknowable at M1 resolution. Impact: minor. Decision: document, no code change planned. |
| **F7** | **Fine bars in decision-TF gaps.** When a decision-TF bar is missing (weekend gap, patchy data), fine bars in the gap window are consumed by `_exitIndex++` + warmup skip (`if (fine.OpenTimeUtc < decisionBar.OpenTimeUtc) continue`) without ever passing through `ProcessSlTpHits`. | SL/TP exits that would have triggered during the gap never fire — the position survives a gap it might not have. Optimistic bias. Impact: only matters with patchy decision data. Mitigation noted: future gap-exit-detection pass would process fine bars in gaps before skipping. |

## Fixed gaps (for audit trail)

| # | Gap | Resolution |
|---|---|---|
| **F1** | No spread cost on entry/exit fills | **Fixed (T3):** `BacktestReplayAdapter` + `TapeReplayAdapter` now apply half-spread on fills (longs buy at ask, shorts sell at bid; SL/TP detection uses opposite side). Golden 63/63 survived — both kernel paths use the same adapter. |
| **F2** | Intrabar floating equity not snapshotted | **Fixed (T3):** `TapeReplayAdapter.OnBarObserved` tracks `minEquity` across the fine-bar window and emits it via `EmitAccountUpdate(BrokerTimeUtc, minEquity)`. |
| **F3** | Trailing/breakeven cadence (decision-bar vs per-tick) | **Design gap — measure first.** `KernelTrailingEvaluator` runs once per decision bar by design. cTrader trails per tick. Dual-res tape detects hits of the *last-set* stop on M1 bars, but the stop only *moves* per H1. Revisit only if V4 reconcile shows it changes a GO/NO-GO decision. |
| **F4** | Gap-through fills at exact stop price | **Fixed (T3):** Gap-through fills now include slippage — fills at the bar open (worse price) if it gaps beyond the stop, not at the stop. |
| **F8** | Silent single-resolution fallback (exit resolution not surfaced) | **Fixed (T0):** `ExitResolution` is now on `RunDetailResponse`. Tape venue logs a Warning when falling back to single-res. Owner can see whether a run got wick-fidelity or not. |

## What the harness does
`LedgerReconciler.Compare(engineLedger, venueLedger)` diffs two normalized `ReconcileLedger`s and classifies
each field difference:

- **RawMoney** (NetProfit / GrossProfit / Commission / Swap) — should agree **to the cent**. On the cTrader
  path the cBot forwards cTrader's OWN `pos.NetProfit/Commissions/Swap` (verified: `TradingEngineCBot.cs`
  reads them straight off the position, `CTraderBrokerAdapter` ingests them). **A RawMoney divergence means a
  real bug**, not a modelling choice.
- **Aggregation** (MaxDrawdownPct / WinRatePct / equity curve) — **divergence is EXPECTED here**. The engine
  re-derives these from sparser data than cTrader has. This is the predicted root of the owner's "DB ≠ cTrader"
  pain.
- **TradeSet** (TotalTrades / WinningTrades) — count mismatches, typically from late settlement.

## Sources
- **Venue (oracle):** `shamshir-report.json` — written by `ShamshirTradeLogger` in the cBot, harvested by
  `CtraderReportHarvester`. Parsed by `ShamshirReportParser`. Carries cTrader's own per-trade economics.
- **Engine:** the run's DB row (`BacktestRunEntity`: NetProfit/GrossPnL/CommissionTotal/SwapTotal/
  MaxDrawdownPct/TotalTrades/WinningTrades/WinRatePct) + `Trades`. Mapped to `ReconcileLedger` by
  `LedgerReconcileService.BuildEngineLedgerAsync`.
- **Web endpoint:** `GET /api/backtest/analytics/reconcile?left={runId}&right={runId}` returns a full
  `LedgerReconciler.Compare` output (per-field divergences + text summary).

## Predicted divergences (from the static trace — confirm with a real run)
1. **MaxDrawdownPct: engine ≪ venue.** cTrader's MaxDD includes intrabar *floating* equity; the engine sees
   only per-bar account snapshots / realized trades, so its DD is systematically smaller (the known
   "DB MaxDD=0 vs venue 4.6%"). *Fix options (P5): feed per-bar account equity into the engine's DD, or on the
   cTrader path display the venue's DD as truth.*
2. **Swap: small gaps at rollover.** Swap is charged at daily rollover (triple Wednesday). Snapshot timing can
   differ. *Fix (P5): model rollover in `TradeCostCalculator`, calibrated from the oracle's per-trade swap.*
3. **TotalTrades: venue may undercount late-settled closes** (stats taken before final settlement).
4. **RawMoney: expected ~0.** If it isn't, that's the first bug to chase — start there.

## How to run a real reconcile (owner)
1. Run a short cTrader backtest (e.g. EURUSD H1, 1 day) with `--ReportPath` set so the cBot writes
   `shamshir-report.json` (already the default cTrader path).
2. `var venue = ShamshirReportParser.Parse(File.ReadAllText(shamshirReportPath));`
3. Build `engine` from the run's DB summary+trades (thin map).
4. `var report = LedgerReconciler.Compare(engine, venue); Console.WriteLine(report.ToText());`
5. Record which categories diverged in this file. RawMoney → bug hunt; Aggregation → P5 modelling.

Do the SAME short config through the fast `Venue=tape` path and reconcile that against the oracle too — that is
the acceptance test for the fake venue (P3/P5).
