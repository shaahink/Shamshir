# P7.1 — P4.1 Live Verification (Session 1)

**Date:** 2026-07-09
**Session:** 33 (P7 Cleanup, Session 1)
**Branch:** `iter/parity-pipeline`

## QA of Previous Session (s32)

**Confirmed.** Full gate battery re-run verbatim: build 0err/5warn, Unit 715/0/6, Integration 120/0/0, fast Sim 144/0/0, golden byte-identical. ShippedPlaybook_Parses 10/10 confirmed.

The handoff block claimed Unit 714 but actual count is 715 (session 32's audit commit 99d5f45 added a test). No divergence — the handoff slightly undercounts but all tests pass.

## P4.1 Verification Results

### 1. Exploration Funnel Banner — FIX REQUIRED

**Finding:** The `explorationMode` flag was NOT persisted to the database. It flowed correctly from the POST request → `CustomParams` → in-memory `BacktestRunState`, but the `RunQueryService.GetRunAsync` persisted path (lines 100-141) never populated `RunDetailResponse.ExplorationMode`/`RecordExcursions`. Completed runs always returned `false`.

**Root cause:** `BacktestRunSummary` had no `ExplorationMode` or `RecordExcursions` fields → not written in `WriteStartRecordAsync`/`WriteEndRecordAsync` → not read in `GetRunAsync` persisted path.

### 2. Fix Applied (M46)

1. Added `bool ExplorationMode = false` and `bool RecordExcursions = false` to `BacktestRunSummary` record (`src/TradingEngine.Domain/Interfaces/IBacktestRunRepository.cs:47-50`)
2. Added corresponding properties to `BacktestRunEntity` (`src/TradingEngine.Infrastructure/Persistence/Entities/BacktestRunEntity.cs:63-65`)
3. Migration `M46_RunExplorationFlags` (20260709010118) — adds `ExplorationMode` (INTEGER, default 0) and `RecordExcursions` (INTEGER, default 0) to `BacktestRuns` table
4. Wired in `SqliteBacktestRunRepository.SaveAsync` and `ReconcileAsync`
5. Wired in `BacktestOrchestrator.WriteStartRecordAsync` and `WriteEndRecordAsync` — reads from `cfg.CustomParams`
6. Wired in `RunQueryService.GetRunAsync` persisted path — `r.ExplorationMode`, `r.RecordExcursions`

### 3. Post-Fix Verification

**Exploration run API response (completed):**
- RunId: `99fa7698` (EURUSD H1, 2026-03-02 to 2026-04-02, explorationMode=true, recordExcursions=true)
- Status: `completed`, ExplorationMode: `True`, RecordExcursions: `True`
- DB confirms: `ExplorationMode=1, RecordExcursions=1`

**Exploration funnel banner precondition met:**
The run-report template (`web-ui/src/app/features/runs/run-report/run-report.component.ts:90-98`) shows the purple "Exploration complete" banner when `explorationMode && (status === 'completed' || status === 'completed-with-warnings')`. This condition now evaluates to `true` for completed exploration runs.

### 4. Backfill Endpoint

**POST /api/system/backfill-mae-mfe:**
- First call: `{"totalCandidates":84,"updated":84,"skippedNoSymbol":0,"skippedZeroStop":0}`
- Second call (idempotency): `{"totalCandidates":0,"updated":0,"skippedNoSymbol":0,"skippedZeroStop":0}`

**MaeR/MfeR verification (SQL):**
```
SELECT COUNT(*), COUNT(MaeR), COUNT(MfeR), AVG(MaeR), AVG(MfeR) FROM TradeResults
→ 84 | 84 | 84 | 0.783 | 1.079
```
All 84 trades have MaeR (avg 78.3% of stop distance) and MfeR (avg 107.9% of stop distance) — values are reasonable.

**Per-run distribution:**
| RunId | Trades | MaeR present | MfeR present |
|-------|--------|:---:|:---:|
| 2c9551d1 | 28 | 28 | 28 |
| 817af3f5 | 24 | 24 | 24 |
| 020fd4eb | 8 | 8 | 8 |
| 81729685 | 7 | 7 | 7 |
| 0f6a97d3 | 6 | 6 | 6 |

## Gates

- Build: 0err/5warn (pre-existing net6.0 TFM warnings)
- Unit: 715/0/6
- Integration: 120/0/0
- Fast Sim: 144/0/0
- Golden: byte-identical (git diff empty)
- ShippedPlaybook_Parses: 10/10

## Verdict

P4.1 funnel banner was structurally correct but broken by a persistence gap (ExplorationMode lost on run completion). M46 migration + wiring fix resolves it. Backfill endpoint works and is idempotent. MaeR/MfeR values are populated and reasonable.
