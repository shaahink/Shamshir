# A1 Session 5 — Reconciliation gap fix

**Date:** 2026-07-09
**Session:** #5 (recovery from interruption)

## Diagnosis

### DB verification
- TradeResults: 99 rows across runs (2c9551d1=28, 020fd4eb=8, 2cdba11a=3, etc.)
- Journal: 9183 rows across runs
- No reconciliation discrepancy: all runs have TotalTrades matching actual TradeResults count
- Jul 9 tape runs: all 0 TotalTrades AND 0 TradeResults (genuinely zero trades)

### Root cause found
`RunQueryService.GetRunsAsync()` (line 46-79) queries `_db.BacktestRuns.AsNoTracking()` and reads `TotalTrades = r.TotalTrades` — bypassing `ReconcileAsync()` entirely.

`RunQueryService.GetRunAsync()` (line 90-144) goes through `_runRepo.GetByIdAsync()` → `SqliteBacktestRunRepository.ReconcileAsync()` which self-heals by re-deriving from Trades table.

### Corroborating evidence
- `SqliteBacktestRunRepository.cs:107-230` — ReconcileAsync method does full re-derive when TotalTrades=0 or ExitCode=-1
- `BacktestRunEntity.cs:43` — TradeResultEntity.RunId is nullable (`string?`)
- `RunQueryService.cs:69` — list endpoint reads raw column value directly

## Fix

Added `FixStaleTradeCounts()` method in `RunQueryService.cs` that:
1. Collects RunIds from runs with TotalTrades=0
2. Batch-queries Trades table for actual counts
3. Updates in-memory RunListResponse values where discrepancy found

Called after the main query and before `FixStuckRunStatuses`.

## Gate results

| Gate | Result |
|------|--------|
| build | 0 errors, 5 pre-existing net6.0 warnings |
| unit | 716 passed, 0 failed, 6 skipped |
| integration | 121 passed, 0 failed, 0 skipped |
| sim-fast | 144 passed, 0 failed, 0 skipped |
| golden | clean (no diffs) |

## Limitations

The fix addresses the reconciliation gap — it will correct stale TotalTrades when trades exist in the DB. It does NOT fix the underlying issue where Jul 9 tape runs produce 0 trades despite the Market default revert (f0855ed). For new runs that genuinely produce 0 TradeResults, there is nothing to reconcile.

Next step for the 0-trade issue: trace `BarEvaluator.EvaluateAsync` → `EntryPlanner.Plan()` → kernel event pipeline. Compare working run 2cdba11a (Jul 7, with explicit RunPlanJson entries) vs broken Jul 9 runs (empty RunPlanJson).
