# iter-cache-reads — HANDOVER

**Completed:** 2026-07-01
**Branch:** `iter/cache-reads` (merged to `develop`)
**Base:** `develop` (post iter-ctrader-capture merge)

---

## What was built

### RunDataCache — In-Memory Write-Through Cache

A consolidated read layer that eliminates SQLite lock contention between kernel writes and UI reads during live backtests.

### Phase 1 — Quick Wins
- `GetRunBars` capped at 5,000 events (was unbounded — biggest lock hog)
- `GET /api/runs` cached via `IMemoryCache` with 2s expiry

### Phase 2 — Write-Through Cache
- `IRunDataCache` interface (Domain) + `RunDataCache` implementation (Infrastructure)
- Writers push journal/equity/trades/bars to cache after each DB `SaveChangesAsync`
- `RunQueryService` checks cache first → DB fallback
- Journal ring-buffered at 10,000 entries per run
- Cache singleton shared between Web host and inner engine host via `EngineHostOptions`

### Tests
- 19 cache unit tests: write-through, read-through, ring-buffer eviction, thread safety, completion preservation, per-run isolation
- All 290 existing unit tests still green (0 failures)

---

## File map

| File | Role |
|------|------|
| `src/TradingEngine.Domain/Interfaces/IRunDataCache.cs` | Cache interface — Append/Get methods for journal/equity/trades/bars |
| `src/TradingEngine.Infrastructure/Caching/RunDataCache.cs` | Implementation — ConcurrentDictionary + ring-buffered journal |
| `src/TradingEngine.Web/Services/RunQueryService.cs` | Cache-first reads + IMemoryCache for runs list + capped bars |
| `src/TradingEngine.Host/ScopedStepRecordSink.cs` | Resolves cache from inner host scope |
| `src/TradingEngine.Infrastructure/.../SqliteStepRecordSink.cs` | Pushes journal batch to cache after DB save |
| `src/TradingEngine.Infrastructure/.../TradePersistenceHandler.cs` | Pushes trade to cache after DB save |
| `src/TradingEngine.Infrastructure/.../EquityPersistenceHandler.cs` | Pushes equity batch to cache after DB save |
| `src/TradingEngine.Infrastructure/Caching/BufferedBarWriter.cs` | Pushes bars to cache after bulk insert |
| `src/TradingEngine.Domain/EngineHostOptions.cs` | Added `IRunDataCache? RunDataCache` |
| `src/TradingEngine.Host/EngineServiceCollectionExtensions.cs` | Registers cache in inner host DI |
| `src/TradingEngine.Web/Configuration/ServiceRegistration.cs` | Registers `IRunDataCache` singleton |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Passes cache to inner host via options |
| `tests/TradingEngine.Tests.Unit/Cache/RunDataCacheTests.cs` | 19 cache tests |

---

## Architecture

```
                    Web Host                          Inner Engine Host
                    ────────                          ─────────────────

  POST /api/runs   → BacktestOrchestrator  ──► EngineHostOptions { RunDataCache }
                        │                                    │
                        │ registers                          │ DI singleton
                        ▼                                    ▼
  GET /api/runs/{id} → RunQueryService  ←── IRunDataCache ───┐
       /trades          │ cache-first                        │
       /equity          │ DB-fallback                        │ push after
       /journal         ▼                                    │ SaveChangesAsync
                     IRunDataCache ◄── SqliteStepRecordSink
                      (singleton)  ◄── TradePersistenceHandler
                                   ◄── EquityPersistenceHandler
                                   ◄── BufferedBarWriter
```

---

## What's NOT done (carry-forward)

1. **Phase 3 — DB Tuning**: Dedicated read-only SQLite connection with `PRAGMA query_only`. Not yet needed if Phase 1+2 eliminate contention.

2. **Auto-eviction timer**: Completed runs stay in cache indefinitely. A background timer should auto-evict after 60s. Currently, `Evict` must be called explicitly (Orchestrator calls it on run end, but only if set up — verify).

3. **Memory pressure bounds**: No total cache size limit. For very long backtests with thousands of trades and tens of thousands of bars, the unbounded `ConcurrentBag<EquitySnapshot>` and `ConcurrentBag<Bar>` collections could grow large. Consider adding a soft cap.

4. **Daily PnL / Analytics from cache**: These endpoints still query DB directly. They could compute from cached trades if the cache has them.

5. **Runs list cache invalidation**: `InvalidateRunsCache()` is defined but not yet called on run start/complete. The 2s expiry means it self-heals, but explicit invalidation on state changes would be cleaner.

---

## Verification

```powershell
# Unit tests
dotnet test tests/TradingEngine.Tests.Unit   # 290 pass, 0 fail, 6 skip

# Cache-specific tests
dotnet test tests/TradingEngine.Tests.Unit --filter "FullyQualifiedName~Cache"  # 19 pass
```
