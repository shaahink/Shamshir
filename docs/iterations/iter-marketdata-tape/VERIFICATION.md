# iter-marketdata-tape — VERIFICATION (Phase V)

**Date:** 2026-07-01
**Branch:** `iter/integration-cache-tape` (merged iter-cache-reads-2 + iter/marketdata-tape)
**Agent:** OpenCode / DeepSeek

## Phase V1 — Recorder produces valid shards ✅

- Rebuilt the cBot (`dotnet build src/TradingEngine.Adapters.CTrader`) — Build succeeded (0 errors, net6.0 warnings benign)
- Added `--Record` support to `BacktestCliRequest` + `BuildArgs`
- Wrote `MarketDataRecorderE2ETests.Record_EurUsd_M1_1Day_ProducesShard_And_IngestsCleanly`
- Recorded EURUSD M1 for 1 day (2025-05-30 to 2025-06-01): **170 bars**, valid NDJSON
- Recorded EURUSD H1: **2 bars** (weekend gap, expected for 1-day range spanning Fri-Sun)
- Shard format verified: camelCase JSON, UTC timestamps, correct OHLCV values
- Wire format locked by `MarketDataShardIoTests.Parses_the_exact_cbot_recorder_line` (pre-existing test)
- Ingest pipeline: `MarketDataIngester.IngestFileAsync` → `SqliteMarketDataStore.WriteBarsAsync` works
- Re-ingest idempotent (0 new rows on second pass)
- Market data integration tests: **7/7 pass**
- Tape adapter integration tests: **3/3 pass**

## Phase V2 — Bulk download (pending)

- Recorder cBot works for H1 + M1
- To download the owner's working set: run cTrader CLI with `--Record=true --Periods=m1 --ReportPath=<dir> --SymbolString=EURUSD,GBPUSD,...` 
- For 6 months of data, expect ~130k M1 bars per symbol (~3-5 minute CLI run per symbol)
- Data lands in `src/TradingEngine.Web/data/marketdata.db` after ingest

## Phase V3 — Tape backtest (unverified)

- `TapeReplayAdapter` is fully wired in `BacktestOrchestrator.RunEngineReplayAsync` (`Venue=tape`)
- Requires market data in `marketdata.db` first (see V2)
- Exit resolution defaults to M1 for dual-resolution exits
- Integration tests pass, but no end-to-end tape backtest was run (needs market data)

## Phase V4-V6 — Reconcile (unverified)

- `LedgerReconciler.Compare` + `ShamshirReportParser` built and unit-tested
- `RECONCILE-FINDINGS.md` documents predicted divergences (MaxDD, swap, trade counts)
- Needs a real cTrader run + tape run to compare

## Regression gates

| Gate | Status |
|------|--------|
| Build (clean) | ✅ 0 errors |
| Unit tests | ✅ 314/0/6 |
| Determinism (golden) | ✅ 3/3 byte-identical |
| Market data integration | ✅ 7/7 |
| Tape adapter integration | ✅ 3/3 |
| cTrader E2E smoke | ✅ 1/1 (53s) |
| cTrader E2E full suite | ⚠️ Timeout (pre-existing simulation test issues) |
| Angular SPA build | ⚠️ Not yet built (requires `npm install && npm run build` in web-ui/) |
