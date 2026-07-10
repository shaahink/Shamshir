# cTrader test policy — what stays on the real venue, what moves to tape

**Written:** 2026-07-05 (static review session, iter-quant-model P4.5). Owner ask: cTrader-backed tests
have been a recurring blocker/flake source across sessions (10–25+ min runs, orphan processes, timeout
flakes under contention). Now that the tape venue exists and is honest (P0.1–P0.3 spread/fill fixes),
only tests that prove things TAPE CANNOT PROVE earn a cTrader dependency.

## Principle

A test may require cTrader only if it proves one of:

1. **Transport/connection** — NetMQ wiring, handshake, bar/tick flow into the engine.
2. **Order round-trip** — one canonical smoke: an order leaves the engine, the real compiled cBot places
   it, a fill comes back. ONE such test, short window.
3. **Venue-ledger reconciliation** — cTrader's own report vs our DB (ClientOrderId matching, per-trade
   costs). Tape cannot self-verify against the oracle; these tests are P6's tooling.
4. **Data acquisition** — download/record → shard → ingest. The data comes FROM cTrader by definition.

Everything else — "produces trades over N days/months", per-symbol behavior, per-TF behavior, strategy
audits — is **strategy/engine behavior** and belongs on tape (golden, characterization, acceptance tests).
Running those through cTrader tests the same engine code slower, flakier, and with a venue we cannot
control fixture-wise.

## Triage of the current suite (verified against the test tree 2026-07-05)

### KEEP — tag `[Trait("Category", "CtraderContract")]`

| Test | Why it stays |
|---|---|
| `NetMQBridgeTest.EngineReceivesBarAndTickOverNetMQ` | Transport (rule 1) |
| `PipelineE2ETests.ThreeDays_PipeAndDataFlow` | Connection + data flow, already `Category=Fast` (rule 1) |
| `PipelineE2ETests.EurUsd_H1_3Days_ProducesTrades` | THE canonical order round-trip smoke, 3 days (rule 2) |
| `PipelineE2ETests.InProcessEngine_WithCtraderCli_EurUsd_OneDay_ProducesTrades` | Only coverage of the in-process/listen-mode transport variant (iter-ctrader-capture path) (rule 1/2) |
| `DiffE2ETests.CtraderVsDb_Comparison_ProducesReport` | Ledger reconciliation (rule 3) |
| `DiffE2ETests.CostIntegrity_PerTradeCostsMatch_ClientOrderIdReconciliation` | Cost integrity vs venue ledger (rule 3) |
| `CtraderE2EHarnessSmokeTests.TradeLedger_ClientOrderIdReconciliation_NoMissingTrades` | Ledger integrity (rule 3) |
| `CtraderScenarioE2ETests.HappyPath_EurUsd_TradeLedgerHasIntegrity` | Ledger integrity happy path (rule 3) |
| `CtraderScenarioE2ETests.EdgeCase_WeekendRange_RunsToCompletionWithoutGarbage` | Session/weekend handling of the real venue connection (rule 1) |
| `CtraderScenarioE2ETests.AfterRun_NoOrphanCtraderProcesses` | Guards the documented orphan-process gotcha (harness hygiene) |
| `MarketDataRecorderE2ETests.Record_EurUsd_M1_1Day_ProducesShard_And_IngestsCleanly` | Data acquisition (rule 4) — P5.1 depends on it |
| `MarketDataBulkDownloadE2ETests.Download_..._All_Ingest_Cleanly` | Data acquisition (rule 4) — P5.1 depends on it |

### RETIRE → tape equivalent (delete after confirming the tape coverage named exists)

| Test | Why it goes | Tape equivalent |
|---|---|---|
| `PipelineE2ETests.EurUsd_H1_ThreeMonth_GeneratesAtLeastOneTrade` | 3-month strategy behavior; 10-min timeout budget | golden/characterization suites already run 3-month windows on tape |
| `PipelineE2ETests.EurUsd_H1_30Days_MirrorsWebDefault_ProducesTrades` | Web-default behavior check | a tape run with the same web-default config (add to characterization if missing) |
| `PipelineE2ETests.GbpUsd_H1_30Days_ProducesTrades` | Symbol-variety behavior; THE documented timeout flake (P2.1 gate notes) | GBPUSD tape characterization once P5.1 downloads GBPUSD |
| `DiscoveryAuditTests.EurUsd_H1_1Month_MeanReversion_FullAudit` | Strategy audit — journal + tape give the identical audit faster | tape run + Journal assertions |
| ONE of `CtraderE2EHarnessSmokeTests.EurUsd_H1_3Days_ProducesTrades_UsingPhasedHarness` / `_UsingRunAsync` | Two smokes of the same harness path is one too many | keep `_UsingRunAsync` (the API agents actually use), retire the phased twin |

### RESHAPE

| Test | Change |
|---|---|
| `PipelineE2ETests.EurUsd_M15_3Days_ProducesTrades` | Keep — it is the ONLY coverage of non-H1 through the real cBot `Periods` wiring (post-P1 this matters). But re-shape the assertion to connection facts (M15 bars received by the engine, ≥1 order accepted by the cBot) instead of "produces trades", which couples it to strategy behavior. Tag `CtraderContract`. |

## When the CtraderContract set runs

- When code under `src/Ctrader*`, the cBot, `CTraderBrokerAdapter`, NetMQ transport, or the wire schema
  changes.
- Before merging an iteration branch to main.
- In a P6 reconcile session.
- **Never** as a phase gate for engine/research/UI work, and never launched by an agent without the owner
  asking. Phase gates use:
  `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"`

## Implementation notes for the retiring agent

- One commit: add the `CtraderContract` trait to the keep set; delete the retire set; reshape the M15 test.
  Quote in the commit body, per retired test, the tape test that covers the behavior (name + file). If no
  tape equivalent exists yet (GBPUSD case until P5.1), write the replacement FIRST or mark the gap in
  TEST-INVENTORY.md — do not delete coverage into a void.
- Update `docs/TEST-INVENTORY.md` totals and the G2 row (kernel-path limit orders — now also flagged in
  iter-quant-model PLAN.md P4.5 carry-forwards).
- Do not touch `CtraderTestHarness` behavior itself in the same commit.
