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
**Iteration 3 status:** ✅ Complete (R1–R7). See `ITERATION-3-FINAL.md` — deep review found 6 critical + 7 serious + 10 moderate surviving bugs; 15 new decisions (D36–D50); strategy composition design; iteration 4 plan
**Iteration 4 status:** Not started. See `ITERATION-4.md` — money management circuit (4A) + cTrader CLI integration (11A–11E). D51–D59 resolved.

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

### Iteration 2 (Phases 2A–2C) ✅ Complete — See ITERATION-2.md

| Phase | Branch | Status | Tests | Key Deliverables |
|---|---|---|---|---|
| 2A — Engine Unblocking | `phase/2a-engine-unblock` | ✅ Done | +9 unit | Fix DI throws, bar accumulation, IIndicatorService wiring, DataFeedService path/sequencing |
| 2B — Financial Correctness | `phase/2b-financial-correctness` | ✅ Done | +7 unit | Fix lot sizing, FTMO daily floor, protection mode reset, 5 missing risk checks, SymbolInfo in strategies |
| 2C — Working Engine Loop | `phase/2c-working-loop` | ✅ Done | +7 simulation | TypedEventBus, PositionManager, SimulatedBrokerAdapter fills, real PnL in harness |

### New Decisions (D21–D24) — All resolved in ITERATION-2.md

| ID | Decision | Vote |
|---|---|---|
| D21 | Strategy indicator contract | ✅ A — `RequiredIndicators` property on `IStrategy` |
| D22 | PositionManager location | ✅ A — `TradingEngine.Services` |
| D23 | TypedEventBus location | ✅ A — `TradingEngine.Infrastructure/Events` |
| D24 | Open position tracking in RiskManager | ✅ A — `RegisterPosition`/`DeregisterPosition` on `IRiskManager` |

### Iteration 3 (R1–R7) ✅ Complete — See ITERATION-3-FINAL.md

| Phase | Branch | Status | Tests | Key Deliverables |
|---|---|---|---|---|
| R1 — Data & Symbol | `phase/r1-data-symbol` | ✅ Done | +7 | SimBrokerAdapter: ISymbolInfoRegistry, pip-size slippage. SlTpCalculator/TrailingStopService: real symbol lookup |
| R2 — Config & Mode | `phase/r1-data-symbol` | ✅ Done | +4 | EngineMode from config, Aspire fix, dotnet format |
| R3 — Position Manager | `phase/r3-position-engine` | ✅ Done | +7 | Exit reason SL/TP dynamic, trailing method switch, DD in equity |
| R4 — Multi-Strategy | `phase/r4-multi-strategy` | ✅ Done | +1 | Two concurrent strategies, DataFeedService multi-symbol |
| R5 — Web Real Data | `phase/r5-web-data` | ✅ Done | — | Dashboard/perf/trades/events query SQLite |
| R6 — Dev Polish | `phase/r6-polish` | ✅ Done | — | README, .gitattributes |
| R7 — Hardening | `phase/r7-hardening` | ✅ Done | — | Slippage from config, broker interface decoupled |

**Results:** 26 bugs fixed (5 critical, 8 serious, 8 moderate, 5 minor). **69 tests passing** (64 unit + 3 integration + 2 simulation). **19 new tests.**

### Iteration 4 (Phases 4A–4F) — See ITERATION-3-FINAL.md §12

| Phase | Branch | Status | Tests | Key Deliverables |
|---|---|---|---|---|
| 4A — Critical Fixes | `phase/4a-critical-fixes` | ❌ Not started | +8 unit | Lot sizing uses real profile; indicator namespace; DD fraction fix; AccountUpdate from SimBroker; PersistenceService singleton; pipe partial-read fix; breakeven one-shot; AtrTrail high-water |
| 4B — OrderDispatcher Wiring | `phase/4b-dispatcher-wiring` | ❌ Not started | — | Wire OrderDispatcher + PositionTracker into EngineWorker; remove duplicate logic; cap `_bars` at 500; partial fill + duplicate execution guards |
| 4C — Strategy Composition | `phase/4c-composition` | ❌ Not started | +6 unit | `ISignalProvider`, `IEntryFilter`, `IExitBehavior`, `IPositionBehavior`; `ComposedStrategy`; built-in filters + behaviors; EmaAlignment, MeanReversion, SessionBreakout strategies; RSI + BB in Skender service |
| 4D — Lot Sizing + Risk | `phase/4d-lot-sizing` | ❌ Not started | +4 unit | `LotSizingMethod` enum + `RiskProfile` fields; `PositionSizer` dispatch; `StrategyStats`; force-close on DD breach; tick synthesis spread fix |
| 4E — State Sync | `phase/4e-state-sync` | ❌ Not started | +3 integration | `IBrokerAdapter.GetAccountStateAsync()`; startup reconciliation in live mode; pipe reconnect (3 retries, exponential backoff); `PositionLifecycleState` tracking |
| 4F — Aspire + Test Harness | `phase/4f-aspire-tests` | ❌ Not started | +5 simulation | Aspire `Engine__Mode` + shared DB path + `WaitForCompletion`; EngineTestHarness real indicators + lot sizing; multi-strategy + composition + edge-case tests |

### New Decisions (D25–D35) — Resolved in ITERATION-3-FINAL.md

| ID | Decision | Vote |
|---|---|---|
| D25 | Risk profile resolution per intent | ✅ A — `IRiskProfileResolver` |
| D26 | Per-strategy position cap | ✅ A — in `RiskManager.Validate()` |
| D27 | Current equity propagation | ✅ A — `Volatile.Read` field |
| D28 | Persistence writes from EngineWorker | ✅ A — `PersistenceService` fire-and-forget |
| D29 | Shared DB path | ✅ A — solution-relative via `AppContext` |
| D30 | EngineTestHarness real indicators | ✅ A — inject `SkenderIndicatorService` |
| D31 | Real equity tracking | ✅ B — SimulatedBrokerAdapter owns balance |
| D32 | DB path implementation | ✅ A — `AppContext.BaseDirectory` resolve-up |
| D33 | getCrossRate injection | ✅ A — inject `Func<string,string,decimal>` |
| D34 | SSE RiskState updates | ✅ B — `SseRiskHandler : IEventHandler<EquityUpdated>` |
| D35 | Phase execution ordering | ✅ Confirmed — 3A→3B→(3C∥3E)→3D→3F |

### New Decisions (D36–D50) — Resolved in ITERATION-3-FINAL.md

| ID | Decision | Vote |
|---|---|---|---|
| D36 | Bar history cap | ✅ A — `MaxBarsPerTimeframe = 500`; evict oldest when exceeded |
| D37 | Indicator key namespace | ✅ A — prefix with symbol: `"EURUSD:ATR_14"`; strip prefix when building `MarketContext.IndicatorValues` |
| D38 | Strategy composition model | ✅ A — `ISignalProvider` + `IEntryFilter` + `IExitBehavior` + `IPositionBehavior`; `IStrategy` unchanged; new strategies use `ComposedStrategy` wrapper |
| D39 | PositionManagementConfig source | ✅ A — strategies declare `IReadOnlyList<IPositionBehavior> PositionBehaviors { get; }`; `PositionManager` reads from this instead of hardcoded switch |
| D40 | Lot sizing methods | ✅ A — add `LotSizingMethod` enum + fields to `RiskProfile`; `PositionSizer` dispatches on method |
| D41 | Position state machine | ✅ A — `PositionLifecycleState` enum tracked in `PositionManager._tracked`; log every transition |
| D42 | Pipe reconnection | ✅ A — 3 retries, exponential backoff (2s, 4s, 8s); enter protection mode if all fail; re-sync state on reconnect |
| D43 | Broker state sync on startup | ✅ A — `IBrokerAdapter.GetAccountStateAsync()` called after `ConnectAsync` in live/paper mode; reconcile before accepting signals |
| D44 | Tick synthesis spread | ✅ A — `HistoricalDataProvider` uses `symbolInfo.TypicalSpread / 2` as half-spread |
| D45 | Order rejection handling | ✅ A — `OrderState.Rejected` removes from pending map, logs `RejectionReason`, deregisters risk |
| D46 | Partial fill handling | ✅ A — track cumulative `FilledLots` per `OrderId`; remove from pending only when `FilledLots >= RequestedLots` |
| D47 | Duplicate execution guard | ✅ A — `HashSet<Guid> _processedExecutionIds`; skip already-processed events |
| D48 | Force close on DD breach | ✅ A — when `ForceCloseOnBreach == true` and max-DD protection entered: publish `ForceCloseAllRequested`; `EngineWorker` calls `ClosePositionAsync` for all open positions |
| D49 | Aspire shared DB path | ✅ A — `Engine__Mode` env var (double underscore); `Persistence__DbPath` shared; `WaitForCompletion(engine)` on web |
 | D50 | Three new strategies | ✅ A — `EmaAlignmentStrategy`, `MeanReversionStrategy`, `SessionBreakoutStrategy`; all use `ComposedStrategy`; each with session filters and position behaviors |
 | D51 | DailyDdBase enum | ✅ A — `InitialBalance` / `DailyStart` on `PropFirmRuleSet`; drawdown tracker dispatches on mode |
 | D52 | cBot target framework | ✅ A — `net6.0` — required for cTrader CLI (CLI rejects net48 algo files) |
 | D53 | ctrader-cli.exe discovery | ✅ A — auto-glob `%LOCALAPPDATA%\Spotware\cTrader\**\ctrader-cli.exe`, take newest; config override `CTrader:CliPath` |
 | D54 | Pipe transport for CLI backtest | ✅ A — named pipe (Windows); TCP deferred to future iteration |
 | D55 | CTraderRunner project | ✅ A — new project `src/TradingEngine.CTraderRunner` (net10.0); runtime library for orchestrating ctrader-cli backtests |
 | D56 | Backtest results storage | ✅ A — `BacktestRuns` table in existing SQLite via `TradingDbContext`; `BacktestRunSummary` domain record keyed by run ID |
 | D57 | Web UI backtest page scope | ✅ A — table only, no charts, no detail page. Charts deferred |
 | D58 | Auto-deploy mechanism | ✅ A — MSBuild `AfterTargets="Build"` target, gated by `-p:AutoDeploy=true`; off by default |
 | D59 | Phase 4D merged into 4C | ✅ A — lot sizing variants implemented in same branch as strategy composition |
 | D60 | BacktestRunner starts engine subprocess | ~~A — BacktestRunner.RunAsync starts engine with Engine:Mode=Live~~ **Superseded by D66** |
 | D61 | Serilog uses ReadFrom.Configuration | ✅ A — no hardcoded MinimumLevel in Program.cs; appsettings.json controls level; Debug in Development |
 | D62 | DrawdownTracker initialized from first AccountUpdate | ✅ A — no hardcoded $100k; InitializeIfNeeded(balance) called from HandleAccountUpdate |
 | D63 | CalculateLotSize takes currentMid parameter | ✅ A — entry price for SL distance = market price (currentMid), not equity.Equity |
 | D64 | ClientOrderId correlates engine↔cBot | ✅ A — engine generates Guid, sends in SubmitOrder payload; cBot echoes in ExecutionEvent |
 | D65 | PipeExists() removed permanently | ✅ A — `NamedPipeClientStream.Connect()` inside a probe consumed the engine's one connection slot; deleted, no replacement in the Aspire path |
 | D66 | BacktestRunner is a CLI launcher only under Aspire | ✅ A — `CTrader:StartEngineSubprocess=false` by default; Aspire owns engine lifecycle; `StartEngine()` only called when explicitly opted in |
 | D67 | Pipe name coordinated via Aspire env vars | ✅ A — AppHost sets `Engine__Broker__PipeName` on both engine and web; BacktestRunner reads `_config["Engine:Broker:PipeName"]`; no hardcoded strings |
 | D68 | Engine state reset on new pipe connection | ✅ A — `NamedPipeBrokerAdapter.OnClientConnected` callback; `EngineWorker.ResetState()` clears bars, indicators, equity, counters on every new cBot connection |
 | D69 | WebSmokeTests won't spawn engine subprocesses | ✅ A — `WebApplicationFactory` overrides `CTrader:StartEngineSubprocess=false`; fire-and-forget BacktestRunner never starts engine subprocess in tests |
 | D70 | NetMQ transport for cBot↔engine | ✅ Final — Named pipes abandoned. ctrader-cli sandbox intercepts .NET managed sockets; NetMQ uses native P/Invoke (ZeroMQ) which bypasses. PUB/SUB + ROUTER/DEALER. |
 | D71 | Strategy evaluation on bar close | ✅ Final — Indicators only change on bar close. `ProcessBarsAsync` evaluates once per bar. `ProcessTicksAsync` handles fills/risk only. |
 | D72 | World ACL pipe security | ✅ Superseded by D70 |
 | D73 | bars.BarClosed event for bar data | ✅ Final — cBot uses `MarketData.GetBars().BarClosed` instead of `OnBar()`. |
 | D74 | Fixed ports 15555/15556 for NetMQ | ⚠️ Tech debt — hardcoded, fine for single-user, log for future dynamic allocation |
 | D75 | TickEveryN = 10 throttling | ✅ Final — Ticks published 1 in 10. Used for fills/SL/TP only, not strategy signals. |
 | D76 | --full-access required | ✅ Confirmed — Both .NET managed sockets AND NetMQ native sockets intercepted without it. |
 | D77 | No 3-arg GetBars overload | ✅ Accepted — `MarketData.GetBars(tf, symbol, count)` doesn't exist. 34-bar default is platform limit. |
 | D78 | bar.OpenTime must be UTC | ✅ Final — `DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc)` before serialization. |
 | D79 | diag PUB topic for observability | ✅ Final — cBot publishes trace lines on `diag` topic; engine logs as `CBOT|…`. |
 | D80 | Multi-symbol via cBot parameters | ✅ Final — Comma-separated `SymbolString` parameter. `SubscribeAll()` for `(symbol, tf, barClosed)`. Dedup via `HashSet<(symbol, tf, openTime)>`. |

 | D81 | K4 twins relocated to a test-support assembly, not deleted | Final - OrderDispatcher/KernelOrderGate/AccountProcessor moved to tests/TradingEngine.Tests.Support (golden oracle home); grep->0 in src is the gate, "absent from production wiring" is the intent. TradingLoop/PositionTracker stay (not gated). |
 | D82 | Golden oracle stays realized-equity; no MtM re-baseline | Final - KernelLoopHarness/golden use FakeVenue realized equity (the oracle). Production uses mark-to-market; its floating-DD is validated by in-host BacktestReplayTests + cTrader e2e, not the golden snapshot. No re-baseline. |
 | D83 | One journal = StepRecord; legacy writers deleted | Final - PipelineEventWriter + BarEvaluationHandler (DropOldest) deleted; ChannelJournalWriter (Wait) is the single journal. Legacy IDecisionJournal/IPipelineJournal consumers bind to NullDecisionJournal/NullPipelineJournal. |
 | D84 | EF migrations regenerated from scratch for ParentRunId | Final - recreate/regen-init (delete migrations + single fresh InitialCreate); dev DB recreated, app migrates + re-seeds from JSON on boot. Pre-release: no data to preserve. |
 | D85 | Download ports hardcoded 15562/3 — no port manager exists yet | Final - iter-tape-trust T1. No port allocator anywhere in codebase. Documented as known limitation; must be built before concurrent downloads/backtests. |
 | D86 | EmitExecutionEvent helper added per-adapter, not shared static | Final - Both adapters are sealed in same namespace. Per-adapter helpers keep scope small; no shared dependency needed. |
 | D87 | GetAccountStateAsync returns _balance for both balance+equity | Final - Called at startup only (no open positions). Computing floating PnL adds risk without value. |
 | D88 | cBot shards append:true (not .partial/rename) | Final - Simpler. Ingester dedupe absorbs overlaps. No rename-on-close coordination needed. |
 | D89 | LedgerReconcileService is Scoped (needs TradingDbContext) | Final - Scoped to match DB context lifetime. |
 | D90 | F1 spread on fills — directional spread on entry + exit; ask-bar detection for shorts | Final - Actually applied in T3. Changed all fill prices in both adapters. Golden 63/63 survived (kernel-vs-imperative both use same adapter). |
 | D91 | RunPlanJson sourced from cfg.CustomParams[RunRows] | Final - Same source as WriteStartRecordAsync line 566. Consistent with DB path; no new serialization needed. |
 | D92 | F1 halfSpread sourced from SymbolInfo.TypicalSpread / 2 | Final - `GetHalfSpread()` helper in both adapters; fallback 0.00005m if registry lookup fails. Per-bar spread (Q3) is future refinement. |
 | D93 | ProcessPendingLimits decrementExpiry param | Final - Boolean param, default true (backward compat). Fine-bar calls pass false so limit expiry counts decision bars. |
 | D94 | ComparePairId stored in CustomParams, not a DB column | Final - Avoids schema migration for T4. Both tape + cTrader runs share the same ComparePairId in their params dict. |
 | D95 | SweepRunnerService is Singleton, uses IServiceScopeFactory | Final - Must be singleton (holds sweep job state). Scoped deps (IBacktestCommandService, IRunQueryService) created per-cell via factory scope. |
 | D96 | Sweep cell execution uses 300 × 500ms polling (2.5min timeout) | Final - Simple polling loop; no event-driven completion. Adequate for tape venue (<1s per cell). |
