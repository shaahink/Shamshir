# iter-tape-trust — Handover (T0–T2 complete)

**Date:** 2026-07-02
**Branch:** `iter/tape-trust` (based off `iter/integration-cache-tape`)
**Tests:** Unit 314/0/6, Integration 91/0, Simulation 63/63 golden (unchanged through T0–T2)

## 1. What was completed

### T0 — Tape runs report the truth
- **B1:** Created `IReplayVenue` interface (`Domain/Interfaces/IReplayVenue.cs`) with `int BarCount { get; }`.
  Both `BacktestReplayAdapter` and `TapeReplayAdapter` implement it. Orchestrator casts to `IReplayVenue`
  instead of concrete `BacktestReplayAdapter` → tape runs now report `completed` with correct bar counts.
- **B1b:** Pre-query at `BacktestOrchestrator.cs:860` branches on `useTape` — counts bars from
  `IMarketDataStore.ReadBarsAsync` for tape, `IBarRepository.GetAsync` for replay.
- **B2:** `BacktestRunState` extended with `InitialBalance`, `BacktestFrom`, `BacktestTo`, `RiskProfileId`,
  `RunPlanJson`, `EffectiveConfigJson`. `BuildRunDetailFromState` wires all of them + existing
  `Venue`, `GovernorEnabled`, `RegimeEnabled`, `CommissionPerMillion`, `SpreadPips`. Integration test
  `RowRun_persists_and_surfaces_full_selection` is now green.
- **F8:** `TapeReplayAdapter.ConnectAsync` fallback log changed to `LogWarning` (survives `MinLogLevel=Warning`).
  Adapter sets `ExitResolution` property (e.g. `"M1"` or `"H1 (fallback — no M1 bars)"`). Orchestrator
  captures it onto `BacktestRunState.ExitResolution`. Surfaced via `RunDetailResponse.ExitResolution`.
- **B9:** `EmitExecutionEvent` helper in both adapters — wraps `_executionChannel.Writer.TryWrite` with
  error logging on false. Replaced all 16 raw `TryWrite` calls (8 per adapter).

### T1 — Data acquisition at scale
- **B3:** cBot `StartRecording` now does cross-product (symbols × periods) instead of positional
  `(symbols[i], periods[i])`. Every symbol gets every listed period.
- **B4:** Download is a background job system:
  - `DownloadJobService` — manages in-memory job state (queued/recording/ingesting/done/failed).
  - `POST /api/data-manager/download` returns `{ jobId, status: "queued" }` immediately.
  - `GET /api/data-manager/jobs/{id}` polls status, bars recorded, error.
  - `GET /api/data-manager/jobs` lists all jobs.
  - Accepts explicit `from`/`to` range in addition to `days`.
  - **Known limitation:** ports hardcoded to 15562/3. No dynamic port manager exists yet.
  - **Decision D85:** Current hardcoded ports are documented as a known gap; a port allocator should
    be built before parallel downloads or concurrent backtests.
  - Shard cleanup only on success/failure paths; retained on unexpected errors for inspection.
- **B7:** cBot shards open with `append: true` — re-running the same range appends; dedupe absorbs overlaps.
- **B8:** `WriteBarsAsync` chunks inserts at 5,000 rows with `ChangeTracker.Clear()` between.
  `IngestFileAsync` streams large files (≥~20MB) in 50k-row chunks via `WriteBarsAsync`.
- **B6:** `GetAccountStateAsync` returns current `_balance` instead of `_initialBalance` in both adapters.
- **B10:** Tick synthetic ask uses `SymbolInfo.PipSize` instead of hardcoded `0.0001m`.
- **Coverage view:** NOT DONE. Deferred — can be added alongside T4 (compare mode UI).

### T2 — Trust loop (partial)
- **LedgerReconcileService:** Maps `BacktestRunEntity` + `TradeResultEntity` → `ReconcileLedger`.
- **Reconcile endpoint:** `GET /api/backtest/analytics/reconcile?left={runId}&right={runId}` returns
  full `LedgerReconciler.Compare` output with per-field divergences + text summary.
- **RECONCILE-FINDINGS.md:** Updated with pre-registered F1-F4 fidelity gaps (spread-free fills,
  intrabar equity, trailing cadence, gap-through fills) so V4 reconciliation isn't mis-triaged.

## 2. What was NOT completed

| Item | Status | Why |
|------|--------|-----|
| V2 (owner's working set) | Not started | Needs cTrader credentials |
| V3 (speed baseline) | Informal only | 531ms measured during review; formal baseline needs structured test |
| V4 (tape vs cTrader reconcile) | Infrastructure ready | Mapper + endpoint built; actual reconcile needs cTrader credentials + runs |
| V5 (engine-DB vs cTrader) | Infrastructure ready | Same mapper works; needs cTrader report parsing side |
| T3 (F1-F4 fidelity hardening) | Not started | F1 will change all trade results — needs careful test baseline update |
| T4 (Compare mode UI) | Not started | Mapper + endpoint ready; needs UI work |
| T5 (Sweep runner) | Planned | See section 7 |
| B5 (limit expiry in decision bars) | Not started | Deferred to T3 |
| B11 (repo hygiene) | Partial | `.git-rewrite/` deleted; gitignore for `src/TradingEngine.Web/data/` added; stale DBs remain untracked due to `*.db` rule |

## 3. Decisions made during this iteration

| # | Decision | Rationale |
|---|---|---|
| D84 | `IReplayVenue` interface placed in `TradingEngine.Domain/Interfaces/` | Domain-level contract, no infra deps. Both adapters already have `BarCount`. |
| D85 | Download port allocation uses hardcoded 15562/3 | No port manager exists in codebase. Documented as known limitation; a port allocator should be built before concurrent downloads or backtests using the same ports. |
| D86 | `EmitExecutionEvent` helper added per-adapter (not shared) | Both adapters are sealed, in same namespace. Shared static helper would add a dependency; per-adapter helpers keep scope small. |
| D87 | `GetAccountStateAsync` returns `_balance` for both balance and equity | Called only at startup where no positions are open; the second parameter is informational. Computing floating PnL here adds risk without value. |
| D88 | cBot shards use `append:true` (not `.partial`/rename) | Simpler — the ingester's dedupe absorbs overlaps. No rename-on-stop coordination needed. |
| D89 | `LedgerReconcileService` is Scoped, not Singleton | Needs `TradingDbContext` which is scoped. |
| D90 | F1 spread fix deferred to T3 (not applied in T2) | Would change all trade results + characterization test baselines. Needs dedicated phase with test baseline refresh. |
| D91 | `RunPlanJson` value from `cfg.CustomParams["RunRows"]` | Same source as `WriteStartRecordAsync` (line 566). Consistent with DB path. |

## 4. Bugs found during implementation

None — all 11 bugs from the review (B1-B11) were already catalogued. B6, B8, B10 are now fixed; B5, B11 (partial) remain.

## 5. Known limitations

- **Download ports hardcoded (B4):** 15562/3. A second concurrent download or a concurrent backtest will collide. Build a port allocator.
- **Coverage view not built:** No inventory overlap display. F8 guards the venue side, but the Data Manager page doesn't help users discover data holes.
- **F1 spread not applied:** Both replay venues still fill at mid. All backtest PnL is systematically optimistic by ~1 pip per round turn. This is the single biggest gap between tape and reality.
- **No reconcile script:** The web endpoint exists but no PowerShell runner for offline/batch use.
- **cBot not rebuilt:** B3/B7 changes to `TradingEngineCBot.cs` need a cTrader build (`.algo` rebuild) and `RequiresCTrader` E2E tests. The cBot project compiles as part of the solution, but `RequiresCTrader` tests were not run.

## 6. How to verify

```powershell
# Full build
dotnet build

# Test gates
dotnet test tests/TradingEngine.Tests.Unit           # 314 pass, 0 fail, 6 skip
dotnet test tests/TradingEngine.Tests.Integration    # 91 pass, 0 fail
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"   # 63/63

# Driven tape run (needs marketdata.db with data)
# Create a run via the web UI with Venue=tape, then check:
# GET /api/backtest/analytics/reconcile?left=<tape-run-id>&right=<replay-run-id>
```

## 7. Recommended focus for next session — T3 + T3 → T4 → T5

### Priority 1: T3/F1 — Spread on fills
This is the blocking fidelity item. Implementation plan:
1. Both adapters: entry fill at `close + halfSpread` for longs, `close - halfSpread` for shorts.
2. Exit detection shifts: shorts' SL/TP detection uses ask (OHLC + halfSpread).
3. `ComputeCosts` already handles mid-price PnL — the exit price needs to be spread-adjusted too so
   costs reflect the spread-adjusted fill.
4. Expect characterization tests to break — will need baseline refresh.
5. Golden (kernel-vs-imperative) should stay byte-identical since both use the same adapter.
6. Use `SymbolInfo.TypicalSpread / 2` as `halfSpread`. Per-bar spread (Q3) is a later refinement.

### Priority 2: T3/F4 — Gap-through slippage
Simple: in `DetectSlTpExit` / venue SL detection, if fine bar OPENS beyond stop, fill at open price.

### Priority 3: T3/B5 — Limit expiry in decision bars
Track last decision bar time; only decrement `BarsRemaining` when decision window advances.

### Priority 4: T3/F2 — Intrabar equity
Track min/max floating-equity while scanning fine bars; emit worst-case `AccountUpdate` per decision bar.

### Then: T5 — Sweep runner design
See T5-PLAN below. This is large enough to be its own iteration.

## 8. T5-PLAN — Sweep runner

### Goal
Execute a matrix of (strategy × parameters × symbol × timeframe) as tape backtests with bounded parallelism.
Content-address reuse; journal thinning; results grid.

### Design sketch
- **`SweepRequest`:** `{ strategies[], symbols[], timeframes[], parameterGrid: Dictionary<string, decimal[]>, from, to, venue="tape" }`
- **`SweepRunner`:** Singleton service. Accepts request, expands to cells (every combination), executes
  with bounded `SemaphoreSlim(N=4)`. Each cell creates a fresh inner host.
- **Content-addressing:** Each cell has `(DatasetId, ConfigSetId, Seed)`. Before running, check if a completed
  run already exists with the same identity → skip. Cells that exist but are incomplete → re-run.
- **Journal thinning:** Pass `skipJournal: true` to the inner host config — engine skips `StepRecord` writes,
  keeps trades + summary + equity.
- **Parallelism:** All cells share read-only `IMarketDataStore`. Per-cell writes are run-scoped (each
  inner host has its own `TradingDbContext` — needs DbContextFactory, not singleton).
- **Results:** `GET /api/sweep/results/{sweepId}` returns grid: per-cell metrics (NetProfit, MaxDD, TotalTrades,
  WinRate, P(pass) if estimator wired). CSV export endpoint.
- **Progress:** `GET /api/sweep/status/{sweepId}` returns completed/total cells, elapsed, estimated remaining.

### Open questions
- Should use `IDbContextFactory<TradingDbContext>` for per-cell isolation (scoped DB contexts)?
  Current inner hosts use singleton `TradingDbContext` — may need refactor for parallelism.
- Shared `IMarketDataStore` is read-only safe via `AsNoTracking()` — verify.
- Content-address skip requires a fast lookup → `SELECT RunId FROM BacktestRuns WHERE DatasetId=X AND ConfigSetId=Y AND CompletedAtUtc IS NOT NULL`.

### When to do
After T3 (spread fix) — calibrating on a spread-free simulator optimizes for a world that doesn't exist.
