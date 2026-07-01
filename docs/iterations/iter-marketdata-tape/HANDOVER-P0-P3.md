# iter-marketdata-tape — HANDOVER (P0–P3 executed)

**Date:** 2026-07-01
**Branch:** `iter/marketdata-tape` (worktree at `C:/code/shamshir-mdtape`, based on `9b7c79f`)
**Status:** P0, P1, P2, P3 built + tested green. P4–P6 remain.

## What was built (all backend; kernel/`TradingEngine.Engine` untouched)

| Phase | Delivered | Key files |
|-------|-----------|-----------|
| **P1** | Canonical source-agnostic market-data store in its own `marketdata.db` (dedupe, inventory, weekend-aware gaps) | `Domain/Interfaces/IMarketDataStore.cs`, `Infrastructure/MarketData/{MarketDataBarRow,MarketDataDbContext,SqliteMarketDataStore}.cs`, `Domain/MarketData/TimeframeExtensions.cs`; DI in `Web/Configuration/ServiceRegistration.cs` |
| **P2** | NDJSON interchange format, ingester, pluggable `IHistoricalDataProvider` (+ FileDrop), and cBot `--Record` mode | `Infrastructure/MarketData/{MarketDataShardIo,MarketDataIngester}.cs`, `.../Providers/FileDropProvider.cs`, `Domain/Interfaces/IHistoricalDataProvider.cs`, `Adapters.CTrader/TradingEngineCBot.cs` (recorder) |
| **P3** | `TapeReplayAdapter` — in-process fake venue with dual-resolution exits; wired as `Venue=tape` | `Infrastructure/Adapters/TapeReplayAdapter.cs`, `Web/Services/BacktestOrchestrator.cs` |
| **P0** | Reconciliation harness (engine DB vs cTrader `shamshir-report.json`) | `Infrastructure/Reconcile/{ReconcileLedger,LedgerReconciler,ShamshirReportParser}.cs`; `docs/audit/RECONCILE-FINDINGS.md`, `PROGRESS.md` |

## Gates (verified)
- **Full Unit:** 304 passed / 0 failed / 6 skip.
- **Golden/determinism:** 63/63 byte-identical → the kernel is untouched.
- **Integration:** my MarketData(5)+Ingester(2)+Tape(3)+Reconcile(8, unit) all green. 9 `WebSmokeTests` fail
  **environmentally only** — this worktree has no built Angular SPA (`wwwroot` absent), so page routes 404.
  They pass where the SPA is built; they are NOT a regression (all backend changes only).
- Full solution builds incl. the net6 cBot.

## Owner-verification (needs a real cTrader run — I can't run cTrader headlessly)
1. **cBot `--Record`**: rebuild/deploy the `.algo`, run a backtest with `--Record=true --ReportPath=<dir>
   --Periods=m1` → confirm `<SYMBOL>_m1.ndjson` shards are written and ingest cleanly (wire format is locked by
   `MarketDataShardIoTests.Parses_the_exact_cbot_recorder_line`, but the cTrader-side write is unverified).
2. **Reconcile**: run a short cTrader backtest, then `ShamshirReportParser.Parse(<shamshir-report.json>)` vs the
   run's DB ledger → record which categories diverge (predicted: Aggregation/MaxDD, not RawMoney). See
   `RECONCILE-FINDINGS.md`.

## What remains
- **P4 — UI**: path selector (cTrader / tape / compare-both), Data Manager (inventory + download form using the
  P2 ingester/recorder), Method column on the backtest table, compare view rendering the P0 reconcile verdict.
  Also a small runner that builds the engine-side `ReconcileLedger` from the run's DB summary+trades (thin map).
- **P5 — fidelity hardening**: close the divergences P0 names (floating/intrabar MaxDD, swap at rollover),
  each gated by the reconcile harness going greener on an oracle set.
- **P6 — ticks**: tick tape format + `GetTicks` recording + tick-resolution exits (interfaces already reserve
  for it; `TapeReplayAdapter` dual-resolution generalizes from m1 to ticks).

## Notes for whoever wires the engine-side reconcile ledger
`BacktestRunSummary` already carries NetProfit/GrossPnL/CommissionTotal/SwapTotal/MaxDrawdownPct/TotalTrades/
WinningTrades/WinRatePct → map straight onto `ReconcileLedger`; trades from the `Trades` table → `ReconcileTrade`.
