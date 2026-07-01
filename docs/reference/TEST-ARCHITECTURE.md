# Test Architecture

**Last updated**: 2026-06-18 (post iter-31/32)

How the test suite is layered, what each layer proves, what it hides, and which tests
need credentials. Complements `tests/README.md` (basic taxonomy) and
`BACKTEST-ARCHITECTURE.md` (backtest venue paths).

---

## 1. The four tiers

```
Architecture   ← Reflection invariants (3 tests, <1s)
    ↓
Unit           ← Pure logic, NSubstitute mocks, no DI, no DB (207 tests, ~4s)
    ↓
Integration    ← Real DI + real SQLite + WebApplicationFactory (35 tests, ~10s)
    ↓
Simulation     ← Full engine: harnesses + cTrader CLI (40+ tests, 30–300s)
```

### Architecture (`TradingEngine.Tests.Architecture`)

Boundary invariants enforced by reflection. Catches layer violations that the compiler
wouldn't (e.g. `TradingEngine.Engine` accidentally referencing Infrastructure).

- `Engine_references_only_Domain` — Engine assembly has zero project refs beyond Domain
- `Engine_has_no_ILogger_no_DateTimeNow` — No logging, time, or EF types leaked into Engine
- `EngineMode_only_in_host_and_infrastructure` — Enum confined to composition root

### Unit (`TradingEngine.Tests.Unit`)

Pure xUnit + NSubstitute. No DI, no DB, no IHost, no channels. Fastest feedback
loop — run after every change.

| Category | What it tests | Key classes |
|----------|--------------|-------------|
| Engine / Reducers | State transitions: drawdown, lifecycle, risk gate, governor | `EngineReducerTests`, `DrawdownReducerTests`, `PositionLifecycleTests`, `RiskGateTests` |
| Position | Fill handling, trailing, reconciliation, concurrency | `PositionLifecycleTrailingTests`, `PositionTrackerReconciliationTests`, `PositionTrackerConcurrencyTests` |
| Risk | Lot sizing, drawdown scaling, FTMO rule enforcement, exposure tracking | `RiskManagerTests`, `DrawdownScalerPhase3BTests`, `PositionSizerTests`, `PropFirmRuleValidatorTests` |
| Services | Pip math, SL/TP, order dispatch, signal gate, exit reasons | `PipCalculatorTests`, `SlTpCalculatorTests`, `OrderDispatcherTests`, `ExitReasonTests` |
| Costs & Journal | Commission/swap formulas, limit order fill logic, journal normalization | `TradeCostCalculatorTests`, `BacktestReplayCostsAndLimitsTests`, `JournalNormalizerTests` |
| Config | Effective config resolution, deep-merge semantics | `EffectiveConfigResolverTests` |
| Strategy | Signal generation | `TrendBreakoutStrategyTests` |
| Infrastructure | Transport message parsing, indicator cache keys, regime detection | `FakeTransportTests`, `IndicatorCacheKeyTests`, `RegimeDetectorTests` |

Run: `dotnet test tests/TradingEngine.Tests.Unit`

### Integration (`TradingEngine.Tests.Integration`)

Real DI container, real SQLite/EF Core, `WebApplicationFactory` test host. Validates that
services resolve, schema migrates, repositories persist, and HTTP endpoints return 200.

- `DIValidationTests` — All core services resolve from container
- `MigrationTests` — Fresh database migrates cleanly
- `TradeRepositoryTests`, `UnifiedDecisionJournalTests` — CRUD round-trips
- `WebSmokeTests` — All Razor pages and API endpoints return 200
- `RunProgressContractTests` — SSE/API contract shapes
- `BacktestReplayAdapterTests` — Replay adapter with substitute bar repo

Run: `dotnet test tests/TradingEngine.Tests.Integration`

### Simulation (`TradingEngine.Tests.Simulation`)

Full end-to-end pipeline. The richest layer — uses multiple harness strategies depending on
what needs to be validated. This is where FTMO rules, strategies, and the complete
signal→order→fill→close flow are proven.

Run: `dotnet test tests/TradingEngine.Tests.Simulation` (expect several minutes)

---

## 2. Harness infrastructure

The simulation project has a harness layer that assembles trading engines at different
fidelity levels. Tests pick the harness that matches their validation concern.

### In-memory harness (`EngineHarnessBuilder`)

**Used by**: FTMO journey tests, scenario tests, strategy tests, position management tests.

Assembles a complete but fake engine: real `TradingLoop`, `PositionTracker`, `RiskManager`,
`OrderDispatcher`, `AccountProcessor`, `EntryPlanner` — but a `FakeVenue` (in-memory
channels), `ManualClock`, and `CollectingEventBus`. No IHost, no DB, no network.

```
EngineHarnessBuilder
  .WithStrategy(strategy)
  .WithBars(bars)
  .Build()
    → EngineHarness
      → DriveBarsAsync()   ← bar-by-bar control
      → ClosedTrades       ← assertions on PnL, exits, counts
      → Funnel / Journal   ← decision sequence assertions
```

Bar-by-bar control means tests can assert after specific bars. `SimulateBarExitsAsync`
checks SL/TP hits per bar. `ReconcileAssert` cross-verifies NetPnL == sum(trade nets)
== equity-curve delta == funnel close count.

**What it hides**: Real broker transport, channel backpressure, host lifecycle, DB
persistence, real indicators (strategies get pre-computed or null indicator values).

### Replay harness (`ReplayTestHarness`)

**Used by**: `BacktestReplayTests`.

Full `IHost`-based engine with `BacktestReplayAdapter`, real SQLite, real `EngineWorker`,
real Skender indicators. Uses NSubstitute for `RiskManager`/`Governor` (permissive by
default). Asserts DB contents after the run.

**What it hides**: cTrader transport, network. Everything else is real.

### cTrader harness (`CtraderTestHarness`)

**Used by**: `CtraderPipelineDiagnosticTest`, `CtraderScenarioTests`, `FullBacktestPipelineTest`,
`InProcessCtraderTest`.

Launches `ctrader-cli` as a subprocess with the real cBot `.algo` file. Engine runs in
full hosted mode with `CTraderBrokerAdapter`. NetMQ lock-step protocol. Real cTrader
backtester provides bars and fills.

Requires credentials. Tests skip automatically when `CTrader:CtId` is not configured.

### NetMQ harness (`NetMQBridgeTest`)

Spawns `dotnet run` for `TradingEngine.Host` as a subprocess, sends NetMQ bars via PUB
socket directly (no ctrader-cli). Exercises the full NetMQ transport + engine host +
`CTraderBrokerAdapter` without needing cTrader credentials.

### Other harness components

| Component | Purpose |
|-----------|---------|
| `FakeCBot.cs` | Standalone fake cBot speaking NetMQ protocol (PUB + DEALER). Handles hello, sends bars, collects commands. |
| `FakeVenue.cs` | `IBrokerAdapter` with in-memory channels. Auto-fills at `CurrentMarketPrice`. Exposes `SubmittedOrders`/`CloseRequests`. |
| `BarBuilder.cs` | Fluent synthetic bar generator: `.Trend()`, `.Range()`, `.Spike()`, `.Gap()` with pip-based DSL. |
| `ManualClock.cs` | Settable `IEngineClock` for time-dependent tests. |
| `AlwaysSignalStrategy.cs` | Fires one signal after warmup; waits for trade result before next. |
| `RapidFireStrategy.cs` | Fires every bar (no cooldown). Used for portfolio worst-case projection. |
| `InMemoryDecisionJournal.cs` | Collects `DecisionRecord` items for assertion. |
| `CollectingEventBus.cs` | Stores all events with typed handler support. |
| `ReconcileAssert.cs` | Cross-verification helper: NetPnL == sum(trade nets) == equity delta. |

---

## 3. Credential-dependent tests (cTrader CLI)

cTrader tests are the **verification layer** — they exercise the full engine + cBot +
ctrader-cli + NetMQ stack against real historical data. They are the closest thing to
live trading. Run them for important end-to-end checks and to validate strategy
performance against realistic fills. Once the system matures, these will become
diagnosis-only — run only when a suspicious divergence appears between the
credential-free backtest and expected results.

All cTrader tests live in `TradingEngine.Tests.Simulation/Pipeline/` and use a shared
`[Collection("CtraderSerial")]` to prevent parallel CLI execution.

| Test | What it verifies |
|------|-----------------|
| `CtraderPipelineDiagnosticTest` | EURUSD H1 3-day + 30-day produce trades and bar evaluations |
| `CtraderScenarioTests` | Trade ledger integrity, multi-symbol, risk stop honors drawdown, state machine logging, funnel counters |
| `FullBacktestPipelineTest` | 3-month EURUSD H1, 3-day multi-symbol parametric |
| `InProcessCtraderTest` | In-process engine + ctrader CLI, 1-day produces trades |

**Credential chain:**

```
appsettings.Development.json    →  CTrader:CtId, CTrader:PwdFile, CTrader:Account
   OR
Environment variables           →  CTrader__CtId, CTrader__PwdFile, CTrader__Account
                    │
                    ▼
        CtraderTestHarness.ResolveCredential()
                    │
        ┌───────────┼───────────────┐
        ▼           ▼               ▼
  CtraderScenario  InProcess     CtraderPipelineDiag
  FullPipeline     NetMQBridge
```

If credentials are missing, tests skip (no failure). Two tests also carry
`[Trait("RequiresCTrader", "true")]` for CI filtering.

**To run cTrader tests:**

```powershell
# Set credentials
$env:CTRADER__CTID = "your-ctid"
$env:CTRADER__PWDFILE = "path\to\pwd.txt"
$env:CTRADER__ACCOUNT = "12345"

# Run only cTrader tests
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"

# Skip cTrader tests (CI default)
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true"
```

---

## 4. Test data pipeline

### Synthetic bar generation (in-memory)

`BarBuilder` fluent DSL generates bars in test code:
```csharp
var bars = Bars.Trend()
    .From(1.1000m).Up(50).Pips(8).Noise(3)
    .Count(200)
    .Build();
```

Also `MakeBars()` / `MakeDownLeg()` helpers for common patterns. Used by all in-memory
harness tests.

### CSV generation (committed test data)

`CsvDataGenerator` produces deterministic CSV files from seeded `Random(42)`:

| File | Bars | Scenario |
|------|------|----------|
| `eurusd-h1-bull-2024.csv` | 2000 | Steady uptrend |
| `eurusd-h1-bear-2024.csv` | 2000 | Steady downtrend |
| `eurusd-h1-ranging-2024.csv` | 2000 | Oscillating range |
| `eurusd-h1-ddcrash-2024.csv` | 500 | Sharp 6% daily drop |
| `eurusd-h1-maxdd-2024.csv` | 500 | 11% drawdown over 2 weeks |
| `usdjpy-h1-bull-2024.csv` | 2000 | JPY pair pip value test |

Used by `HistoricalDataProvider` → `SimulatedBrokerAdapter` path in simulation tests.

### Real market data (cTrader)

cTrader CLI tests fetch live historical data from cTrader servers via the cBot
backtester. No committed data files — data comes from the broker's historical feed.

---

## 5. What each test layer proves (and what it doesn't)

| Layer | Proves | Doesn't prove |
|-------|--------|---------------|
| **Architecture** | Layer boundaries are enforced by analyzers + reflection | Correctness of any logic |
| **Unit** | Individual component correctness (reducer transitions, math, validation) | Integration between components, DI wiring, channel backpressure, DB persistence |
| **Integration** | DI resolves, DB migrates, endpoints return 200, repositories persist | Strategy signal quality, risk enforcement, multi-bar scenarios, fill simulation |
| **Simulation (harness)** | Signal→fill→close flow, FTMO rule enforcement, PnL integrity, journal capture | Real broker transport, NetMQ framing, process lifecycle, real market data quality |
| **Simulation (cTrader)** | Full pipeline with real broker: NetMQ, cBot, cTrader backtester, real historical data | None — this is the ground truth |

**The key gap**: A passing unit + integration suite does NOT guarantee the engine actually
trades. The simulation tier (particularly the harness tests) is where that is proven.
A harness-green backtest that produces zero trades in the cTrader path means the transport
or timing layer is broken.

---

## 6. Running tests — quick reference

```powershell
# Fast feedback (under 5s)
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Architecture

# Medium (under 30s)
dotnet test tests/TradingEngine.Tests.Integration

# Full simulation (several minutes, credential-free subset)
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true"

# Credential-gated (needs CTrader__CtId env var)
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true"

# Stop-the-line gates (must be green before any PR)
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Architecture
```

## 7. Adding new tests — which tier?

| You want to test... | Use |
|---------------------|-----|
| A pure calculation or validation rule | Unit (NSubstitute for deps) |
| A state transition (reducer, lifecycle) | Unit |
| DI registration / service resolution | Integration |
| DB schema migration or repository CRUD | Integration |
| HTTP endpoint returns correct status code | Integration (WebSmokeTests) |
| Signal → fill → close end-to-end | Simulation (EngineHarnessBuilder) |
| FTMO rule enforcement over multiple bars | Simulation (EngineHarnessBuilder) |
| Position management (trailing, breakeven) | Simulation (EngineHarnessBuilder) |
| Cost computation over held positions | Simulation (EngineHarnessBuilder or ReplayTestHarness) |
| Full NetMQ transport + engine host | Simulation (NetMQ or cTrader harness) |
| Real cTrader backtester integration | Simulation (CtraderTestHarness, needs credentials) |
