# Progress Metrics — iter-tape-trust

**Updated:** 2026-07-02
**Branch:** `iter/tape-trust`

## Gates (2026-07-02, final)

| Gate | Command | Result |
|------|---------|--------|
| Build | `dotnet build` | 0 errors |
| Unit | `dotnet test tests/TradingEngine.Tests.Unit` | 314 pass / 0 fail / 6 skip |
| Integration | `dotnet test tests/TradingEngine.Tests.Integration` | 91 pass / 0 fail |
| Golden/Determinism | `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden\|FullyQualifiedName~Characterization\|FullyQualifiedName~Acceptance\|FullyQualifiedName~Lifecycle\|FullyQualifiedName~Deterministic\|FullyQualifiedName~Equivalence\|FullyQualifiedName~Journal)"` | 63 pass / 0 fail |
| cTrader E2E | `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true&FullyQualifiedName!~NetMQBridge"` | PENDING (cBot rebuild needed) |

## Speed baseline (informal, from review)

| Path | Bars | Wall-clock | Bars/sec |
|------|------|-----------|----------|
| Tape replay | 170 (EURUSD M1) | ~531 ms | ~320 |
| cTrader CLI | 170 (EURUSD M1) | ~33 s | ~5 |

## Bugs fixed (B1-B11)

| ID | Fixed | Phase |
|----|-------|-------|
| B1 | ✅ | T0 |
| B2 | ✅ | T0 |
| B3 | ✅ | T1 |
| B4 | ✅ | T1 |
| B5 | ✅ | T3 |
| B6 | ✅ | T1 |
| B7 | ✅ | T1 |
| B8 | ✅ | T1 |
| B9 | ✅ | T0 |
| B10 | ✅ | T1 |
| B11 | ✅ (partial) | Housekeeping |

## Fidelity gaps (F1-F8)

| ID | Fixed | Phase | Notes |
|----|-------|-------|-------|
| F1 | ✅ | T3 | Spread on fills in both replay venues |
| F2 | ✅ | T3 | Intrabar equity min tracking |
| F3 | ⚠️ | Deferred | Measure first via V4 |
| F4 | ✅ | T3 | Gap-through slippage |
| F5 | ⚠️ | Open | Commission half-at-open; minor |
| F6 | ⚠️ | Open | Limit+SL same fine bar; document |
| F7 | ⚠️ | Open | Fine bars in decision-TF gaps |
| F8 | ✅ | T0 | Exit resolution surfaced |

## V-phases

| Phase | Status |
|-------|--------|
| V2 (owner's working set) | Not done — needs cTrader |
| V3 (speed baseline) | Informal measurement only |
| V4 (tape vs cTrader reconcile) | Infrastructure ready — needs runs |
| V5 (DB vs cTrader report) | Infrastructure ready — needs runs |

## Files changed

```
src/TradingEngine.Domain/Interfaces/IReplayVenue.cs          (new)
src/TradingEngine.Domain/EngineHostOptions.cs                 (+SkipJournal)
src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs   (F1, F4, B6, B9, B10, IReplayVenue)
src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs       (F1, F2, F4, B5, B6, B9, B10, ExitResolution, IReplayVenue)
src/TradingEngine.Infrastructure/MarketData/SqliteMarketDataStore.cs  (B8)
src/TradingEngine.Infrastructure/MarketData/MarketDataIngester.cs     (B8)
src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs              (B3, B7)
src/TradingEngine.Web/Services/BacktestOrchestrator.cs               (B1, B1b, B2, T4, SkipJournal)
src/TradingEngine.Web/Services/RunQueryService.cs                    (B2, ExitResolution)
src/TradingEngine.Web/Services/LedgerReconcileService.cs    (new, T2)
src/TradingEngine.Web/Services/SweepRunnerService.cs        (new, T5)
src/TradingEngine.Web/Services/DownloadJobService.cs        (new, T1/B4)
src/TradingEngine.Web/Api/DataManagerController.cs                   (B4)
src/TradingEngine.Web/Api/BacktestAnalyticsController.cs             (T2)
src/TradingEngine.Web/Api/SweepController.cs                (new, T5)
src/TradingEngine.Web/Dtos/Runs/RunDetailResponse.cs                 (ExitResolution)
src/TradingEngine.Web/Configuration/ServiceRegistration.cs           (DI)
src/TradingEngine.Host/EngineServiceCollectionExtensions.cs          (SkipJournal)
.gitignore                                                           (data dir)
AGENTS.md                                                            (branch + reading list)
docs/iterations/iter-tape-trust/PLAN.md                     (new)
docs/iterations/iter-tape-trust/HANDOVER.md                  (new)
docs/iterations/iter-marketdata-tape/HANDOVER-REVIEW.md      (new)
docs/iterations/iter-marketdata-tape/FULL-HANDOVER.md                (§11/§13 pointers)
docs/QUANT-ROADMAP.md                                         (new)
docs/audit/RECONCILE-FINDINGS.md                                     (F1-F4 pre-registered)
docs/audit/PROGRESS.md                                       (new, this file)
DECISIONS.md                                                         (D85-D96)
```
