# Shamshir — Resolved Issues

All issues that have been marked `✅ Fixed` across iterations. This file is an audit
trail — items are never deleted, only added.

---

## Iteration 17 — Deterministic Pipeline

- **NetMQ thread-affinity bug** (orders silently lost) → Fixed with `NetMQQueue<T>` (Phase A1)
- **Sleep-based synchronization** → Replaced with `hello`/`hello_ack` handshake (Phase A2)
- **Shutdown data loss** → Fixed with `Linger=2s`, `stats` message, drain on stop (Phase A3)
- **DI block copied in 3 places** → `EngineHostFactory` single composition root (Phase B1)
- **EngineMode type-sniffing** → Explicit `EngineMode` parameter (Phase B1)
- **Hardcoded SymbolInfo** → `config/symbols.json` + `SymbolCatalog` (Phase B2)
- **CrossRateStore double-instance** → Single instance registered (Phase B1)
- **Dual execution-event consumer** → Single consumer via double-drain (Phase B3)
- **Lock-step protocol** → Implemented in cBot + engine (Phase C)
- **Symbol wrong for GBPUSD/AUDUSD** → Fixed in controller + page model (Phase B2)
- **EF Core SQL log flood** → `.AddFilter("Microsoft.EntityFrameworkCore", Warning)`
- **Close position ID mismatch** → Fixed with `_positionMap` reverse-lookup
- **PipelineEvents journal** → Entity, mapping, writer, repository (Phase D1)
- **Unified logging** → Consolidation to `BacktestJournal` (Phase D2)
- **Multi-symbol/timeframe UI** → 12 symbol checkboxes, 6 timeframe checkboxes

## Iteration 11 — BacktestReplayAdapter Fixes

- **BUG-01** — BacktestReplayAdapter never fills orders → `SubmitOrderAsync` writes `ExecutionEvent` directly. `SimulateFill` removed.
- **BUG-02** — Silently drops bars >2,000 → Channels changed to unbounded. `ConnectAsync` starts `FeedBarsAsync` as background task.
- **BUG-03** — Force-close silently does nothing; exit reason wrong → `ClosePositionAsync` sends `_lastClose` as fill price. `DetermineExitReason` replaces incorrect ternary.

## Iteration 12 — Metrics

- **BUG-04** — Max drawdown fabricated → `GetTradeStatsAsync` builds cumulative equity curve, computes peak-to-trough drawdown.
- **DESIGN-05** — Failed backtests create orphaned trade records → `WriteStartRecordAsync` writes in-progress record. `WriteEndRecordAsync` updates on completion/failure.

## Iteration 13 — Observability

- **DESIGN-06** — `BarEvaluationHandler` drops events on shutdown → `DisposeAsync` drains remaining channel events.
- **STD-03** — `BAR_EVAL` at Information level → Changed to `LogDebug`.

## Iteration 16 — cTrader In-Process

- **BUG-05** — Hardcoded cross-rates → `CrossRateStore` with mutable fields. Updated per bar from primary symbol's close price.
- **DESIGN-02** — Execution events only drained when ticks arrive → `DrainExecutionStreamAsync` in `ProcessBarsAsync` live path.
- **DESIGN-03** — `Cancel()` doesn't kill process → `BacktestRunState` stores `CancellationTokenSource` per run.
- **DESIGN-07** — `BacktestOrchestrator.RunAsync` fire-and-forget → `BacktestRunState.RunTask` property. `StopAllAsync()` graceful shutdown.
- **OBS-04** — No equity curve captured → `GetEquityAsync` on `IBacktestQueryService`. Queries `EquitySnapshots`.

## Iteration 18 — Schema Cleanup

- **STD-07** — Raw SQL `ALTER TABLE` in `Program.cs` → Replaced with proper EF migration.

## Iteration 27 — Web UI Fixes

- **UI-01** — Live Monitor "stuck on connecting" → Converted `run-client.js` IIFE to ES module.
- **UI-02** — Strategy picker ignored → `EngineHostOptions.ActiveStrategyIds` + `StrategyRegistry.SelectActiveIds`.
- **UI-03** — Monitor funnel counters always 0 → `TallyEvent` remapped to engine event names.
- **UI-04** — Report funnel inflated by per-bar noise → Signals = accepted orders + rejects. `ReportModel.BuildFunnel`.
- **UI-05** — Report equity curve always empty → Derived realized-equity curve from run's trades.
- **UI-06** — Lifecycle records persisted with `RunId=""` → `PipelineEventWriter.Record` stamps own run id.
- **UI-07** — Hardcoded `/api/performance` stub; trade detail balance from 0; sim clock date-only → Fixed.

## Iteration 31/32 — Costs, Journal, Config

- **VENUE-01** — Costs + limit orders in wrong venue → Ported to `BacktestReplayAdapter` via shared `TradeCostCalculator`.
- **COST-01** — Divergent gross-PnL formulas → All venues route through `TradeCostCalculator.Compute`.
- **ENGINE-01** — Limit cancellation mis-handled as phantom fill → `OrderCancelled` event + `PositionPhase.Cancelled` + ENTRY_EXPIRED journal.
- **JOURNAL-01** — Closes hidden under FILL; close detail empty; signal reason absent → Closes normalize to CLOSE with cost detail. SIGNAL records with reason.
- **SEEDER-01** — StrategyConfigSeeder crashed on fresh DB → `.Clone()` on `JsonElement` + `Undefined` guard.
- **MIGRATIONS-01** — Collapsed to single `InitialCreate`.
- **WEB-BUILD-01** — Web project didn't compile → Restored missing global using.
