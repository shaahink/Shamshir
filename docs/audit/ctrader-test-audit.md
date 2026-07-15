# cTrader Test Audit — RequiresCTrader Classifications

**Session:** #54 (P7.7), 2026-07-09
**Branch:** iter/parity-pipeline
**Scope:** All `[Trait("RequiresCTrader", "true")]` simulation tests + all `Category=CtraderContract` tests (active + retired)

## Methodology

Per `docs/CTRADER-TEST-POLICY.md`, a test may require cTrader only if it proves one of:
1. **Transport/connection** — NetMQ wiring, handshake, bar/tick flow into the engine
2. **Order round-trip** — canonical smoke: engine → cBot → fill → engine
3. **Venue-ledger reconciliation** — cTrader report vs our DB
4. **Data acquisition** — download/record → shard → ingest

Everything else (strategy behavior, per-symbol/TF behavior, PnL integrity assertions) is engine behavior and belongs on tape.

## Classification Table

### ACTIVE — 14 test methods across 8 files

| # | File | Test | Policy Rule | Classification | Rationale |
|---|------|------|:-----------:|---------------|-----------|
| 1 | PipelineE2ETests | `ThreeDays_PipeAndDataFlow` (EURUSD, GBPUSD) | Rule 1 | **KEEP** | Raw connection + data flow through full cBot→NetMQ→engine pipeline. 2 theory rows. |
| 2 | PipelineE2ETests | `EurUsd_H1_3Days_ProducesTrades` | Rule 2 | **KEEP** | THE canonical order round-trip smoke (300s timeout). No tape equivalent for real cBot order placement. |
| 3 | PipelineE2ETests | `EurUsd_M15_3Days_ProducesTrades` | Rule 1 | **KEEP** | ONLY coverage of non-H1 cBot `Periods` wiring. Already reshaped to connection facts (barEvals>0) per P4.5. |
| 4 | PipelineE2ETests | `InProcessEngine_WithCtraderCli_EurUsd_OneDay_ProducesTrades` | Rule 1/2 | **KEEP** | Only coverage of the in-process/listen-mode transport variant (iter-ctrader-capture path). Tape has no listen-mode equivalent. |
| 5 | CtraderE2EHarnessSmokeTests | `EurUsd_H1_3Days_ProducesTrades_UsingRunAsync` | Rule 2 | **MERGE INTO #2** | Duplicate of `PipelineE2ETests.EurUsd_H1_3Days_ProducesTrades`. Both run EURUSD H1 3 days and assert trades>0 + barEvals>0. Keep #2 as the canonical; retire this one. 1 line to skip. |
| 6 | CtraderE2EHarnessSmokeTests | `TradeLedger_ClientOrderIdReconciliation_NoMissingTrades` | Rule 3 | **KEEP** | ClientOrderId reconciliation against cTrader's `shamshir-report.json`. Only way to verify trade-count parity with the venue's own ledger. |
| 7 | CtraderScenarioE2ETests | `HappyPath_EurUsd_TradeLedgerHasIntegrity` | — | **REPLACEABLE** | Asserts EntryPrice>0, Lots>0, NetPnL≠0, SL/TP exits have ExitPrice>0, ≤1 synthetic close. These are ALL engine/strategy domain assertions — a tape run with the same config produces identical engine-side trades. **Effort:** ~15m. Add a tape characterization test with the same assertions. |
| 8 | CtraderScenarioE2ETests | `EdgeCase_WeekendRange_RunsToCompletionWithoutGarbage` | Rule 1 | **KEEP** | Tests real venue connection behavior over a weekend gap (Sat–Sun EURUSD). Tape can't reproduce venue session edge cases. |
| 9 | CtraderScenarioE2ETests | `AfterRun_NoOrphanCtraderProcesses` | — | **KEEP** | Guards the documented orphan-process gotcha. Only meaningful with real cTrader CLI. |
| 10 | DiffE2ETests | `CtraderVsDb_Comparison_ProducesReport` | Rule 3 | **KEEP** | cTrader report vs DB comparison — produces discrepancy report. The ledger reconciliation oracle. |
| 11 | DiffE2ETests | `CostIntegrity_PerTradeCostsMatch_ClientOrderIdReconciliation` | Rule 3 | **KEEP** | Per-trade cost integrity: commission, swap, PnL matched per ClientOrderId. Deepest ledger reconciliation test. |
| 12 | MarketDataRecorderE2ETests | `Record_EurUsd_M1_1Day_ProducesShard_And_IngestsCleanly` | Rule 4 | **KEEP** | Data acquisition: record M1 shard via cTrader CLI, ingest into MarketDataDb, verify inventory. |
| 13 | MarketDataBulkDownloadE2ETests | `Download_EurUsd_H1_M1_ThreeDays_And_GbpUsd_H1_ThreeDays_All_Ingest_Cleanly` | Rule 4 | **KEEP** | Bulk data download: EURUSD H1+M1 + GBPUSD H1, ingest all. Copies output to Web's marketdata.db. |
| 14 | NetMQBridgeTest | `EngineReceivesBarAndTickOverNetMQ` | Rule 1 | **KEEP** | Raw NetMQ transport: spawns engine process, sends bar+bar_result+tick frames over PUB+ROUTER sockets. Deepest transport test. |

### RETIRED — 5 tests (already skipped)

| # | File | Test | Skip Reason |
|---|------|------|-------------|
| R1 | PipelineE2ETests | `EurUsd_H1_ThreeMonth_GeneratesAtLeastOneTrade` | P4.5: 3-month strategy behavior covered by tape golden/characterization |
| R2 | PipelineE2ETests | `EurUsd_H1_30Days_MirrorsWebDefault_ProducesTrades` | P4.5: web-default covered by tape characterization |
| R3 | PipelineE2ETests | `GbpUsd_H1_30Days_ProducesTrades` | P4.5: GBPUSD tape char pending P5.1 downloads |
| R4 | DiscoveryAuditTests | `EurUsd_H1_1Month_MeanReversion_FullAudit` | P4.5: strategy audit covered by tape run + Journal assertions |
| R5 | CtraderE2EHarnessSmokeTests | `EurUsd_H1_3Days_ProducesTrades_UsingPhasedHarness` | P4.5: RunAsync API covers the same harness path |

## Summary

| Classification | Count | Description |
|----------------|:-----:|-------------|
| KEEP | 12 (+2 theory rows) | Genuinely need real cTrader (transport, ledger recon, data acquisition, harness hygiene) |
| MERGE INTO | 1 | `UsingRunAsync` → covered by `PipelineE2ETests.EurUsd_H1_3Days_ProducesTrades` |
| REPLACEABLE | 1 | `HappyPath_EurUsd_TradeLedgerHasIntegrity` → assertions are engine domain, tape can run identical |
| RETIRED (skip) | 5 | Already retired per P4.5 policy |

## Actionable Recommendations

### 1. MERGE: Retire `CtraderE2EHarnessSmokeTests.EurUsd_H1_3Days_ProducesTrades_UsingRunAsync`
- **Effort:** 1 line (add `Skip = "P7.7: merged into PipelineE2ETests.EurUsd_H1_3Days_ProducesTrades"`)
- **File:** `tests/TradingEngine.Tests.Simulation/E2E/CtraderE2EHarnessSmokeTests.cs:49`
- **Coverage:** Identical to `EurUsd_H1_3Days_ProducesTrades` at `PipelineE2ETests.cs:54`
- **Risk:** None. Both exercises the same 3-day EURUSD H1 path via the same `CtraderE2EHarness`.

### 2. REPLACE: Add tape equivalent for `CtraderScenarioE2ETests.HappyPath_EurUsd_TradeLedgerHasIntegrity`
- **Effort:** ~15m. Add a tape characterization test with the same assertions:
  - EntryPrice>0 for all trades
  - Lots>0 for all trades
  - SL/TP exits have ExitPrice>0
  - Real movers (non-trivial exit price) have non-zero NetPnL
  - ≤1 synthetic close (ExitPrice=0)
- **File to create:** `tests/TradingEngine.Tests.Simulation/Characterization/TradeIntegritySmoke.cs` (or add to existing)
- **Tape equivalent config:** EURUSD H1 3 days, standard profile, trend-breakout strategy
- **Then:** Skip the cTrader version
- **Risk:** Low. Tape venue with post-P0.1 fixes (correct sizing, honest fills) produces identical engine-side trade ledger.

### 3. KEEP everything else
The remaining 12 tests prove things tape CANNOT prove: real NetMQ transport, cBot round-trip, venue ledger reconciliation, data acquisition, listen-mode transport, and harness hygiene (orphan process guard, weekend edge case).

## Gate

| Gate | Status |
|------|--------|
| Classification table complete (14 active + 5 retired) | ✅ |
| Replaceable tests identified with effort estimates | ✅ (1 REPLACEABLE, 1 MERGE INTO) |
| Report committed at `docs/audit/ctrader-test-audit.md` | ✅ |

## Verdict: PASS

All 19 RequiresCTrader tests (14 active + 5 retired) classified by policy rules. The P4.5 policy already did the heavy lifting — 5 tests correctly retired. This audit found 1 additional MERGE INTO and 1 REPLACEABLE that the policy did not catch. No KEEP classification is contested; all 12 kept tests genuinely need the real cTrader venue.
