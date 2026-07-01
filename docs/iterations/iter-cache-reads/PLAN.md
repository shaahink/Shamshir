# iter-cache-reads — RunDataCache: In-Memory Write-Through Cache for Backtest Reads

**Written:** 2026-07-01
**Branch base:** `develop` (post iter-ctrader-capture merge)
**Owner:** AI agent (OpenCode)

---

## Problem

During a live backtest, the UI becomes unresponsive. Root cause: SQLite lock contention.

- The kernel writes frequently: journal batches of 500, equity every 5s, bars every 500 bars
- The run-report page fires 8 parallel DB reads on mount
- `GetRunBars` streams ALL journal entries unbounded — holding a shared lock for an extended period
- `GET /api/runs` (the runs list) hits the DB on every navigation
- **Zero caching** exists — `IMemoryCache` is registered but unused

SQLite WAL mode allows concurrent readers during writes, but the sheer volume of parallel reads competing with write transactions causes blocking.

---

## Solution

### Phase 1 — Quick Wins

| Fix | Mechanism |
|-----|-----------|
| Cap `GetRunBars` at 5,000 events | `MaxBarEvents = 5000` in `RunQueryService` — breaks stream after limit. The report page already limits journal to 200 and bar-decisions to 500. |
| Cache `GET /api/runs` | `IMemoryCache` with 2-second sliding expiry. Key `runs:all`. Invalidated on run start/complete. |

### Phase 2 — RunDataCache (the core fix)

A write-through in-memory cache that sits between the kernel writers and web API readers:

```
┌─ Kernel writes (background) ─────────────────────────────────┐
│                                                              │
│  SqliteStepRecordSink    ──► DB INSERT ──► IRunDataCache     │
│  TradePersistenceHandler ──► DB INSERT ──► IRunDataCache     │
│  EquityPersistenceHandler──► DB INSERT ──► IRunDataCache     │
│  BufferedBarWriter       ──► DB INSERT ──► IRunDataCache     │
│                                                              │
└──────────────────────────────────────────────────────────────┘
                                              │
                                              ▼
┌─ Web API reads ─────────────────────────────────────────────┐
│                                                              │
│  GET /api/runs/{id}/trades  ──► cache ?? DB (no lock)       │
│  GET /api/runs/{id}/equity  ──► cache ?? DB (no lock)       │
│  GET /api/runs/{id}/journal ──► cache ?? DB (no lock)       │
│  GET /api/runs/{id}/bars    ──► cache ?? DB (capped 5K)     │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

**Key properties:**
- **Write-through**: Writers push to BOTH DB and cache. Cache is always consistent.
- **Read-through**: API checks cache first → cache miss → DB fallback
- **Ring-buffered journal**: Max 10,000 `StepRecord` entries per run, evicts oldest
- **Singleton shared via DI**: Web host creates the cache, passes it to inner host via `EngineHostOptions.RunDataCache`
- **Self-evicting**: When a run completes, `MarkCompleted` is called. Cache remains for 60s grace, then `Evict` removes it.

### Architecture

```
TradingEngine.Domain/
  Interfaces/IRunDataCache.cs      ← interface (no deps)

TradingEngine.Infrastructure/
  Caching/RunDataCache.cs          ← implementation (ConcurrentDictionary-based)

TradingEngine.Web/
  Configuration/ServiceRegistration.cs  ← registers singleton IRunDataCache
  Services/RunQueryService.cs           ← cache-first reads, IMemoryCache for /api/runs

TradingEngine.Host/
  EngineServiceCollectionExtensions.cs  ← registers cache in inner host DI
  ScopedStepRecordSink.cs               ← resolves cache from scope

TradingEngine.Infrastructure/
  Persistence/Repositories/SqliteStepRecordSink.cs    ← pushes journal to cache
  Persistence/TradePersistenceHandler.cs               ← pushes trades to cache
  Persistence/EquityPersistenceHandler.cs              ← pushes equity to cache
  Caching/BufferedBarWriter.cs                         ← pushes bars to cache
```

### Cache data per run

| Data | Collection | Max Size | Push Trigger |
|------|-----------|----------|--------------|
| Journal entries | `ConcurrentQueue<StepRecord>` (ring) | 10,000 | Per batch (500) |
| Equity snapshots | `ConcurrentBag<EquitySnapshot>` | Unlimited | Every 5s batch |
| Trades | `ConcurrentBag<TradeResult>` | Unlimited | Per trade |
| Bars | `ConcurrentBag<Bar>` | Unlimited | Per batch (500) |

---

## Files changed

### New files

| File | Purpose |
|------|---------|
| `src/TradingEngine.Domain/Interfaces/IRunDataCache.cs` | Cache interface |
| `src/TradingEngine.Infrastructure/Caching/RunDataCache.cs` | Implementation |
| `tests/TradingEngine.Tests.Unit/Cache/RunDataCacheTests.cs` | 12 unit tests |
| `docs/iterations/iter-cache-reads/PLAN.md` | This file |

### Modified files

| File | Change |
|------|--------|
| `EngineHostOptions.cs` | Added `IRunDataCache? RunDataCache` property |
| `EngineServiceCollectionExtensions.cs` | Register cache singleton if present |
| `ScopedStepRecordSink.cs` | Resolve cache from scope, pass to sink |
| `SqliteStepRecordSink.cs` | Push journal to cache after `SaveChangesAsync` |
| `TradePersistenceHandler.cs` | Push trade to cache after save |
| `EquityPersistenceHandler.cs` | Push equity batch to cache after save |
| `BufferedBarWriter.cs` | Push bars to cache after bulk insert |
| `RunQueryService.cs` | Cache-first reads for trades/equity/bars; IMemoryCache for runs list; cap GetRunBars at 5K |
| `ServiceRegistration.cs` | Register `IRunDataCache` singleton |
| `BacktestOrchestrator.cs` | Pass cache to inner host via `EngineHostOptions` |
| `CTraderListenService.cs` | Pass cache to inner host via `EngineHostOptions` |

---

## Test results

- Unit tests: **277 + 19 = 296 pass**, 0 fail, 6 skipped
- Cache tests verify: write-through, read-through, ring-buffer eviction, thread safety, completion preservation, per-run isolation

---

## Carry-forward

1. **Phase 3 (DB tuning)** — dedicated read-only `SqliteConnection` + `PRAGMA query_only` — not yet needed if Phase 1+2 eliminate contention
2. **Auto-eviction timer** — currently, completed runs stay in cache until `Evict` is explicitly called. A background timer could auto-evict after 60s.
3. **Memory pressure monitoring** — no bounds checking on total cache size. For very long backtests, equity/bars could grow large.
