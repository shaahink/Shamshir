# P7.6 — F6-R Economics Recovery (Option A) Verdict

**Session:** #53, 2026-07-09
**Branch:** iter/parity-pipeline
**Phase:** P7 Cleanup + Verification — Session 6

## Background

When cTrader crashes mid-run, close fills arrive as raw OrderFilled events with CloseReason but no
PublishTradeClosed effect. The pre-P7.6 barrier detected this as `Unreconstructable` (F6-R) but
could not recover the economics — it only warned about lost trades.

## What Changed

### Barrier: Reconstruct PublishTradeClosed from paired journal entries

**File:** `src/TradingEngine.Infrastructure/Persistence/TradePersistenceBarrier.cs`

The `CollectAsync` method now:
1. Collects open OrderFilled fills (no CloseReason) for entry data
2. Collects OrderProposed/OrderSubmitted events for StrategyId/Direction/StopLoss/TakeProfit
3. Detects "orphan" close fills (OrderFilled with CloseReason, no PublishTradeClosed in same step)
4. After streaming, reconstructs PublishTradeClosed from paired open + proposal + close data
5. Tracks `unreconstructedCloseFills` separately — only close fills that could NOT be paired

Successfully reconstructed closes are backfilled via the existing `TradeResultFactory.FromClose()` path
— byte-for-byte identical to live-path trades.

### Orchestrator: Updated warning messages

**File:** `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`

- `TRADES_UNRECONSTRUCTABLE:{n}`: now means "n close fills could NOT be reconstructed"
- New: `TRADES_PARTIALLY_UNRECONSTRUCTABLE:{n}`: some recovered, some not

## Gate Results

| Gate | Description | Status |
|------|------------|--------|
| Build | 0err/5warn | PASS |
| Unit | 716/0/6 | PASS |
| Integration | 121/0/0 (+1 test) | PASS |
| Sim-fast | 144/0/0 | PASS |
| Golden | byte-identical | PASS |
| Barrier tests | 6/6 (5 existing + 1 new) | PASS |

## New Test

**`CrashedTeardown_WithPairedOpenAndProposal_ReconstructsTrades`:**
- Seeds 3 close fills with matching open fills + proposals
- Verifies all 3 are reconstructed (Expected=3, Backfilled=3, JournalCloseFills=0)
- Verifies reconstructed trades carry correct economics:
  - TP trade: NetPnL=770, Direction=Long, StrategyId=trend-breakout
  - SL trade: NetPnL=-122, Direction=Short, StrategyId=bb-squeeze
  - TP trade: NetPnL=210, Direction=Long, StrategyId=trend-breakout

## Affected Tests (retrofit)

| Test | Pre-P7.6 | Post-P7.6 | Status |
|------|----------|-----------|--------|
| BtcScenario_JournalHasCloses_TradeResultsEmpty_BackfillsAll | 3 PTC → 3 backfilled | 3 PTC → 3 backfilled (unchanged) | PASS |
| PartialPersistence_BackfillsOnlyTheMissing_NoDuplicates | 2 closes, 1 persisted → 1 backfilled | unchanged | PASS |
| FullyPersisted_NoBackfill_NoLoss | 1 close, 1 persisted → 0 backfilled | unchanged | PASS |
| CrashedTeardown_CloseFillsButNoPublishTradeClosed_IsUnreconstructable | 3 close fills, 0 open/proposal → Unreconstructable | unchanged (no pairing data) | PASS |
| HealthyRun_ExtraCloseFill_ButTradesPersisted_IsNotUnreconstructable | stray + real → not unreconstructable | unchanged | PASS |
| **CrashedTeardown_WithPairedOpenAndProposal_ReconstructsTrades** | N/A | 3 paired fills → 3 reconstructed | **NEW** |

## Files Changed

1. `src/TradingEngine.Infrastructure/Persistence/TradePersistenceBarrier.cs` — F6-R reconstruction logic
2. `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` — updated warning messages
3. `tests/TradingEngine.Tests.Integration/Runs/TradePersistenceBarrierTests.cs` — new test + helpers

## Verdict: PASS

All 6 gates green. F6-R Option A delivers: the barrier now recovers trade economics from
paired journal entries when a crashed cTrader teardown left close fills without
PublishTradeClosed effects. Unpaired close fills (missing open fill or proposal in journal)
remain unreconstructed and surface via TRADES_UNRECONSTRUCTABLE.
