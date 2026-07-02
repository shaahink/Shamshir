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
