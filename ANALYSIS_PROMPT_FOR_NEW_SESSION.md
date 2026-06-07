# Analysis Prompt: Data Flows, Pipes/IPC, Bottlenecks, and Testing Approaches

**Target:** Fresh agent session (Claude, Grok, etc.) with no prior conversation history on this repo.

**Your task:** Perform a deep, read-only analysis of the current Shamshir trading engine codebase. Focus on data movement, inter-process / inter-component communication (especially the cTrader backtest path), current routing mechanisms, bottlenecks, limitations, and the testing strategy (particularly simulated vs "realistic" backtesting).

You are **not** to implement fixes. Your output should be a clear, structured diagnostic report (similar in spirit to a detailed code review + architecture archaeology note). Use the freedom described below to surface issues across layers.

---

## Mandatory Context You Must Internalize First

### 1. Project Reality (Current State Snapshot)

This is a .NET 10 algorithmic trading engine targeting prop-firm rules (primarily FTMO-style). It has **two parallel universes** for getting market data and execution events into the core engine:

**A. Synthetic / In-Process Backtest Path (the "clean" one)**
- `HistoricalDataProvider` (reads committed CSV files from `tests/data/`, synthesizes 4 ticks per bar).
- `DataFeedService` (IHostedService, only active in Backtest mode).
- `SimulatedBrokerAdapter` (implements `IBrokerAdapter` using internal `Channel<T>` + public `ChannelWriter`s + a critical `OnTickReceived(Tick)` method that does pending-order fill simulation + SL/TP hit detection + balance mutation).
- Engine runs in `EngineMode.Backtest`.
- `EngineWorker` detects this path with repeated `if (_broker is SimulatedBrokerAdapter ...)` type checks.
- This path is heavily used by `EngineTestHarness` in simulation tests.

**B. "Realistic" cTrader CLI Backtest Path (the one that actually exercises the cBot)**
- `BacktestRunner` + `CTraderRunner` launches `ctrader-cli` against a built `src.algo` (the cBot).
- The cBot (`TradingEngineCBot` in `TradingEngine.Adapters.CTrader`, net6.0) runs inside cTrader's backtester.
- Data transfer from cBot → engine has gone through **multiple failed experiments** (see history below).
- Currently relies on a **stdout → temp file → polling FileBrokerAdapter** approach (post-Iteration 6 pivot).
- Engine is started in "Live" mode (or equivalent) listening on a `FileBrokerAdapter`.
- Commands (SubmitOrder etc.) from engine back to cBot are currently problematic or one-way only in this path.
- `FullBacktestPipelineTest` exercises the full multi-process stack.

The `IBrokerAdapter` interface (with its 4 `ChannelReader` streams + command methods) is the main seam. Both paths are forced to look the same to `EngineWorker`.

### 2. Recent History of Data Transfer Attempts (Critical — Read This)

The project has had a painful, iterative battle with getting data out of the cTrader backtester into the engine:

- **Original design**: Bidirectional named pipes (`NamedPipeBrokerAdapter` + `PipeClient` in the cBot). Length-prefixed JSON, `PipeMessage { Type, Payload }` envelope (double serialization), Newtonsoft on cBot side, System.Text.Json on engine side.
- **Iteration 5 (phase/5-backtest-flow)**: Many fixes around correlation IDs (`ClientOrderId`), DrawdownTracker init, lot sizing entry price, engine subprocess launching from `BacktestRunner`, Serilog levels, etc. The assumption was still "named pipe will work."
- **Iteration 6 discovery (the big pivot)**: `ctrader-cli` (the backtester host) **blocks**:
  - `NamedPipeClientStream.Connect()` (always fails).
  - TCP loopback sockets.
  - File writes to %TEMP% in some cases.
- **Current workaround (stdout-based)**: cBot uses `Print("DATA|{json}")`. `BacktestRunner` captures stdout, extracts lines, writes them to a temp `.jsonl` file. A new `FileBrokerAdapter` polls the file (every 100ms, tracks `lastLength`, seeks and reads new lines) and feeds the 4 channels.
- Result: The "realistic" backtest path became **one-directional** (cBot → engine only). Order submission / command channel is incomplete or stubbed in the file path.
- Artifacts left behind: `FileBrokerAdapter.cs`, modified `TradingEngineCBot.cs` (still carries a lot of old pipe wiring + `PipeClient`, `TickPublisher` etc. that may be conditionally dead), unused `TcpBrokerAdapter`, modified `PipeClient`, etc.
- Known symptoms after the pivot (from ITERATION-6-HANDOVER):
  - Only ~1 BAR processed per run (engine killed too early; drain timing issues).
  - File polling is slow (~100 ticks/sec) and can drop events between polls.
  - 30s+ artificial drain waits in tests.
  - Two-way communication is missing.

This history is documented in:
- `docs/ITERATION-6-HANDOVER.md`
- `ITERATION-5.md`
- `DECISIONS.md` (especially D54–D69 and the transport-related entries)
- `BACKTEST-SMOKE-TEST.md` (still describes the old named-pipe smoke test procedure)

### 3. Known Data Flow / Pipe / Bottleneck Heads-Up (Read Before Exploring)

**Primary data directions today:**
- Market data (Tick/Bar) and events (AccountUpdate, ExecutionEvent): Always adapter → channels → `EngineWorker` (4 concurrent `ProcessXxxAsync` loops) → strategies (tick-driven `Evaluate(MarketContext)`), risk, trackers.
- Commands: Strategy → `OrderDispatcher` → `IBrokerAdapter.SubmitOrderAsync` (and Modify/Cancel/Close) → adapter (pipe or simulated).
- In synthetic path: `DataFeedService` writes to `SimulatedBrokerAdapter`'s writers **and** calls `OnTickReceived`. `EngineWorker` also calls `OnTickReceived` on ticks for SimulatedBroker (potential double execution path — worth verifying current state).
- Bars are accumulated in `EngineWorker` (ConcurrentDictionary + lock, capped at 500) for context + indicator recompute. Strategies only see snapshots on ticks.
- `LiveMarketDataProvider` reads *back out* of the broker's streams (circular for pipe/file paths).

**Current transport realities:**
- Named pipe code still exists and is used in some "Live" Aspire paths and old smoke tests, but is known-broken for ctrader-cli backtests.
- File polling (`FileBrokerAdapter`) is the active mechanism for the "full pipeline" realistic backtest.
- `PipeClient` on cBot side still uses a dedicated background `Thread` + blocking synchronous reads.
- Serialization split (Newtonsoft vs STJ) + stringly-typed `Payload` envelope remains in pipe code.
- Reconnection, state reset (`EngineWorker.ResetState` on `OnClientConnected`), and initial state sync (`SendInitialState`) are complex and have been sources of lost positions/equity.

**Architectural smells visible from prior work:**
- Heavy `is SimulatedBrokerAdapter` type checks for mode-specific behavior (in `EngineWorker`, `DataFeedService`, `Program.cs`).
- `EngineMode` enum exists but is not the authoritative router.
- `SimulatedBrokerAdapter` has its own internal position/pending dicts + balance mutation + fill logic triggered from `OnTickReceived`. This is both the execution simulator *and* the data source.
- Test harness (`EngineTestHarness`) deliberately bypasses channels, `DataFeedService`, and `EngineWorker` entirely for determinism — it manually synthesizes ticks and drives the broker + strategies directly.
- Persistence and `IEventBus` (TypedEventBus) are mostly fire-and-forget side effects.

**Explicitly called-out limitations (from recent handover):**
- One-way data transfer in the realistic cTrader backtest path.
- Polling-based file IPC is lossy and slow.
- Early termination / drain problems mean strategies rarely see enough bars.
- Remnant dead/commented/dual code paths from the transport pivots.
- cBot still has pipe-related fields and connection attempts even in CLI runs.

You have explicit permission (and are encouraged) to spot-check **other** bottlenecks while doing this work:
- Concurrency / channel backpressure / ordering races (tick vs bar processors, execution draining on tick path, etc.).
- Equity / risk / drawdown initialization timing (historical C-4 class issues around AccountUpdate emission).
- Strategy evaluation data freshness (bars vs ticks, indicator warm-up, snapshotting under locks).
- Multi-process lifecycle and cleanup (engine kill in finally blocks, temp file management, Aspire vs direct subprocess).
- Serialization and framing robustness.
- Any other cross-cutting data movement or state synchronization issues you discover.

### 4. Testing Approaches — Simulated vs Realistic (Give This Attention)

There are at least three distinct testing "universes":

1. **Pure unit** (`TradingEngine.Tests.Unit`): Isolated RiskManager, PositionSizer, strategies (with fake indicators), etc.
2. **Simulated / harness-driven** (`TradingEngine.Tests.Simulation` + `EngineTestHarness`): Fluent builder that wires `HistoricalDataProvider` → manual tick synthesis → direct calls into `SimulatedBrokerAdapter.OnTickReceived` + `SubmitOrderAsync` + manual draining of execution events. Bypasses the entire Host/Worker/DataFeed/Channel machinery. Very deterministic, fast, good for signal + risk correctness. Uses seeded CSV generators in some scenarios.
3. **Full pipeline / multi-system** (`FullBacktestPipelineTest`, smoke procedures): Actually spawns `dotnet run` for the engine (sometimes as subprocess), launches `ctrader-cli` with the real `.algo`, requires real cTrader credentials, captures logs, asserts on `BAR|`, `TICK|`, `SIGNAL|YES` output, etc. These are slow (60s+), environment-heavy, and exercise the real IPC + cBot + backtester stack.

**Agent-friendly property**: The pipeline tests and harnesses are deliberately structured to run "whole systems" (engine + data source + risk + strategies + persistence side effects). This makes them excellent for an agent to iterate across multiple components at once when diagnosing data flow, timing, or integration bugs — you don't have to mock the world to see a real signal or a missing AccountUpdate.

When analyzing, call out:
- What each layer actually validates (and what it hides).
- Gaps between "harness green" and "realistic pipeline" behavior.
- Whether the current simulated approach is too clean (bypassing the exact concurrency and transport issues that bite in production/backtest runs).
- Any opportunities or risks in how these tests are written for future agent-driven debugging.

---

## Instructions for Your Analysis

1. **Start by reading (in rough order)**:
   - `docs/WORKFLOW.md`
   - `DECISIONS.md` (especially transport decisions D54+, simulation phases, and the history of broker adapters)
   - `docs/ITERATION-6-HANDOVER.md` (the most recent painful pivot)
   - `ITERATION-5.md` (context for what was being "fixed" before the transport discovery)
   - `BACKTEST-SMOKE-TEST.md`
   - Key source: `IBrokerAdapter.cs`, both `NamedPipeBrokerAdapter.cs` and `SimulatedBrokerAdapter.cs` + `FileBrokerAdapter.cs` if present, `DataFeedService.cs`, `EngineWorker.cs` (the 4 processors + tick hot path), `Program.cs` (mode wiring), `TradingEngineCBot.cs` + the publishers/handlers/PipeClient, `BacktestRunner.cs`, `EngineTestHarness.cs`, and a couple of strategies + `PositionTracker.cs` / `OrderDispatcher.cs`.
   - Any recent logs or test output files you find illuminating.

2. **Map the actual data flows** (draw them mentally or in your report):
   - Tick/Bar/Account/Execution from source → channels → EngineWorker → strategies/risk/positioning.
   - Reverse command flow.
   - How "Live" (pipe) vs Backtest (simulated) vs "realistic cTrader CLI" (current stdout/file) paths differ in practice.
   - Where state is duplicated (bars dicts, pending orders, positions, equity snapshots, etc.).
   - Side channels (event bus, persistence fire-and-forget, web SSE).

3. **Specifically pressure-test pipes / IPC / data transfer**:
   - Current active mechanism(s) and their directionality.
   - What survived from the named-pipe era vs what was added in the file pivot.
   - Reconnection, initial state, correlation (ClientOrderId), timing, lossiness, and two-way gaps.
   - Why ctrader-cli is such a hostile environment for normal IPC.

4. **Use your freedom**:
   - Spot other bottlenecks (concurrency, ordering, resource, lifecycle, serialization, etc.).
   - Critique the testing strategy — simulated vs realistic, what each catches, what each masks, how agent-friendly the current setup really is for multi-system iteration.
   - Note technical debt, remnant code, mode-detection smells, and anything that would make future reliable data flow hard.
   - If you find inconsistencies between docs/handovers and current code, call them out (the repo may be mid-refactor).

5. **Output format** (make it useful for the next human or agent):
   - Executive summary of the two (or three) data universes.
   - Detailed current data flow + direction map.
   - Pipe / IPC / transport section with history, current state, and limitations.
   - Bottlenecks and risks (categorized: critical for correctness, performance, maintainability, testability).
   - Testing approach analysis (simulated harness strengths/weaknesses, pipeline test realities, gaps between them).
   - Concrete recommendations / questions for future work (no code changes from you).
   - Any other observations.

---

## Tone and Constraints

- Be precise and cite specific files/lines where possible.
- Distinguish "what the design intended" vs "what actually runs today" vs "what recent attempts tried and why they pivoted."
- You are allowed (encouraged) to be direct about painful realities — this codebase has had multiple transport pivots and workarounds under time pressure.
- Do **not** propose or write code changes. Analysis and reporting only.
- Treat the full-pipeline tests as first-class citizens for agent iteration: they are one of the few ways to exercise "engine + cBot + real backtester + data transfer + risk + strategies" together.

---

## Why This Prompt Exists

The data routing and pipe/IPC layer has been one of the most iterated-on and fragile parts of the system. Multiple "working" states existed in simulation that did not survive contact with ctrader-cli. A fresh pair of eyes that understands the history, the two backtest worlds, the channel model, the mode hacks, and the testing split will be able to surface systemic issues that incremental fixes have papered over.

Good hunting. Report back with clarity.