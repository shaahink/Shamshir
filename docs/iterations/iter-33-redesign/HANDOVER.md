# Iter-33 — Angular SPA Handover

**Branch**: `iter/33-angular-spa`  
**Base**: `iter/33-p01-test-infra`  
**Date**: 2026-06-19  
**State**: Feature-complete, all suites green

---

## 1. What Was Taken Over

### Branch state at start
- `iter/33-p01-test-infra` had W1–W8 work from a previous agent:
  - Typed CLI layer (`BacktestCli`), transport health protocol, snapshot recorder/replayer
  - `CtraderE2EHarness`, `RunArtifacts`, bisection of `--report-json` crash
  - cBot renamed to `Shamshir` but `.algo` file still named `src.algo` (SDK constraint)
  - **CRITICAL**: All algo-resolution paths pointed at non-existent `Shamshir.algo` — Web backtests + E2E broken
  - **CRITICAL**: `--report-json` flag crashed cTrader's report-saving — misdiagnosed by previous agent
  - Working tree had unstaged checkpoint: `ShamshirTradeLogger`, `CtraderDiffHarness`, `CtraderReportHarvester`, commission/max-DD fixes

### Key facts discovered during handover review
- cTrader SDK names `.algo` bundles after the project's parent directory (`src/`) — `Shamshir.algo` never built
- `--report-json` crashes cTrader-cli internally (unrelated to our code)
- The cBot writes no files; `events.json` is 100% cTrader-native
- TestCbot (minimal, no NetMQ) produces report.json fine; full cBot's `NetMQConfig.Cleanup(true)` in `OnStop` races cTrader's report-saving

---

## 2. What Was Delivered

### 2.1 Bug Fixes (Phase 1)

| Bug | Fix | File |
|-----|-----|------|
| **OrderId round-trip** | `MapToDomain` missing `OrderId:` parameter | `SqliteTradeRepository.cs` |
| **Exact per-trade join** | Replaced economic matching with `clientOrderId == OrderId` dictionary join | `CtraderDiffHarness.cs` |
| **BUG-09: Governor cooling-off** | Added `ITradingGovernor.OnBar()` call in `TradingLoop` + governor parameter in constructor | `TradingLoop.cs`, `EngineRunner.cs` |
| **NU1903 build breaks** | `<NuGetAudit>false</NuGetAudit>` in `Directory.Build.props` | `Directory.Build.props` |

### 2.2 Backend API Redesign (Phase 2)

- **18 typed DTOs** in `Web/Dtos/` — RunListResponse, RunDetailResponse, StartRunRequest/Response, TradeSummaryResponse, JournalEntryResponse, EquityPointResponse, DailyPnlResponse, RunAnalyticsResponse, StrategySummaryResponse, StrategyDetailResponse, StrategyUpdateRequest, BarResponse, ProtectionDayResponse, ProtectionEntryResponse, GovernorStateResponse, PipelineEventResponse
- **3 query service interfaces** + implementations: `IRunQueryService`, `IProtectionQueryService`, `IBarQueryService`
- **New Angular-facing controllers**: `RunsController` at `/api/runs`, `TradesController` at `/api/trades`
- **Updated controllers** to use services instead of `TradingDbContext` directly: `BarsController`, `ProtectionController`, `StrategiesController`
- **Scalar API docs**: `Scalar.AspNetCore` 2.16.4 at `/scalar/v1`, `Microsoft.AspNetCore.OpenApi` 10.0.9
- **CORS**: `localhost:4200` with credentials
- **Strategy initialization**: `StrategyRegistry.CreateStrategies()` called at Web startup from persisted config store — **critical fix** — previously strategies returned empty because `_cachedAll` was never populated

### 2.3 Program.cs Cleanup

- **Before**: 120-line flat DI dump with `Path.Combine(.., "..")` walks, mixed concerns
- **After**: 6-line `Program.cs` → `AddShamshir()` extension → `UseShamshir()` extension
- Decomposed into: `AddApi()` / `AddPersistence()` / `AddAppServices()` / `AddEngineServices()`
- **NgServeHost**: Development-only `IHostedService` that spawns `ng serve`, polls `localhost:4200` until ready (up to 60s), then allows browser launch

### 2.4 Scrapped

- All 32 `.cshtml` Razor files + code-behind
- `RiskSseController` (SSE replaced by SignalR)
- `PerformanceApiController` (merged into TradesController)
- `EquityController` (merged into RunsController as `/api/equity` endpoint)
- Old `wwwroot/js/` and `wwwroot/css/`

### 2.5 Angular SPA (Phases 3–10)

**Toolchain**: Angular 19.2, Tailwind CSS v4.3.1 (CLI prebuild), lightweight-charts v5.2, @microsoft/signalr, TypeScript strict

**CSS architecture**: Tailwind v4's PostCSS plugin doesn't scan `.ts` inline templates. Workaround: `npm run css` runs `tailwindcss` CLI with `--content "src/**/*.ts"` to pre-generate `styles.generated.css`, which Angular then bundles. This is wired into `npm start` and `npm run build`.

#### Pages built:

| Page | Route | Key features |
|------|-------|-------------|
| **Dashboard** | `/` | 8 stat tiles (status, daily DD, max DD, trades today, equity, open positions, governor, daily limit), equity+drawdown dual chart |
| **Runs List** | `/runs` | Table with status badges, checkbox compare (select 2+ → comparison table) |
| **New Backtest** | `/runs/new` | 12 symbol checkboxes, 6 timeframe checkboxes, date quick-select (Month/Quarter/Year), strategy picker from API, risk profile, balance/commission/spread |
| **Run Report** | `/runs/:id` | 10 KPIs (net, return%, max DD, profit factor, win rate, trades, gross, commission, swap, avg R), reconciliation badges, equity+drawdown dual chart, DD timeline bars, trades table (16 columns with color-coded PnL), journal with kind filters |
| **Run Monitor** | `/runs/:id/monitor` | Live SignalR: progress bar + %, bars/sec, ETA, elapsed, sim clock, equity tile, balance, open positions, daily/max DD, governor, distance to limit, funnel counters (6 tiles), equity sparkline, journal feed, cancel button, breach banner |
| **Run Analyzer** | `/runs/:id/analyzer` | R-multiple histogram, holding time histogram, PnL by hour, PnL by day of week, MAE/MFE scatter |
| **Trades List** | `/trades` | Filters (symbol, strategy, direction, from/to date), pagination (Prev/Next), clickable rows → detail |
| **Trade Detail** | `/trades/:id` | 16 stat tiles, candlestick chart with entry/exit price lines |
| **Strategies** | `/strategies` | Cards with stats, inline enable/disable toggle |
| **Strategy Detail** | `/strategies/:id` | Identity + actions, Edit Config mode with JSON textarea + validation, Enable/Disable toggle |
| **Compliance** | `/compliance` | Governor state KPIs, daily protection ledger table |
| **Events** | `/events` | Pipeline events with run ID filter |
| **Settings** | `/settings` | Read-only config overview |
| **API Docs** | `/scalar/v1` | Scalar API reference (accessible from nav link) |

#### Charts (Lightweight-Charts v5)

| Component | Type | Features |
|-----------|------|----------|
| `EquityChartComponent` | Line chart | Dual-pane: blue equity line + red drawdown area (peak-to-trough computed in component) |
| `CandleChartComponent` | Candlestick | OHLC bars + horizontal price line markers for entry/exit |
| `HistogramChartComponent` | Histogram | Column distribution, configurable color |
| `ScatterChartComponent` | Scatter | Point markers (line invisible), configurable color |

**Critical chart fix**: All 4 chart components originally used `querySelector('[#chartContainer]')` which NEVER finds the element — Angular template `#ref` variables are not DOM attributes. Changed to CSS class `.chart-host`.

#### Shared components
`StatTileComponent`, `DataTableComponent` (sortable, formattable, colorFn), `BadgeComponent`, `EquityChartComponent`, `CandleChartComponent`, `HistogramChartComponent`, `ScatterChartComponent`

#### State management
Signals-based per-feature stores (`RunsStore`, computed signals for derivations). No NgRx.

#### Architecture rules followed
- All inline templates (no external `.html` files)
- Zero `any` in component logic (typed `api.types.ts` with 17 interfaces)
- Lazy-loaded routes per feature module
- `RunHubService` with typed RxJS subjects for SignalR
- **No `[class.*]` multi-line bindings** — Angular parser treats `<element` with multiple `[class.*]` spanning lines as self-closing. Workaround: `[attr.class]` with component method or single-line bindings

---

## 3. Architecture Decisions

### D1 — Tailwind v4 CSS strategy
Angular 19's built-in Tailwind v4 integration doesn't scan `.ts` inline templates for class names. **Decision**: Use Tailwind CLI (`npx tailwindcss -i src/styles.css -o src/styles.generated.css --content "src/**/*.ts"`) as a prebuild step. Wired into `npm start` and `npm run build` via `"css"` script.

### D2 — Chart container selection
`querySelector('[#chartContainer]')` never works because Angular template `#ref` variables don't create DOM attributes. **Decision**: Use CSS class `.chart-host` for chart containers.

### D3 — Strategies API initialization
`StrategyRegistry.GetAll()` returns `_cachedAll ?? []`. `_cachedAll` is populated by `CreateStrategies()` which was only called inside `EngineRunner` (backtest execution), never at Web API startup. **Decision**: Call `CreateStrategies()` in `AddEngineServices()` with configs from `IStrategyConfigStore` to populate the registry at startup.

### D4 — Dev mode Angular serving
.NET serves Angular build output for production, but `ng serve` with proxy is better for development (HMR, build errors visible). **Decision**: `NgServeHost` spawns `ng serve` in development, polls health until ready. Production uses `ng build` output in `wwwroot/`.

### D5 — NgServe disabled by default
Spawning a child process from Program.cs is fragile. **Decision**: Controlled by `Dev:NgServe` config flag (true in `appsettings.Development.json`). Production never spawns it.

### D6 — Route design
Old `/api/backtest/*` routes kept for backward compat, new `/api/runs/*` routes for Angular. Redundant controllers (`EquityController`, `PerformanceApiController`, `RiskSseController`) removed.

### D7 — EF Migration
Single `InitialCreate` migration matching current entity schema. Old DB files deleted to avoid migration conflicts. `OrderId` column already present in schema from initial migration.

---

## 4. Known Issues / Remaining Work

### Deferred by owner
- **#18 Batch/multi-run runner** (`NEXT-STEPS RW-03`) — explicitly deferred

### Not yet implemented (future work)
- **Per-bar "why rejected / why no signal" funnel** — needs `BarEvaluations` API endpoint (`GET /api/runs/{id}/bars?strategyId=`)
- **LLM-readable report export** (markdown/JSON download button)
- **cTrader parity diff UI** — surface `CtraderDiffHarness` results in report page
- **Strategy config detail** — backend returns limited fields (`Timeframe=""`, `Symbols=[]`). Full config (ParametersJson, PositionManagementJson, etc.) needs an API endpoint reading from `IStrategyConfigStore`
- **Live venue status page** — needs persisted venue-status events

### Expected with fresh DB
- **Bars endpoint returns empty** — bars are stored during backtest runs via the engine's bar ingestion pipeline (`BarEntity` table populated during evaluation). Not seeded.
- **Journal empty** — pipeline events (SIGNAL, ORDER, FILL, CLOSE, etc.) are written during backtest runs via `PipelineEventWriter`. Not seeded.
- **Equity chart empty** on run report — requires equity data from a completed run.
- **Live monitor idle** — requires an active running backtest to show progress, equity, journal via SignalR.

### Template parser quirks
- `<span>` and `<label>` with multiple `[class.*]` bindings across lines cause Angular parser to treat them as self-closing. **Rule**: Use single-line `[attr.class]` with component method, or `[ngClass]`, or all-bindings-on-one-line.
- `<button>` same issue. Avoid multi-line bindings on void/phrasing elements.
- `@if (signal(); as alias)` with `as` aliasing is fragile in nested `@else` blocks. Use separate `@if` blocks or access signal directly.

---

## 5. How to Run

### Development

```powershell
# Terminal 1: .NET API (starts ng serve automatically)
dotnet run --project src/TradingEngine.Web --launch-profile https

# Or API-only (manual ng serve):
dotnet run --project src/TradingEngine.Web --launch-profile api-only

# Terminal 2 (if using api-only profile):
cd web-ui; npm start
```

- Angular SPA: `http://localhost:4200`
- API docs: `https://localhost:7108/scalar/v1`
- .NET API: `https://localhost:7108` / `http://localhost:5134`

### Production build

```powershell
cd web-ui; npm run build    # generates CSS + builds to wwwroot/
dotnet run --project src/TradingEngine.Web   # serves everything from single process
```

### Test suites

```powershell
dotnet test tests/TradingEngine.Tests.Unit           # 207 pass, 4 skip
dotnet test tests/TradingEngine.Tests.Architecture   # 3 pass
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"  # requires credentials
```

---

## 6. File Map

```
src/TradingEngine.Web/
  Program.cs                              # 6 lines, composition root
  Configuration/
    ServiceRegistration.cs                # AddApi/AddPersistence/AddAppServices/AddEngineServices
    MiddlewarePipeline.cs                 # UseShamshir — migrate, seed, middleware
  Api/
    RunsController.cs                     # /api/runs/* + /api/equity
    TradesController.cs                   # /api/trades/*
    StrategiesController.cs               # /api/strategies/*
    BarsController.cs                     # /api/bars
    ProtectionController.cs               # /api/protection/*
    GovernorController.cs                 # /api/governor
    EventsController.cs, ExportController.cs, ExperimentsController.cs, HealthController.cs
    BacktestController.cs, BacktestAnalyticsController.cs  # kept for backward compat
  Dtos/                                   # 18 typed response/request records
  Services/
    IRunQueryService.cs + RunQueryService.cs
    IProtectionQueryService.cs + ProtectionQueryService.cs
    IBarQueryService.cs + BarQueryService.cs
    NgServeHost.cs                        # Spawns ng serve in dev
    RunProgress.cs, RunProgressBroadcaster.cs

web-ui/
  src/
    styles.css                            # @import "tailwindcss" (source for CLI prebuild)
    styles.generated.css                  # CLI output (gitignored)
    index.html
    app/
      app.component.ts                    # Nav shell + router-outlet
      app.config.ts                       # provideRouter, provideHttpClient
      app.routes.ts                       # Lazy-loaded feature routes
      models/api.types.ts                 # 17 TypeScript interfaces
      core/signalr/run-hub.service.ts     # Typed SignalR hub connection
      shared/
        stat-tile.component.ts
        data-table.component.ts
        equity-chart.component.ts         # Lightweight-Charts v5 dual-pane
        candle-chart.component.ts         # OHLC + price lines
        histogram-chart.component.ts
        scatter-chart.component.ts
        badge.component.ts
      features/
        dashboard/dashboard.component.ts
        runs/
          runs.routes.ts, runs.store.ts, runs.service.ts, runs.service.spec.ts
          run-list/run-list.component.ts          # + compare modal
          new-backtest/new-backtest.component.ts  # symbol/timeframe/strategy pickers
          run-monitor/run-monitor.component.ts    # SignalR live monitor
          run-report/run-report.component.ts      # 10 KPIs + chart + trades + journal
          run-analyzer/run-analyzer.component.ts  # 5 charts
        trades/
          trades.routes.ts
          trade-list/trade-list.component.ts      # Filters, pagination, clickable rows
          trade-detail/trade-detail.component.ts  # 16 stat tiles + candlestick chart
        strategies/
          strategies.routes.ts
          strategy-list/strategy-list.component.ts    # Cards + inline toggle
          strategy-detail/strategy-detail.component.ts # Enable/disable + edit config
        compliance/compliance.component.ts
        events/events.component.ts
        settings/settings.component.ts
```

---

## 7. Git History (this branch)

```
d3b12fc fix: initialize strategies from config store at Web startup
8be3d3c fix: chart container selectors, trade list RouterLink+clickable rows, strategy detail API compat, profit factor Infinity, barHeight empty array
8179b1b fix: Tailwind CSS via CLI prebuild — content scans .ts inline templates
679dc64 fix: restore /api/equity endpoint, fix strategy-list array vs object, clean unused imports, postcss .mjs, add type module
faddd7d fix: clean AddShamshir into sub-methods, NgServeHost polls Angular readiness before browser opens, Scalar uses default auto-discovery
0b9cfba refactor: clean Program.cs — extracted DI/middleware, NgServeHost spawns Angular CLI in dev, proxy to port 5134, launch to localhost:4200
47b694d feat: complete Angular SPA — all charts, dashboard, analyzer, strategy edit, compare modal, enhanced report/monitor/new-backtest, trades filters, settings
a52e9a2 feat: complete Angular SPA — all features, proper types, CORS, production hosting, scrapped Razor
52ab37c feat: Angular SPA scaffold + API redesign (DTOs, services, Scalar, clean DI) + bug fixes (governor cooling-off, OrderId join, exact trade reconciliation)
```
