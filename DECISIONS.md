# Shamshir Trading Engine — Implementation Plan & Decision Record

> Created: 2026-06-06
> Purpose: Document all implementation decisions, open questions, and the phased build plan.
> Update this file as decisions are made.

---

## Workflow

Every phase follows: `branch → implement → PR → merge to dev → next branch`

Branch naming: `phase/##-name` (e.g., `phase/01-domain`). PRs merge into `dev`.
`main` is production-ready only, updated via release PRs from `dev`.

---

## Repository Structure

```
C:\Code\Shamshir\
├── src/
│   ├── TradingEngine.Domain/          # Pure domain types, interfaces, events
│   ├── TradingEngine.Application/     # Assembly marker only (future use cases)
│   ├── TradingEngine.Infrastructure/  # EF Core, Skender, adapters, persistence
│   ├── TradingEngine.Risk/            # Risk engine, position sizing, prop firm rules
│   ├── TradingEngine.Strategies/      # Strategy implementations
│   ├── TradingEngine.Services/        # PipCalculator, SL/TP, trailing stop, indicators
│   ├── TradingEngine.Adapters.CTrader/# C# 6 cBot (Phase 9)
│   ├── TradingEngine.Host/            # Console + Windows Service host
│   └── TradingEngine.Web/             # ASP.NET Core Razor Pages viewer
├── aspire/
│   └── TradingEngine.AppHost/         # .NET Aspire orchestration (dev only)
├── tests/
│   ├── TradingEngine.Tests.Unit/      # Pure domain + risk + services unit tests
│   ├── TradingEngine.Tests.Integration/# Persistence + adapter integration tests
│   └── TradingEngine.Tests.Simulation/ # End-to-end backtest simulation tests
├── config/
│   ├── strategies/                    # JSON per strategy
│   ├── risk-profiles/                 # JSON per risk profile
│   └── prop-firms/                    # JSON per prop firm ruleset
├── tests/data/                        # Test CSV files (committed)
├── docs/                              # Design docs (already present)
├── .github/workflows/                 # CI/CD (Phase 10)
├── DECISIONS.md                       # This file
├── TradingEngine.sln
├── Directory.Build.props
├── Directory.Packages.props
└── .editorconfig
```

---

## ✅ Locked Decisions (from design docs — not open for discussion)

These are captured from the three design docs. Listed here for reference:

| Decision | Value | Source |
|---|---|---|
| Runtime | .NET 10, C# 13 | design §1 |
| Process model | Console (dev) + Windows Service (prod) | design §1 |
| Broker adapter | cTrader cBot, C# 6, named pipes | design §1 |
| Internal messaging | System.Threading.Channels + typed event bus | design §1 |
| Persistence | EF Core + SQLite; Dapper for complex reads | design §1 |
| Indicators | Skender.Stock.Indicators (wrapped) | design §1 |
| Configuration | Strongly-typed C#, JSON-backed | design §1 |
| Logging | Serilog (console + file sinks) | design §1 |
| Prop firm baseline | FTMO — configurable rule set | design §1 |
| Money management | First-class in risk layer — NOT in strategies | design §1 |
| Reporting | ASP.NET Core localhost web app | design §1 |
| Testing | xUnit, no cTrader dependency | design §1 |
| Web frontend | Razor Pages + Chart.js CDN — no npm/node | guide §2.12 |
| All times UTC internally | DateTime.Now / UtcNow banned outside BrokerClock/StubClock | guide §4.3 |
| FTMO daily reset | 22:00 UTC (midnight Prague) | guide §1.5, domain §11.6 |
| SlMethod | 3 values: FixedPips, AtrMultiple, SwingBased | guide §1.4 |
| Position.FloatingPnL() | REMOVED from Position record — use PipCalculator | guide §1.1 |
| PositionManagementConfig | Defined as record (guide §1.2) | guide §1.2 |
| IBrokerAdapter.BrokerTimeUtc | Added to interface | guide §1.3 |
| Skender containment | Arch check §4.2: no Skender types outside scanned project | guide §4.2 |
| Lot rounding | Math.Floor, never Math.Round | guide §5, domain §13 |
| PipCalculator | In Services (not Domain) | D4 resolved |
| Application project | Assembly marker only (empty) | D7 resolved |

---

## ✅ All Decisions Resolved

All 20 decisions (D1–D20) were voted on by the project owner in `START.md`. See that file for full votes and rationale.

Quick reference:

| ID | Decision | Vote |
|---|---|---|
| D1 | Skender placement | A — Infrastructure/Indicators/ |
| D2 | Backtest data path | A — DataFeedService |
| D3 | Concurrency model | A — Single-threaded tick processor |
| D4 | PipCalculator location | Services |
| D5 | Config loading | A — ConfigLoader |
| D6 | Strategy resolution | A — [StrategyId] attribute |
| D7 | Application project | Assembly marker only |
| D8 | LiveMarketDataProvider | A — throw NotSupportedException |
| D9 | NewsFilter | A — stub |
| D10 | Tick synthesis | A — 4 ticks at 0/25/50/75% |
| D11 | Slippage determinism | A — fixed offset |
| D12 | FTMO daily reset time | 22:00 UTC |
| D13 | SlMethod enum | 3 values |
| D14 | DurationSeconds | Add to TradeResult |
| D15 | MaxExposurePercent | A — sum of open risk / equity |
| D16 | Daily reset on late start | A — fire immediately |
| D17 | cTrader API | Info provided |
| D18 | Test data source | Synthetic via CsvDataGenerator |
| D19 | Number of phases | 10 phases, unchanged |
| D20 | SymbolInfo registry | A — ISymbolInfoRegistry + defaults.json |

---

## 📋 Phase Breakdown

### Pre-Phase: Repository Setup

**Branch:** `chore/init-repo`

1. `git init`, create `main` + `dev`, protect `main`
2. `.gitignore` (dotnet, IDE, logs, DB files)
3. All projects scaffolded via `dotnet new` (guide §2.3)
4. `Directory.Build.props`, `Directory.Packages.props` (guide §2.1, §2.2), `.editorconfig`
5. All `config/` skeleton directories with example `.json`
6. `tests/data/` directory

---

### Phase 1: Domain Types ✅ Complete

**Branch:** `phase/01-domain` → merged to dev: `daf8b7f`

**Scope:** 59 files — all value objects, market data types, trading lifecycle records, events (7 concrete), interfaces (17), SymbolInfo, BrokerClock/StubClock, StrategyIdAttribute. Zero logic.

**Validation:** `dotnet build src/TradingEngine.Domain --no-restore` → 0 errors, 0 warnings. ✅

**Key rules:**
- Every file = one top-level type, all `public`
- Domain has NO NuGet packages — flat `TradingEngine.Domain` namespace (no sub-namespaces)
- No `class` implementations (except `BrokerClock` and `StubClock`)
- `Position` record has NO `FloatingPnL()` method (guide §1.1)
- `IBrokerAdapter` includes `DateTime BrokerTimeUtc { get; }` (guide §1.3)
- `RiskProfile` includes `double MaxSlPips` (domain §5.4)
- `PositionManagementConfig` record defined (guide §1.2)
- `SlMethod` = 3 values: `FixedPips`, `AtrMultiple`, `SwingBased` (guide §1.4)
- `TradeResult` includes `DurationSeconds` (D14)
- `ISymbolInfoRegistry` in Domain interfaces (D20)

---

### Phase 2: Risk Engine ✅ Complete

**Branch:** `phase/02-risk` → merged to dev: `a3817d3`

**Scope:** 10 files — PositionSizer, DrawdownTracker, RiskManager (with EnterProtectionMode), PropFirmRuleValidator, DrawdownScaler, SessionFilter, NewsFilter (stub), INewsFilter interface, GlobalUsings.

**Validation:** `dotnet build` + `dotnet test --filter "Category=Risk"` → 15 tests pass ✅

**Key rules:**
- `PositionSizer.Calculate()` — uses `Math.Floor`, never `Math.Round` (guide §7.3)
- `RiskManager.Validate()` — returns ALL violations, not first-only (guide §3 Phase 2)
- `DrawdownTracker.InitialAccountBalance` — set once via `Initialize()`, never updated (domain §11.2)
- `DrawdownTracker` supports Fixed and Trailing drawdown types
- `PropFirmRuleSet` — full schema from domain doc §11.6 (19 fields)
- `NewsFilter` — stub returning "no news" (D9)

---

### Phase 3: Infrastructure ✅ Complete

**Branch:** `phase/03-infrastructure` → merged to dev: `9bfa08c`

**Scope:** 36 files — 6 EF Core entities + 6 mappings + 2 DbContexts + 5 repositories + SqliteDataProvider + TradeReportQueries/PerformanceSummary + 4 adapters (SimulatedBrokerAdapter, HistoricalDataProvider, LiveMarketDataProvider stub, NamedPipeBrokerAdapter) + 3 Skender files (SkenderIndicatorService, SkenderQuote, IndicatorCache) + BufferedBarWriter + SymbolInfoRegistry + ServiceCollectionExtensions.

**Validation:** `dotnet build` + `dotnet test tests/TradingEngine.Tests.Integration` → 3 tests pass ✅

**Key rules:**
- Skender in Infrastructure (not Services — D1) — `internal sealed`
- EF Core entities flat, no navigation property chains on hot paths
- All enums stored as strings, DateTime as TEXT (ISO 8601 UTC), Money as two columns
- `ReportingDbContext` → `QueryTrackingBehavior.NoTracking`
- `BufferedBarWriter` → `Channel.CreateBounded<Bar>(10_000)`, `DropOldest`, batch=500
- `SimulatedBrokerAdapter` exposes `ChannelWriter<Tick>` / `ChannelWriter<Bar>` for external feed (D2)
- `HistoricalDataProvider` synthesises 4 ticks per bar at 0/25/50/75% of duration (D10)
- `LiveMarketDataProvider` throws `NotSupportedException` (D8)
- `NamedPipeBrokerAdapter` — pipe server, length-prefixed JSON, async read loop
- `SymbolInfoRegistry` — thread-safe `ConcurrentDictionary<Symbol, SymbolInfo>`

---

### Phase 4: Services Layer ✅ Complete

**Branch:** `phase/04-services` → merged to dev: `250852f`

**Scope:** 8 files — PipCalculator (Distance, PipValuePerLot/3 cases, GrossPnL, FloatingPnL, RMultiple), SlTpHelpers (FixedPip, AtrBased, SwingBased, RRMultiple, AtrMultiple, IsSlValid), SlTpCalculator (ISlTpCalculator), TrailingHelpers (StepTrail, AtrTrail, Breakeven), TrailingStopService (ITrailingStopService), ExcursionTracker.

**Validation:** `dotnet build` + `dotnet test --filter "Category=Services"` → 15 tests pass ✅

**Key rules:**
- PipCalculator in Services — takes `getCrossRate` delegate (D4)
- All helpers use `decimal` for financial arithmetic, `double` for indicator values
- `RoundToTickSize` applied to all SL/TP outputs
- StepTrail validates `newSl > currentSl` for longs (never backward)
- Breakeven checks trigger R-multiple before activating, then returns null

---

### Phase 5: Strategies ✅ Complete

**Branch:** `phase/05-strategies` → merged to dev: `2c8fb6c`

**Scope:** 4 files — TrendBreakoutStrategy, TrendBreakoutConfig/TrendBreakoutParameters, StrategyIdAttribute (in Domain). Strategy uses AtrBased SL + RRMultiple TP, EMA trend filter, lookback breakout detection.

**Validation:** `dotnet build` + `dotnet test --filter "Category=Strategy"` → 3 tests pass ✅

**Key rules:**
- `Evaluate()` NEVER throws — wrapped in try/catch, logs error, returns null (guide §6 rule 1)
- `Evaluate()` is synchronous
- `Evaluate()` receives `IndicatorValues` from `MarketContext` — never calls `IIndicatorService` directly
- `OnTradeResult()` tracks win/loss streaks with thread-safe increments
- `Reset()` clears `_lastSignalDirection`, `_winStreak`, `_lossStreak`
- Checks `context.Bars count >= RequiredBarCount` at top of `Evaluate()`
- Breakout signal: `latestBar.High > priorLookbackHigh` (fixed from `Close > highestHigh` — design doc bug)

---

### Phase 6: Simulation Tests ✅ Complete

**Branch:** `phase/06-simulation` → merged to dev: `285450b`

**Scope:** 5 files — EngineTestHarness (fluent builder), BacktestResult record, CsvDataGenerator (deterministic synthetic OHLCV), TrendBreakoutScenarios (end-to-end test). Generates 500 H1 bars with configurable drift/noise, feeds through HistoricalDataProvider, runs strategy, collects trades.

**Validation:** `dotnet test tests/TradingEngine.Tests.Simulation` → 1 end-to-end test passes ✅

**Key rules:**
- `EngineTestHarness` uses direct data flow (no channel race conditions)
- `CsvDataGenerator` uses seeded `Random(42)` for determinism
- Test verifies: bullish trend data → at least 1 trade generated
- Strategy breakout signal fixed: compares `High` to prior N bars' high (not `Close`)

---

### Phase 7: Host Wiring ✅ Complete

**Branch:** `phase/07-host` → merged to dev: `4d1b367`

**Scope:** 9 files — EngineWorker (BackgroundService, 4 concurrent loops, drain-first pattern), DataFeedService (IHostedService), DailyResetService (schedules 22:00 UTC reset), StrategyRegistry (scans `[StrategyId]` attribute), ConfigLoader (loads all JSON from config/ subdirs), Program.cs (full DI wiring), appsettings.json + appsettings.Backtest.json.

**Validation:** `dotnet build` → 0 errors

**Key rules:**
- Config loaded via `ConfigLoader` — paths relative to `AppContext.BaseDirectory` (D5)
- Strategy resolution via `[StrategyId]` attribute + `StrategyRegistry` assembly scan (D6)
- `EngineWorker` uses internal `Channel<ExecutionEvent>` with `BoundedChannelFullMode.Wait` (D3)
- Account updates via `Interlocked`-swapped field (D3)
- `DataFeedService` feeds bars/ticks from provider into broker writer channels (D2)
- `DailyResetService` fires immediately if past 22:00 UTC on startup (D16)

---

### Phase 8: Web Viewer ✅ Complete

**Branch:** `phase/08-web` → merged to dev: `0b6db61`

**Scope:** 24 files — Razor Pages (Dashboard `/`, Trades `/trades`, Detail `/trades/{id}`, Performance, Events), 6 API controllers (SSE `/sse/risk`, Trades, Performance, Equity, Events, CSV Export), Layout (dark theme, navbar), Chart.js frontend.

**Validation:** `dotnet build src/TradingEngine.Web` → 0 errors

**Key rules:**
- No JS framework, no npm/webpack (guide §2.12 LOCKED)
- Chart.js + chartjs-chart-financial via CDN
- Bare CSS (no Bootstrap)
- SSE on `/sse/risk` streams RiskState JSON

---

### Phase 9: cTrader Adapter ✅ Complete

**Branch:** `phase/09-ctrader` → merged to dev: `f36caa7`

**Scope:** 8 files — TradingEngineCBot (main cBot), PipeClient (named pipe with background reader), PipeMessage (length-prefixed JSON framing), TickPublisher/BarPublisher/AccountUpdatePublisher (serialize and send), OrderCommandHandler (dispatches commands).

**Validation:** `dotnet build src/TradingEngine.Adapters.CTrader` → 0 errors, no C# 8+ features ✅

**Key rules:**
- `<LangVersion>6</LangVersion>`, `<Nullable>disable</Nullable>`, target net48
- Newtonsoft.Json for serialization
- cBot connects to engine's named pipe server (D17)
- Uses `System.Threading.Thread` for background pipe reads (no async in cTrader)

---

### Phase 10: Aspire + CI/CD ✅ Complete

**Branch:** `phase/10-aspire-cicd` → merged to dev: `30c0871`

**Scope:** 4 files — `pr.yml` (build + test + coverage on PR → develop), `release.yml` (build + test + publish on push → main), `AppHost.cs` (wires engine + web via Aspire).

**Validation:** `dotnet build` + `dotnet test` → all 37 tests pass ✅

---

## Progress Tracking

**Iteration 1 total:** 159 `.cs` source files + 17 test files | 37 tests | All 10 phases merged
**Iteration 2 status:** See `ITERATION-2.md` — 3 sub-phases, 24 confirmed bugs, 4 new decisions

### Iteration 1 (Phases 0–10)

| Phase | Branch | Status | Tests | Key Deliverables |
|---|---|---|---|---|
| Pre-Phase | `chore/init-repo` | ✅ Done | — | Git init, solution scaffold (13 projects), all configs, decisions |
| 1 — Domain | `phase/01-domain` | ✅ Done | `dotnet build` 0 err | 59 files: value objects, market data, trading lifecycle, events (7), interfaces (17), SymbolInfo, clocks |
| 2 — Risk | `phase/02-risk` | ✅ Done | 15 unit | PositionSizer, DrawdownTracker, RiskManager, PropFirmRuleValidator, DrawdownScaler, NewsFilter stub |
| 3 — Infrastructure | `phase/03-infrastructure` | ✅ Done | 3 integration | EF Core (6 entities + mappings + DbContexts + repositories), Skender (internal), adapters (4), caching |
| 4 — Services | `phase/04-services` | ✅ Done | 15 unit | PipCalculator, SlTpHelpers, TrailingHelpers, ExcursionTracker |
| 5 — Strategies | `phase/05-strategies` | ✅ Done | 3 unit | TrendBreakoutStrategy with [StrategyId] attribute, config |
| 6 — Simulation | `phase/06-simulation` | ✅ Done | 1 e2e | EngineTestHarness, CsvDataGenerator, end-to-end backtest |
| 7 — Host | `phase/07-host` | ✅ Done | — | EngineWorker, DataFeedService, ConfigLoader, StrategyRegistry, DI |
| 8 — Web | `phase/08-web` | ✅ Done | — | Razor Pages (5), API controllers (6), SSE, Chart.js |
| 9 — cTrader | `phase/09-ctrader` | ✅ Done | — | C# 6 cBot with PipeClient, publishers, command handler |
| 10 — CI/CD | `phase/10-aspire-cicd` | ✅ Done | — | GitHub Actions (PR + Release), Aspire AppHost |

### Iteration 2 (Phases 2A–2C) — See ITERATION-2.md

| Phase | Branch | Status | Tests | Key Deliverables |
|---|---|---|---|---|
| 2A — Engine Unblocking | `phase/2a-engine-unblock` | ❌ Not started | — | Fix DI throws, bar accumulation, IIndicatorService wiring, DataFeedService path/sequencing |
| 2B — Financial Correctness | `phase/2b-financial-correctness` | ❌ Not started | +7 unit | Fix lot sizing, FTMO daily floor, protection mode reset, 5 missing risk checks, SymbolInfo in strategies |
| 2C — Working Engine Loop | `phase/2c-working-loop` | ❌ Not started | +7 simulation | TypedEventBus, PositionManager, SimulatedBrokerAdapter fills, real PnL in harness |

### Iteration 2 Issue Summary

| Severity | Count | Blocking |
|---|---|---|
| CRITICAL | 6 | Engine cannot start or produce trades |
| SERIOUS | 5 | Silent financial errors |
| MODERATE | 8 | Design violations, missing features |
| MINOR | 4 | Technical debt |

### New Decisions (D21–D24) — All resolved in ITERATION-2.md

| ID | Decision | Vote |
|---|---|---|
| D21 | Strategy indicator contract | ✅ A — `RequiredIndicators` property on `IStrategy` |
| D22 | PositionManager location | ✅ A — `TradingEngine.Services` |
| D23 | TypedEventBus location | ✅ A — `TradingEngine.Infrastructure/Events` |
| D24 | Open position tracking in RiskManager | ✅ A — `RegisterPosition`/`DeregisterPosition` on `IRiskManager` |
