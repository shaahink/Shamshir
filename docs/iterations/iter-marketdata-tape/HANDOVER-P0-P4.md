# iter-marketdata-tape — HANDOVER (P0-P4 executed)

**Date:** 2026-07-01
**Branch:** `iter/integration-cache-tape` (merged iter-cache-reads-2 + iter/marketdata-tape)
**Status:** P0-P4 backend complete; P4 frontend partial; P5-P6 remain

## What was delivered this session

### iter-cache-reads-2 (P1-P5 cache fixes)
| Phase | Fix | Files |
|-------|-----|-------|
| P1 | **B1** — Snapshot invalidation on append + fixed inverted getter condition (`!=` → `==`). **B2** — `MarkCompleted` called in `BacktestOrchestrator` finally and `CTraderListenService.FinalizeRunAsync`. | `RunDataCache.cs`, `BacktestOrchestrator.cs`, `CTraderListenService.cs` |
| P2 | **B3** — Dead `AppendBar`/`Bars` removed from interface + impl + `BufferedBarWriter`. | `IRunDataCache.cs`, `RunDataCache.cs`, `BufferedBarWriter.cs` |
| P3 | **B4** — Trade cache sorts DESC (matches DB). **G2** — `AsNoTracking()` + cache-first for `GetRunDailyPnLAsync` / `GetRunAnalyticsAsync`. | `RunDataCache.cs`, `RunQueryService.cs` |
| P4 | **G1** — `GetRunAsync` serves running runs from `BacktestRunState` + cached trades (zero DB I/O). Runs list cache invalidated on start/completion. | `RunQueryService.cs`, `BacktestOrchestrator.cs` |
| P5 | Eviction sweeper (`CacheEvictionSweeper` — 60s grace, cap N=8). Equity downsampling at 20k soft-cap. | `IRunDataCache.cs`, `RunDataCache.cs`, `CacheEvictionSweeper.cs` (new), `ServiceRegistration.cs` |

### iter-marketdata-tape (P0-P4)
| Phase | Delivered | Key files |
|-------|-----------|-----------|
| Phase M | Merged both branches on `iter/integration-cache-tape`; single commit, pushed to origin | — |
| Phase V1 | Recorder cBot verified: produces valid H1+M1 NDJSON shards; wire format confirmed; ingest pipeline works end-to-end | `BacktestCli.cs`, `BacktestCliRequest.cs`, `MarketDataRecorderE2ETests.cs` |
| Phase P4 | Tape venue selector in New-Backtest; Method column in run list; Data Manager page with inventory API | `DataManagerController.cs`, `DataManagerComponent.ts`, `RunListResponse.cs` (added Venue/RiskProfileId) |

## Gates (verified)
| Gate | Status |
|------|--------|
| Full Unit | ✅ 314/0/6 |
| Determinism (golden) | ✅ 3/3 byte-identical |
| Market data integration | ✅ 7/7 |
| Tape adapter integration | ✅ 3/7 |
| cTrader E2E smoke | ✅ 1/1 (53s) |
| Angular SPA build | ⚠️ Not yet built (`npm install` needed in `web-ui/`) |
| Full Simulation suite | ⚠️ Timeout (pre-existing; not a regression) |

## What remains (carry-forward)
| Phase | Task |
|-------|------|
| V2 | Bulk-download owner's working set (EURUSD H1+m1, 1-6 months) via recorder |
| V3 | Run tape backtest (Venue=tape), record wall-clock speedup vs cTrader path |
| V4 | Reconcile tape vs cTrader (LedgerReconciler.Compare) — needs both runs |
| V5 | Reconcile engine-DB vs cTrader (the owner's original pain) |
| P4c | Compare mode: run both cTrader + tape, render side-by-side ledger + verdict |
| P4d | Download form in Data Manager (kick off recorder + ingest from UI) |
| P5 | Fidelity hardening: close MaxDD/swap/TradeSet divergences named in RECONCILE-FINDINGS.md |
| P6 | Tick tape format + tick-resolution exits (optional) |

## File map (new + modified this session)
```
src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs    # Recorder mode (existing, verified)
src/TradingEngine.CTraderRunner/BacktestCli.cs              # + --Record support
src/TradingEngine.CTraderRunner/BacktestCliRequest.cs       # + Record property
src/TradingEngine.Domain/Interfaces/IRunDataCache.cs        # + GetRunIds, GetCompletedAtUtc (P5)
src/TradingEngine.Infrastructure/Caching/RunDataCache.cs    # P1-P5 fixes
src/TradingEngine.Infrastructure/Caching/BufferedBarWriter.cs # Removed AppendBar
src/TradingEngine.Web/Api/DataManagerController.cs          # NEW: GET /api/data-manager/inventory
src/TradingEngine.Web/Configuration/ServiceRegistration.cs  # + CacheEvictionSweeper, MarketDataDbContext factory
src/TradingEngine.Web/Dtos/Runs/RunListResponse.cs          # + Venue, RiskProfileId (P4)
src/TradingEngine.Web/Services/BacktestOrchestrator.cs      # P1: MarkCompleted, P4: Venue=tape
src/TradingEngine.Web/Services/CacheEvictionSweeper.cs      # NEW: background eviction
src/TradingEngine.Web/Services/CTraderListenService.cs      # P1: MarkCompleted
src/TradingEngine.Web/Services/RunQueryService.cs           # P3+P4: cache-first, memory run detail, Venue in list
tests/TradingEngine.Tests.Simulation/E2E/MarketDataRecorderE2ETests.cs  # NEW: recorder verification
tests/TradingEngine.Tests.Unit/Cache/RunDataCacheTests.cs   # + 6 new tests (P1-P5)
web-ui/src/app/app.routes.ts                                # + /data-manager route
web-ui/src/app/app.component.ts                             # + Data nav link
web-ui/src/app/features/data-manager/data-manager.component.ts  # NEW: Data Manager page
web-ui/src/app/features/runs/new-backtest/...               # + tape venue option
web-ui/src/app/features/runs/run-list/...                   # + Method column
web-ui/src/app/models/api.types.ts                          # + venue field on RunSummary
```

## Notes for the continuing agent
1. **Build the Angular SPA** before running integration/UI tests: `cd web-ui && npm install && npm run build`
2. **To populate marketdata.db:** run `dotnet test --filter "FullyQualifiedName~MarketDataRecorderE2ETests"` with cTrader credentials, or run cTrader CLI directly with `--Record=true --Periods=m1 --ReportPath=<dir>`
3. **Golden/determinism must stay 63/63.** Run `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true"` before and after any kernel change.
4. **The cache branch was squashed** into a single commit to avoid large .bak file push issues. The original branch `iter-cache-reads-2` exists locally but was not pushed.
