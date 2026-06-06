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

## ❓ Decision Points for Your Review

### D1 — Skender Placement

**Conflict:** Arch rule §4.2 scans Infrastructure for Skender types. Guide Phase 4 places `SkenderIndicatorService` in Services. These cannot both be true.

**Options:**
- **A (recommended):** Put `SkenderIndicatorService` in Infrastructure. Update Phase 4 guide to reference Infrastructure. The arch checker stays as-is.
- **B:** Put it in Services. Update arch rule §4.2 to scan Services instead. Move `SkenderQuote` to Services.
- **C:** Create a dedicated `TradingEngine.Indicators` project.

---

### D2 — Backtest Data Path

**Conflict:** Engine loop reads `broker.TickStream`. In backtest, `SimulatedBrokerAdapter` is the broker but has no data unless fed. `HistoricalDataProvider` yields data but isn't connected to the adapter.

**Options:**
- **A (recommended):** `DataFeedService` — an `IHostedService` in the Host project that reads `IMarketDataProvider` and writes to `SimulatedBrokerAdapter`'s `TickWriter`/`BarWriter`.
- **B:** `SimulatedBrokerAdapter` internally owns and reads from `IMarketDataProvider` (constructor injection).
- **C:** `EngineWorker` directly reads both and bridges them.

---

### D3 — Concurrency Model

**Conflict:** `ProcessTicksAsync` and `ProcessExecutionEventsAsync` both touch `IPositionManager` state. Race condition risk.

**Options:**
- **A (recommended):** Single-threaded tick processor. `ProcessExecutionEventsAsync` enqueues events to a `Channel<ExecutionEvent>`. Tick processor drains this channel at the start of each tick cycle before evaluating strategies/positions. No locks.
- **B:** Keep concurrent streams, add `ReaderWriterLockSlim` on position state.
- **C:** Both streams run but position mutations go through an `ActionBlock` (Dataflow).

---

### D4 — PipCalculator Location ✅ DECIDED

**Status:** Placed in `TradingEngine.Services`. Domain stays pure data + interfaces. `PipCalculator` takes `Func<string, string, decimal> getCrossRate` delegate.

---

### D5 — Config Loading Strategy

**Unknown:** How `config/` JSON files are discovered, loaded, validated at runtime (no broker available in backtest, file paths relative to binary).

**Options:**
- **A (recommended):** `ConfigLoader` service. Given base path, scans subdirectories, deserializes with `System.Text.Json`, validates cross-references, returns typed config objects. Registered in DI as singleton.
- **B:** `IConfiguration` + `AddJsonFile()` per directory. Standard .NET options pattern.
- **C:** Embedded resources in the Host assembly.

---

### D6 — Strategy ID → Type Resolution

**Unknown:** How `"trend-breakout"` string from config maps to `TrendBreakoutStrategy` class.

**Options:**
- **A (recommended):** `[StrategyId("trend-breakout")]` attribute on strategy class. `StrategyRegistry` scans assembly at startup, builds `Dictionary<string, Type>`.
- **B:** Convention: `"trend-breakout"` → `TrendBreakoutStrategy` via `Type.GetType()` with kebab-to-PascalCase mapping.
- **C:** Manual `Dictionary<string, Type>` in `Program.cs`.

---

### D7 — Application Project Content ✅ DECIDED

**Status:** Keep `TradingEngine.Application` with an assembly marker (`public static class AssemblyMarker {}`). Referenced by Risk, Strategies, Services per dependency graph. Future use cases go here.

---

### D8 — LiveMarketDataProvider Behavior

**Unknown:** What happens in Paper/Live mode before cTrader adapter (Phase 9) is complete?

**Options:**
- **A (recommended):** `LiveMarketDataProvider` throws `NotSupportedException("Live mode requires cTrader Phase 9")` with clear message.
- **B:** Silent stub — returns empty streams, no-ops on `SeekAsync()`. Engine runs with no data silently.
- **C:** Register `SimulatedBrokerAdapter` as both `IBrokerAdapter` and `IMarketDataProvider` in paper mode.

---

### D9 — NewsFilter Implementation

**Unknown:** Needs external ForexFactory news calendar feed. Format? URL? Update frequency?

**Options:**
- **A (recommended):** Stub `NewsFilter` that always returns "no news window active". Real implementation deferred to future phase.
- **B:** Hardcode a small set of known news events for testing only.

---

### D10 — Tick Synthesis from Bars

**Unknown:** `HistoricalDataProvider.StreamTicksAsync` "synthesises 4 ticks per bar" — exact timing?

**Options:**
- **A (recommended):** 4 ticks at `OpenTimeUtc + 0%, 25%, 50%, 75%` of bar duration. Order: Open → High → Low → Close (bullish) or Open → Low → High → Close (bearish). Bid = Close, Ask = Close + half typical spread.
- **B:** 4 ticks at bar boundaries only (same timestamp).
- **C:** Random timing within bar interval (breaks determinism).

---

### D11 — Slippage Determinism

**Unknown:** `SimulationOptions.SlippagePips` — fixed or randomized?

**Options:**
- **A (recommended):** Fixed offset = `SlippagePips`. No randomness. Reproducible.
- **B:** `RngSeed` + uniform distribution `[-SlippagePips, +SlippagePips]`. Seed controls determinism.

---

### D12 — FTMO dailyResetTimeUtc ✅ DECIDED

**Status:** Use `"22:00:00" UTC` (= midnight Prague CET). Domain doc is authoritative for financial rules per guide §1.5.

---

### D13 — SlMethod Enum ✅ DECIDED

**Status:** Use 3-value enum: `FixedPips, AtrMultiple, SwingBased`. TradeDirection already on TradeIntent — SwingHigh/SwingLow are redundant per guide §1.4.

---

### D14 — DurationSeconds in Schema ✅ DECIDED

**Status:** Add `double DurationSeconds` to `TradeResult` and `TradeResultEntity`. Computed as `(ClosedAtUtc - OpenedAtUtc).TotalSeconds`.

---

### D15 — MaxExposurePercent Metric

**Open:** "Max exposure exceeded" check — what is the metric?

**Options:**
- **A (recommended):** Sum of `Position.Lots × PipValue × SlPips` (total open risk) across all positions / current equity. Must not exceed `RiskProfile.MaxExposurePercent`.
- **B:** Sum of notional values (Lots × ContractSize × Price) / equity.
- **C:** Number of open positions only.

---

### D16 — Daily Reset Timer at Startup

**Open:** If engine starts at 22:30 UTC (after 22:00 daily reset time), should reset fire immediately?

**Options:**
- **A (recommended):** Yes — fire `OnDailyReset()` immediately if current time > today's reset time. Otherwise DD tracking is wrong until next day.
- **B:** No — wait until next scheduled reset time (22:00 next day).

---

### D17 — LiveMarketDataProvider / cTrader Details

**Open:** I need more info about the cTrader environment to design `LiveMarketDataProvider` and the cBot:
- Does cTrader's API support pushing tick data programmatically, or does the cBot poll?
- What is the cTrader API version / NuGet package? (cTrader API references?)
- How does the cBot get notified of new ticks/bars — events? callbacks?
- Will you provide access to a cTrader environment for testing?

---

### D18 — Test Data Source

**Open:** The plan needs `tests/data/eurusd-h1-sample.csv`. Options:
- I generate synthetic data via `CsvDataGenerator` and commit it
- You provide real historical data
- We use a well-known public source (e.g., Dukascopy, OANDA)

---

### D19 — # of Phases

**Current:** 10 phases (per agent-implementation-guide). Each phase = 1 PR.
- Too many? Should some merge?
- Too few? Should any split?

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

### Phase 1: Domain Types

**Branch:** `phase/01-domain`

**Scope:** ~55 files — all value objects, market data types, trading lifecycle records, events, interfaces. Zero logic.

**Validation:** `dotnet build src/TradingEngine.Domain --no-restore` → 0 errors, 0 warnings.

**Key rules:**
- Every file = one top-level type, all `public`
- Domain has NO NuGet packages except `Microsoft.Extensions.Logging.Abstractions`
- No `class` implementations (except `BrokerClock` and `StubClock`)
- `Position` record has NO `FloatingPnL()` method (guide §1.1)
- `IBrokerAdapter` includes `DateTime BrokerTimeUtc { get; }` (guide §1.3)
- `RiskProfile` includes `double MaxSlPips` (domain §5.4)
- `PositionManagementConfig` record defined (guide §1.2)
- `SlMethod` = 3 values: `FixedPips`, `AtrMultiple`, `SwingBased` (guide §1.4)

---

### Phase 2: Risk Engine

**Branch:** `phase/02-risk`

**Scope:**
- `TradingEngine.Risk/` — PositionSizer, RiskManager, DrawdownTracker, PropFirmRuleValidator, NewsFilter (stub), SessionFilter, DrawdownScaler
- `config/prop-firms/` — ftmo-standard.json, ftmo-aggressive.json
- `config/risk-profiles/` — conservative.json, standard.json, aggressive.json
- `TradingEngine.Tests.Unit/` — 14 risk tests

**Validation:** `dotnet build` + `dotnet test --filter "Category=Risk"`

**Key rules:**
- `PositionSizer.Calculate()` — use `Math.Floor`, never `Math.Round` (guide §7.3)
- `RiskManager.Validate()` — return ALL violations, not first-only (guide §3 Phase 2)
- `DrawdownTracker.InitialAccountBalance` — set once, never updated (domain §11.2)
- `PropFirmRuleSet` — `JsonUnmappedMemberHandling.Disallow` on deserialization
- `NewsFilter` — stub returning "no news" (D9)

---

### Phase 3: Infrastructure

**Branch:** `phase/03-infrastructure`

**Scope:**
- `TradingEngine.Infrastructure/` — Persistence (entities, mappings, DbContexts, repositories), Adapters (SimulatedBrokerAdapter, HistoricalDataProvider, LiveMarketDataProvider stub, NamedPipeBrokerAdapter), Skender wrapper, BufferedBarWriter
- `TradingEngine.Tests.Integration/` — 7 tests
- `tests/data/eurusd-h1-sample.csv`

**Validation:** `dotnet build` + `dotnet test tests/TradingEngine.Tests.Integration`

**Key rules:**
- Skender in Infrastructure (not Services — D1)
- EF Core entities flat, no navigation property chains on hot paths
- All enums stored as strings, DateTime as TEXT (ISO 8601 UTC), Money as two columns
- `ReportingDbContext` → `QueryTrackingBehavior.NoTracking`
- `BufferedBarWriter` → `Channel.CreateBounded<Bar>(10_000)`, `DropOldest`, batch=500
- `SimulatedBrokerAdapter` exposes `ChannelWriter<Tick>` / `ChannelWriter<Bar>` for external feed (D2)

---

### Phase 4: Services Layer

**Branch:** `phase/04-services`

**Scope:**
- `TradingEngine.Services/` — SlTpCalculator, TrailingStopService, PipCalculator, ExcursionTracker
- Unit tests: 11 service tests

**Validation:** `dotnet build` + `dotnet test --filter "Category=Services"`

**Key rules:**
- PipCalculator in Services — takes `getCrossRate` delegate (D4)
- SlTpHelpers static methods match domain doc §5 and §6 exactly
- TrailingHelpers static methods match domain doc §8 exactly
- All financial arithmetic with `decimal`

---

### Phase 5: Strategies

**Branch:** `phase/05-strategies`

**Scope:**
- `TradingEngine.Strategies/` — TrendBreakoutStrategy + config, MA Trend + Volatility Expansion skeletons
- `config/strategies/` — trend-breakout.json
- Unit tests: 7 strategy tests

**Validation:** `dotnet build` + `dotnet test --filter "Category=Strategy"`

**Key rules:**
- `Evaluate()` NEVER throws (guide §6 rule 1)
- `Evaluate()` is synchronous (guide §6 rule 1 — do NOT make async)
- `Evaluate()` receives `MarketContext.IndicatorValues` — never calls `IIndicatorService` directly
- `OnTradeResult()` must be thread-safe
- Check `context.Bars count >= RequiredBarCount` at top of `Evaluate()`

---

### Phase 6: Simulation Tests

**Branch:** `phase/06-simulation`

**Scope:**
- `TradingEngine.Tests.Simulation/` — EngineTestHarness, BacktestResult, CsvDataGenerator, scenarios
- `tests/data/eurusd-h1-sample.csv`
- 7 end-to-end simulation tests

**Validation:** `dotnet test tests/TradingEngine.Tests.Simulation` + reproducibility check

**Key rules:**
- `EngineTestHarness` builds minimal DI container (not full IHost)
- `DataFeedService` connects HistoricalDataProvider → SimulatedBrokerAdapter (D2)
- Deterministic: seeded RNG for slippage/rejection if randomness is added (D11)

---

### Phase 7: Host Wiring

**Branch:** `phase/07-host`

**Scope:**
- `TradingEngine.Host/` — EngineWorker, DataFeedService, DailyResetService, StrategyRegistry, ConfigLoader, Program.cs, appsettings*.json

**Validation:** `dotnet run --project src/TradingEngine.Host -- --mode backtest --from 2024-01-01 --to 2024-01-31` → exit 0

**Key rules:**
- Config loaded via `ConfigLoader` service (D5)
- Strategy resolution via `[StrategyId]` attribute + `StrategyRegistry` (D6)
- Execution events enqueued to tick processor channel, not processed independently (D3)
- DailyResetService fires immediately if past reset time on startup (D16)

---

### Phase 8: Web Viewer

**Branch:** `phase/08-web`

**Scope:**
- `TradingEngine.Web/` — Razor Pages (Dashboard, Trades, Trade Detail, Performance, Events), API controllers, SSE endpoint, Chart.js

**Validation:** `dotnet run --project src/TradingEngine.Web` → all routes return 200

**Key rules:**
- No JS framework, no npm/webpack (guide §2.12 LOCKED)
- Chart.js + chartjs-chart-financial plugin via CDN for candlesticks
- Bare CSS (no Bootstrap)
- SSE on /sse/risk streams RiskState JSON

---

### Phase 9: cTrader Adapter

**Branch:** `phase/09-ctrader`

**Scope:**
- `TradingEngine.Adapters.CTrader/` — C# 6 cBot with PipeClient, publishers, OrderCommandHandler

**Validation:** `dotnet build src/TradingEngine.Adapters.CTrader` → 0 errors, no C# 8+ features

**Key rules:**
- `<LangVersion>6</LangVersion>`, `<Nullable>disable</Nullable>`, target net48
- Newtonsoft.Json for serialization
- cBot connects to engine's named pipe server (D17)

---

### Phase 10: Aspire + CI/CD

**Branch:** `phase/10-aspire-cicd`

**Scope:**
- `aspire/TradingEngine.AppHost/Program.cs`
- `.github/workflows/pr.yml`, `.github/workflows/release.yml`

**Validation:** `dotnet build` + `dotnet test --no-build -c Release` → all pass

---

## ⚠️ Open Questions Summary

Please respond to these (D1–D19 above) before Phase 1 begins:

| ID | Topic | My Recommendation |
|---|---|---|
| D1 | Skender placement | A — Infrastructure |
| D2 | Backtest data path | A — DataFeedService |
| D3 | Concurrency model | A — Single-threaded tick processor |
| D5 | Config loading | A — ConfigLoader service |
| D6 | Strategy resolution | A — [StrategyId] attribute |
| D8 | LiveMarketDataProvider | A — throw NotSupportedException |
| D9 | NewsFilter | A — stub (deferred) |
| D10 | Tick synthesis | A — 4 ticks at 0/25/50/75% |
| D11 | Slippage determinism | A — Fixed offset |
| D15 | MaxExposurePercent metric | A — Sum of open risk / equity |
| D16 | Daily reset on late start | A — Fire immediately |
| D17 | cTrader API details | Awaiting your input |
| D18 | Test data source | Awaiting your input |
| D19 | Number of phases | Awaiting your input |

---

## Progress Tracking

| Phase | Branch | Status | PR | Merged |
|---|---|---|---|---|
| Pre-Phase | `chore/init-repo` | ❌ Not started | — | — |
| 1 — Domain | `phase/01-domain` | ❌ Not started | — | — |
| 2 — Risk | `phase/02-risk` | ❌ Not started | — | — |
| 3 — Infrastructure | `phase/03-infrastructure` | ❌ Not started | — | — |
| 4 — Services | `phase/04-services` | ❌ Not started | — | — |
| 5 — Strategies | `phase/05-strategies` | ❌ Not started | — | — |
| 6 — Simulation | `phase/06-simulation` | ❌ Not started | — | — |
| 7 — Host | `phase/07-host` | ❌ Not started | — | — |
| 8 — Web | `phase/08-web` | ❌ Not started | — | — |
| 9 — cTrader | `phase/09-ctrader` | ❌ Not started | — | — |
| 10 — Aspire/CI | `phase/10-aspire-cicd` | ❌ Not started | — | — |
