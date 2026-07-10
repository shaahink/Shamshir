# P7.4 Evidence — Traps 4+5+6 + P5.1

**Date:** 2026-07-09
**Session:** #50 (P7.4 deliver)

## Gate Battery (fresh, this session)

| Gate | Result |
|------|--------|
| Build (`dotnet build TradingEngine.slnx`) | 0 errors, 5 warnings (pre-existing net6.0 TFMs) |
| Unit (`dotnet test TradingEngine.Tests.Unit`) | 716 passed, 0 failed, 6 skipped |
| Integration (`dotnet test TradingEngine.Tests.Integration`) | 120 passed, 0 failed, 0 skipped |
| Sim-fast (`dotnet test TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"`) | 144 passed, 0 failed, 0 skipped |
| Golden (`git diff --stat **/*golden*.json`) | empty (byte-identical) |
| Architecture `EntityAuditableTests` | 1/1 passed (all entities implement IAuditableEntity) |

## Trap 4 — BlockBootstrapper writes to temp shard

- **New file:** `src/TradingEngine.Infrastructure/MarketData/BootstrapMarketDataStore.cs`
- **Approach:** Decorator over `IMarketDataStore` that intercepts `WriteBarsAsync` for sources prefixed `bootstrap-` → stores in `ConcurrentDictionary` in-memory. `ReadBarsAsync` merges bootstrap bars with real store results.
- **DI registration:** `ServiceRegistration.cs:106` now registers `BootstrapMarketDataStore(sp.GetRequiredService<SqliteMarketDataStore>())` as `IMarketDataStore` singleton.
- **Effect:** BlockBootstrapController no longer pollutes the production `MarketDataShard` SQLite DB. Synthetic tapes stay in-memory.

## Trap 5 — BlockBootstrapController DateTime.UtcNow (ALREADY FIXED)

- Commit `99d5f45` (pre-P7.4) already injected `IEngineClock` and replaced all 4 `DateTime.UtcNow` calls with `_clock.UtcNow`.
- Verified: zero `DateTime.UtcNow` references in the file. No action needed.

## Trap 6 — EntityAuditableTests

- **Entities fixed (all now implement `IAuditableEntity`):**
  - `ExitCalibrationEntity` — already had `CreatedAtUtc`/`UpdatedAtUtc`; added `: IAuditableEntity`
  - `ReferenceScaleEntity` — added `: IAuditableEntity` + `CreatedAtUtc`/`UpdatedAtUtc`
  - `StrategyCellParkEntity` — added `: IAuditableEntity` + `CreatedAtUtc`/`UpdatedAtUtc`
  - `VenueSessionEntity` — added `: IAuditableEntity` + `CreatedAtUtc`/`UpdatedAtUtc`
  - `WalkForwardJobEntity` — added `: IAuditableEntity` + `UpdatedAtUtc` (already had `CreatedAtUtc`)
  - `WalkForwardWindowResultEntity` — added `: IAuditableEntity` + `CreatedAtUtc`/`UpdatedAtUtc`
- **Migrations:** M48 (ReferenceScales), M49 (StrategyCellParks), M50 (VenueSessions + WalkForwardJobs + WalkForwardWindowResults)
- **Test result:** `EntityAuditableTests.All_persistence_entities_implement_IAuditableEntity` — PASSED

## P5.1 — RunQueryService status dedup

- **File:** `src/TradingEngine.Web/Services/RunQueryService.cs:53-56`
- **Change:** Replaced inline status ternary with `RunStatusResolver.Resolve(isCompleted, errorMessage, warningsJson)`.
- **Added:** `using TradingEngine.Domain;` for `RunStatusResolver`.
- **Effect:** `GetRunsAsync` now uses the centralized status resolver (consistent with `GetRunAsync` which already used it via `ResolveStatus` helper).

## Pre-existing (not caused by P7.4)

- `EnginePurityTests.Engine_has_no_ILogger_no_DateTimeNow` — fails on `EngineReducer.ReconcileToVenue(simTimeUtc)` parameter type `DateTime`. Pre-existing architecture test failure, not in standard gate battery.
