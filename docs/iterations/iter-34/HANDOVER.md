# Iter-34 — UI Completion — HANDOVER

**Branch:** `iter/34-ui-completion`  
**Base:** `iter/33-angular-spa` (commit iter-33b)  
**Date:** 2026-06-19  
**State:** All phases delivered. Build green. Unit tests: 207 pass, 0 fail.

---

## 1. What Was Delivered

### Phase 1: Backend data-path fixes

| Fix | Files | Description |
|-----|-------|-------------|
| **Run-level cost aggregation** | `BacktestRunEntity.cs`, `IBacktestRunRepository.cs`, `SqliteBacktestRunRepository.cs`, `RunDetailResponse.cs`, `RunListResponse.cs`, `RunQueryService.cs`, `BacktestOrchestrator.cs` | Added `GrossPnL`, `CommissionTotal`, `SwapTotal` columns to `BacktestRunEntity`. `GetTradeStatsAsync` now aggregates per-trade costs. DTOs and query service map new fields. EF migration `AddRunCostColumns`. |
| **Reconciliation fix** | `BacktestOrchestrator.cs` | Catch path now calls `GetTradeStatsAsync` instead of writing zero-filled `TradeStats(0,0,0,0,0)`. |
| **Progress bar accuracy** | `BacktestOrchestrator.cs` | `state.BarsTotal` updated to actual `adapter.BarCount` after replay completes. Removed hard 99% cap — now reaches 99.9% when all bars processed. |
| **EffectExecutor cost fallback** | `EffectExecutor.cs` | Net fallback now computes `gross - commission - swap` instead of `gross`. |
| **Partial close cost stamping** | `BacktestReplayAdapter.cs` | `ClosePartialPositionAsync` now stamps `GrossProfit`/`Commission`/`Swap`/`NetProfit` on `ExecutionEvent` via `ComputeCosts`. |
| **Trade detail Timeframe** | `TradesController.cs` | Already resolved from run's `Period` column (iter-33b fix). |

### Phase 2: Risk/FTMO/Governor DB storage

| Item | Files |
|------|-------|
| **Entities** | `RiskProfileEntity.cs`, `PropFirmRuleSetEntity.cs`, `GovernorOptionsEntity.cs` — each stores full config as JSON blob |
| **Stores** | `IRiskProfileStore`/`SqliteRiskProfileStore`, `IPropFirmRuleSetStore`/`SqlitePropFirmRuleSetStore`, `IGovernorOptionsStore`/`SqliteGovernorOptionsStore` |
| **Seeders** | `RiskProfileSeeder`, `PropFirmRuleSetSeeder`, `GovernorOptionsSeeder` — idempotent, from JSON config files |
| **Controllers** | `RiskProfilesController` (rewritten for DB), `PropFirmRulesController` (new), `GovernorOptionsController` (new) — all with `GET`/`PUT`/`POST`/`DELETE` + `/duplicate` |
| **Orchestrator** | `BuildLoadedConfigFromDbAsync` now loads risk/prop-firm/governor from DB stores |
| **Migration** | `AddConfigProfiles` — creates `RiskProfiles`, `PropFirmRuleSets`, `GovernorOptions` tables |
| **DI/Middleware** | Stores registered in `ServiceRegistration.cs`; seeders called at startup in `MiddlewarePipeline.cs` |

### Phase 3: Strategy field editor

| File | Change |
|------|--------|
| `strategy-detail.component.ts` | **Complete rewrite**: per-field forms replacing raw JSON textarea. Fields: `displayName`, `riskProfileId` (dropdown), `regimeFilter` (5 checkboxes), `orderEntry` (method dropdown + 4 numeric), `positionManagement` (6 collapsible sections: SL, TP, Breakeven, Trailing, Ride, Partial TP), `reentry` (toggle + 3 ints), `parameters` (dynamic key-value form). **Excluded**: timeframe and symbols (read-only display). |
| `StrategiesController.cs` | Added `POST /api/strategies/{id}/duplicate` endpoint |
| `api.types.ts` | Added full `StrategyDetail` interface |

### Phase 4: Risk/FTMO/Governor Angular UI pages

| Page | Route | Features |
|------|-------|----------|
| **Risk Profiles list** | `/risk-profiles` | Card grid with stats (risk%, DD%, positions, sizing). Copy + New buttons. |
| **Risk Profile detail** | `/risk-profiles/:id` | Full field editor (all 18 fields). Conditional fields for sizing method. Save/Delete/Duplicate. |
| **FTMO Rules list** | `/prop-firm-rules` | Card grid with stats (daily loss%, total loss%, profit target%, min days). Copy + New. |
| **FTMO Rule detail** | `/prop-firm-rules/:id` | Full field editor (all fields including news windows, weekend rules). Save/Delete/Duplicate. |
| **Governor Options** | `/governor-options` | Field editor + save. Loss band arrays edited as JSON strings. |

Nav links added in `app.component.ts`: **Risk**, **FTMO**, **Governor**.

### Phase 5: Chart & monitor fixes

| Fix | File | Description |
|-----|------|-------------|
| **Dual-line chart** | `equity-chart.component.ts` | Added `showBalance` input + blue balance line series. `ChartPoint` now has optional `balance` field. |
| **Report chart** | `run-report.component.ts` | Chart now shows balance line. Cost tiles use server-provided `grossPnL`/`commissionTotal`/`swapTotal` from run detail (with fallback to per-trade sums). |
| **Monitor init** | `run-monitor.component.ts` | Equity chart passes balance data. |
| **Report cost display** | `run-report.component.ts` | Added `grossDisplay`/`commDisplay`/`swapDisplay` computed signals with server-provided fields. |

### Phase 6: Angular code quality

| File | Change |
|------|--------|
| `.eslintrc.json` | New — config with `@typescript-eslint` + `@angular-eslint` plugins |
| `angular.json` | Added `lint` builder |
| `package.json` | Added `lint` script + ESLint devDependencies |
| `tsconfig.json` | Added `forceConsistentCasingInFileNames`, relaxed `noPropertyAccessFromIndexSignature` (needed for dynamic field access) |

---

## 2. How to Run

```powershell
# Build Angular SPA
cd web-ui; npm run build

# Run the app (single origin)
dotnet run --project src/TradingEngine.Web --launch-profile https

# Tests
dotnet test tests/TradingEngine.Tests.Unit   # 207 pass
```

---

## 3. New API Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| `GET/PUT/DELETE` | `/api/risk-profiles/{id}` | CRUD risk profiles |
| `POST` | `/api/risk-profiles` | Create new profile |
| `POST` | `/api/risk-profiles/{id}/duplicate` | Clone profile |
| `GET/PUT/DELETE` | `/api/prop-firm-rules/{id}` | CRUD prop-firm rules |
| `POST` | `/api/prop-firm-rules` | Create new rule set |
| `POST` | `/api/prop-firm-rules/{id}/duplicate` | Clone rule set |
| `GET/PUT` | `/api/governor-options` | Read/update governor |
| `POST` | `/api/strategies/{id}/duplicate` | Clone strategy |

---

## 4. EF Migrations (2 new)

1. `20260619044809_AddRunCostColumns` — adds `GrossPnL`, `CommissionTotal`, `SwapTotal` to `BacktestRuns`
2. `20260619045126_AddConfigProfiles` — creates `RiskProfiles`, `PropFirmRuleSets`, `GovernorOptions` tables

Applied automatically at startup via `MiddlewarePipeline.UseShamshir()`.

---

## 5. Files Changed (summary)

**Backend C# (20 files modified + 16 new):**
- Domain: `IBacktestRunRepository.cs`
- Host: `EffectExecutor.cs`
- Infrastructure: `BacktestReplayAdapter.cs`, `BacktestRunEntity.cs`, `TradingDbContext.cs`, `SqliteBacktestRunRepository.cs`
- Web: `BacktestOrchestrator.cs`, `RunQueryService.cs`, `RunDetailResponse.cs`, `RunListResponse.cs`, `RiskProfilesController.cs`, `StrategiesController.cs`, `MiddlewarePipeline.cs`, `ServiceRegistration.cs`
- New: 3 entities, 3 store interfaces, 3 store implementations, 3 seeders, 2 controllers, 2 EF migrations

**Angular (8 files modified + 9 new):**
- Modified: `app.component.ts`, `app.routes.ts`, `api.types.ts`, `strategy-detail.component.ts`, `run-report.component.ts`, `run-monitor.component.ts`, `equity-chart.component.ts`, `angular.json`, `tsconfig.json`, `package.json`
- New: 3 feature groups (risk-profiles, prop-firm-rules, governor), `.eslintrc.json`

---

## 6. Known Gaps (not yet verified)

- **cTrader storage path**: All cost/reconciliation/progress-bar fixes are static-tested only. Needs a real cTrader backtest to confirm bars/equity/journal actually persist and costs are correct.
- **Timeframe/symbol multi-select**: The New-Backtest UI sends `symbols[]` and `periods[]` arrays. Backend processes them for cTrader path but replay is single-symbol only. Run record stores only singular `Symbol`/`Period`.
- **ESLint packages not installed**: `npm install` needed before `npm run lint` will work.
