# Shamshir — Resolved Issues

All issues that have been marked `✅ Fixed` across iterations. This file is an audit
trail — items are never deleted, only added.

---

## Iteration 37 — Frontend Finish + Pressure Tests + Dead-Code Sign-off (`iter/37-frontend-finish`)

**iter-36 cutover follow-ups (K-GAP):**
- **K-GAP-1** day/week/month roll emitted in the kernel loop; DD re-bases to current equity (C4/H7 now provably closed multi-day) → `ResetClock` + `EngineReducer` + `KernelResetMultiDayTests`
- **K-GAP-2** backtest equity persisted (on-completion batch flush; `PersistentEquitySink` mode no longer hard-coded Live) → `EquitySnapshotFlush` + `BacktestEquityFlushTests`
- **K-GAP-3** per-run bars persisted on the kernel path (live/non-catalog charts render) → `EngineRunner.ReportBar` publishes `BarIngested` + `PerRunBarPersistenceTests`
- **K-GAP-4** report/funnel readers repointed off the empty tables onto the StepRecord journal → `RunProjection`/`BacktestQueryService` + `StrategyBreakdownFromJournalTests`
- **K-GAP-6** multi-symbol fill attribution (venue stamps `ExecutionEvent.Symbol`; pump prefers it) → `MultiSymbolAttributionTests`
- **M6** profit target confirmed keying off equity

**Pressure/reality test spine (TEST-PLAN):** G (governor/drawdown/protection ×12), F (FTMO daily-vs-overall + profit target), J (journal source-of-truth ×9), E (equity persistence), B (chart/timeframe), C (per-strategy characterization), D (multi-symbol + replay/duplicate).

**Frontend (PLAN F1–F8):** unified journal (order/fill join, named violations, full kind filter, badges), per-strategy funnel, NDJSON download + Duplicate + lineage, report stats + MAE/MFE scatter + JSON/MD export, live-monitor stick-to-bottom + balance-null fix (L1/L2/L3/NEW-7), per-trade SL/TP chart + TF column, risk-profile validate-before-save, new-backtest overrides + resolved-config preview, dashboard placeholder hygiene, real CSV export (M20).

**Dead-code sign-off (D-drop):** deleted `PipelineEvents`/`BarEvaluations` (entities/mapping/repo/interface/DTO/`JournalNormalizer`), dead consumers (`EventsController` + events page, `BacktestController.Journal`, `RunQueryService.GetRunJournalAsync`), and the never-fired protection-ledger path (handlers/`ProtectionQueryService`/`ProtectionController`/ledger tables/`compliance` page). EF reset → fresh `InitialCreate` with no dead tables. `grep PipelineEvent|BarEvaluationEntity|ProtectionLedger src → 0`.

**Empty/invalid backtest guard:** API 400 on no-symbol / inverted range / non-positive balance (`BacktestStartGuardTests`).

**Still open:** K-GAP-5 (per-trade Timeframe column, Low); F7 server-side validation framework; cTrader-E2E/NetMQ (env/owner-verified).

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

## Iteration 35 — Kernel Skeleton + Parts A & B

### Kernel gate bugs fixed (kernel authoritative; old paths to be deleted in cutover)
- **C3/H1** — Trailing max-DD floor uses `equity.Equity` not `PeakEquity` → `DrawdownState.GetMaxDrawdownFloor` in `PreTradeGate`
- **C4** — MaxDD protection never auto-exits → `ProtectionState.ClearsOn` + `Kernel.DecideReset`
- **H2** — Weekly/monthly DD never enforced → `PreTradeGate` checks enabled by B1 toggles
- **H3** — Worst-case ignored `DailyDdBase` → `PreTradeGate` honors `DailyDdBase`; `RiskGate.cs` deleted
- **H4** — Duplicate C3 in `RiskGate.cs:39` → `RiskGate.cs` deleted
- **H5** — AntiMartingale silent fall-through → `KernelSizing` explicit branch
- **H6** — FixedLots/FixedDollarRisk bypass drawdown scaling → `KernelSizing` applies scale to all methods
- **H7** — Governor `OnDailyReset()` never called → `HandleDayRolled` → `GovernorMachine.ApplyDailyReset` in kernel path
- **M7** — Worst-case projection excludes commission → `PreTradeGate.CandidateWorstCase` includes round-trip commission
- **NEW-3/C14** — SL-distance validation unenforced; `MaxSlPips=0` rejects all → `PreTradeGate`: `<=0` = "no limit"

### Venue bugs fixed (live code, immediately effective)
- **C5** — `AccountUpdate(balance, 0, balance)` at 3 sites → Fixed to `(balance, balance, 0)` in `SimulatedBrokerAdapter`
- **C7** — Limit expiry per tick → Moved to `OnBarObserved` per-bar decrement
- **C8** — SessionBreakout range = full buffer → Filtered to `[RangeStartUtc, RangeEndUtc)` time-of-day window
- **M10** — `ComputeCosts` swallows gross PnL to zero → Catch computes gross PnL from direction/price

### Architecture / debt removed
- **RiskGate.cs** + **RiskGateTests.cs** — Dead code with zero callers in `src/`
- **DailyResetService** — Wall-clock `BackgroundService`; kernel owns sim-time resets via `HandleDayRolled`
- **All UNWIRED comments** — Removed from `EngineReducer`; all 5 branches now wired via `Kernel.Decide`

### New additions
- `ProtectionToggles` — 9 on/off flags (daily, max, weekly, monthly DD, profit target, force-close, news, weekend, governor)
- 3 toggle tests verifying: disabled daily DD → no protection, disabled force-close → no flatten, disabled governor → orders pass
- `DatasetEntity`/`ConfigSetEntity` persistence + `BarTape` + `RunSpec` on `BacktestRun`
- Journal table + `SqliteStepRecordSink` + `KernelJournalController` (paged + NDJSON export)
- `ReplayRunner` + determinism test + 7 scenario invariant tests
- Golden replay oracle with committed `golden-snapshot.json` baseline

### Iter-35 (cont.) — owner-driven A/B finishing (verified green, golden unchanged)
- **C6** — `SimulatedBrokerAdapter.ClosePartialPositionAsync` now realizes the closed portion: costs via `TradeCostCalculator`, `_currentBalance` update, cost-stamped exec, `AccountUpdate` (mirrors the full-close path; both venues now agree)
- **H14** — `BacktestReplayAdapter` close exec now reports `trade.Lots` instead of `0` (stays a full close in the lifecycle FSM; ledger/reconciliation see real volume)
- **C8 (residual)** — `SessionBreakoutStrategy` session range now filters to **today's** date AND the time-of-day window (was time-of-day across the whole buffer → cross-day contamination)
- **A3 lossless journal** — added `JournalLosslessTests`: no-drop-under-burst (500 records into capacity-8 Wait channel all persist) + retry-failed-batch-no-loss (sink throws once → batch retried, `DroppedBatches==0`). Closes the untested A3 guarantee.

### Iter-35 finish — Part A+B completion + Phases 1-4 (verified green, 11/11 smoke)

#### Part A remaining (kernel cutover)
- **AF1** — Determinism: `PositionLifecycle.CreateIntended` no longer calls `Guid.NewGuid()` (uses orderId as positionId); body-scan purity test in `EnginePurityTests`; `DeterminismTests` rewritten with position lifecycle.
- **AF3** — Bar exits: `EngineReducer.DetectSlTpExit` exposed as public static; `SimulateBarExitsAsync` delegates to kernel authority.
- **AF4** — Equity/breach: `Kernel.EvaluateDrawdownBreach` static helper replaces `AccountProcessor` watchdog (toggle-gated, includes weekly/monthly).
- **AF5** — Resets: `ProtectionState.ClearsOn` matrix complete (Never/AccountReset/unknown policies); C4 MaxDD protection exit fixed in production.
- **AF6** — Governor: `GovernorMachine` implements `ITradingGovernor`; `TradingGovernorService.cs` deleted; DI swapped.
- **AF7** — Sizing: `PositionSizer.cs` + `DrawdownScaler.cs` deleted; all callers repointed to `KernelSizing`.
- **AF8** — Journal: H20 flush-loss fix (buffer clear after save); MIN-02 `SingleReader=true` on `BarEvaluationHandler`.
- **AF9** — Replay: `ReplayEffectExecutor` no longer fills at price 0 (uses fallback price 1.0m).
- **AF10** — Hash stability: `DatasetConfigHashTests` (6 tests — ConfigSet determinism, DatasetRef round-trip).
- **AF12** — Architecture: `EnginePurityTests` updated for `ITradingGovernor` DateTime allowance.

#### Part B (venue + cTrader)
- **C1** — cBot `ExecuteSubmitOrder` reads `orderType`/`limitPrice`; "Limit" orders route through `PlaceLimitOrder`.
- **C2** — cBot `cancel_order` handler added (`ExecuteCancelOrder` → `ClosePosition`).
- **M1** — Partial close reads commission/swap AFTER `ClosePosition()` (TradeResult).
- **H15** — `BacktestReplayAdapter` fill timestamp now uses bar close time (`OpenTimeUtc + BarDuration`).
- **H16** — `BacktestReplayAdapter.ComputeFloatingPnL` uses directional bid/ask instead of mid.
- **H11** — `CTraderBrokerAdapter` synthetic close uses `_lastMid` instead of `Price(0m)`.
- **M19** — `BuildLoadedConfigFromDbAsync` bare `catch{}` → logged warning with fallback.

#### Phase 1 — Web run lifecycle
- **C12** — `RunsController.Cancel` cancels only target run via per-run CTS; `StopAllAsync` deprecated.
- **C11** — `RunEngineReplayAsync` uses linked user token + 30-min timeout (no isolated CTS).
- **C13** — `BacktestAnalyticsController` route renamed to `api/backtest/analytics`.
- **H22** — `ResolveEffectiveConfigJsonAsync`/`WriteStartRecordAsync` moved inside try/finally.
- **H24** — `StrategyOverrides` on `StartRunRequest` → `CustomParams` → `EffectiveConfigResolver`.
- **H25** — `BarCount` field + `Interlocked.Increment` (was race-prone property).
- **H27** — `_runs` + `_lastSentTicks` purged on run completion (was memory leak).
- **H26** — Journal `DecisionRecordView` uses sim-time parsed from `state.SimTime`.
- **H10** — Last-bar `AccountStream` drain runs even on cancellation.
- **H23** — Legacy `StartRequest` gains `RiskProfileId` + `Venue` fields.

#### Phase 2 — Data-loss stop
- **C9** — `PipelineEventWriter` logs dropped writes (warning every 1000 drops).
- **C10** — `EquityPersistenceHandler` flush loop groups by RunId (was first-item-only).
- **H18** — `BarEvaluationHandler` logs dropped writes.
- **H19** — `BufferedBarWriter` logs dropped writes.
- **H21** — SQLite WAL mode + busy_timeout set via PRAGMA on startup.
- **M16** — `EquityPersistenceHandler.DisposeAsync` completes channel before canceling.

#### Phase 3+4 — Trade chart + reporting
- **NEW-6** — `Timeframe` field added to `TradeResult` domain model + `TradeSummaryResponse` DTO.
- **M11** — `JournalNormalizer`: `OrderCancelled` maps to `CANCELLED` when reason contains "cancelled".
- **M12** — `JournalNormalizer.CloseReasons`: added `TRAIL`, `BREAKEVEN`, `PARTIAL`.
- **H28** — `ScatterChartComponent` plots both MAE and MFE (two series, not just MFE).
- **H29** — `run-report.component.ts`: cost reconciliation uses `Gross - Comm - Swap - Net` (no per-term `abs`).
- **H30** — Journal filter: dropped invalid `BAR`, added `GOVERNOR`, `ENTRY_EXPIRED`, `CANCELLED`.
- **M20** — `ExportController` queries real trades from `IRunQueryService` (was header-only stub).
- **M21** — `RunSummary` Angular interface gains `grossPnL`, `commissionTotal`, `swapTotal`.
- **H20** — `PipelineEventWriter` + `BarEvaluationHandler` buffer.Clear() moved after successful save.

#### Phase 5 — Live monitor (iter-35 finish)
- **L1** — Equity chart: single `setData`, showBalance reactive, no-op forEach removed.
- **L2** — Journal: seq-based merge, append-only, never replaces array with tail slice.
- **L3** — Breach banner: cleared on completion + mid-run DD recovery.
- **NEW-7** — `setInterval` cleared in `ngOnDestroy`.

#### Phase B-E — Report, Config, Monitor, Venue (iter-35 finish)
- **Template fix** — `toFixed` null-guards on `dp.pnl`, `grossPnLAmount`, `rMultiple`, `commissionAmount`, `swapAmount`, `barHeight` in run-report component (9 guards added).
- **Violations rendering** — `fmtReason()` parses JSON violation arrays into readable names.
- **Strategy detail** — Config view shows formatted human-readable sections instead of raw `JSON.stringify`.
- **Settings page** — Dynamic data fetched from API (strategy count, run count, profile count).
- **Trade-list** — Gross/Comm/Swap cost columns added to trade-list component.
- **Data-table** — `rowClick` output for navigation; run-report trade rows link to `/trades/{id}`.
- **Breach recovery** — Banner clears when daily DD drops below 2% mid-run.
- **EF Core fix** — `BacktestOrchestrator.GetTradeStatsAsync` materializes snapshot query before `Max()`.

#### Audit fixes (iter-35 finish)
- **GovernorOptions** — Web DI reads from `ConfigLoader.LoadBase().Governor` (not default `new GovernorOptions()`). Fixes M18.
- **ProtectionState** — `MonthlyDrawdown` clears on Month boundary only (was `true` = any boundary).
- **ResetPolicy matrix** — Applied to Daily/Weekly/Monthly causes (not just MaxDD). "Never" blocks all auto-clear.
- **AccountProcessor** — Multi-boundary roll fix: checks Day/Week/Month independently (was cascading ternary).
- **M5** — cTrader dedup signature includes `GrossProfit|NetProfit|Commission|Swap` in `CTraderBrokerAdapter.TryWriteExec`.
- **M6** — `PropFirmRuleValidator.IsProfitTargetMet` uses equity (not balance).
- **M9** — `IndicatorSnapshotService.RecomputeIndicatorsAsync` checks `CancellationToken`.
- **M13** — `EntryPlanner.Plan` bounds-checks SL/TP prices (prevents negative/overflow).

#### E2E Infrastructure (iter-35 finish)
- **Playwright** + Chromium headless browser installed.
- **Seed bars** — 2000 EURUSD H1 bars seeded into temp SQLite DB via CSV.
- **Temp DB isolation** — `Persistence__DbPath` env var for isolated E2E test runs.
- **E2E specs** — `web-ui/tests/e2e/ui-smoke.spec.ts` — 13 tests covering all key pages.
- **`npm run e2e`** script in `web-ui/package.json` (CI-ready).
- **`shamshir-ui` skill** — thin orchestrator: build, launch, seed bars, run backtest, run E2E, teardown.
- **`shamshir-e2e` skill** — cTrader E2E harness, diff, logging chain documentation.
- **`shamshir-kernel` skill** — Kernel architecture, determinism rules, cutover patterns.
- **Verified**: 13/13 E2E pass with real trade data (16 trades, 6403 PnL, 6953 Gross, 615 Comm, -65 Swap).
- **H20** — `PipelineEventWriter` + `BarEvaluationHandler` buffer.Clear() moved after successful save.

---

## Iteration 36 � The Kernel Cutover (one engine, one journal, real replay/duplicate)

**K0-K3** (gates, evaluator, venue-feedback bridge, kernel backtest loop) � DELIVERED; golden reproduced bit-identically, no re-baseline.

**K4 � full flip + correctness gaps:**
- Production runs ONLY the kernel (`KernelBacktestLoop.RunFromBrokerAsync`) for live + backtest; imperative loop deleted.
- gap-1: `OrderProposed` carries the resolved per-strategy `RiskProfile`; `Kernel.DecideProposed` sizes with it (multi-profile runs no longer mis-size).
- gap-3: trailing/breakeven runs in the kernel loop � new `StopLossModifyRequested` event ? reducer updates the authoritative stop + emits `ModifyStopLoss` (TP preserved); `KernelTrailingEvaluator` reuses the real `PositionManager`.
- gap-4: per-bar `AccountSnapshot` written from the authoritative `EngineState` (Monitor no longer blank under the kernel).
- **Twins out of `src` (literal K4 gate):** `OrderDispatcher`/`KernelOrderGate`/`AccountProcessor` relocated to `tests/TradingEngine.Tests.Support` (golden oracle, D81); `grep "AccountProcessor|KernelOrderGate|OrderDispatcher|RunBacktestLoopAsync(|SimulateBarExitsAsync(" src ? 0`.

**K5 � one journal:** `GET /api/runs/{id}/journal` serves the lossless StepRecord stream (SQL-paged) + `/journal/export` NDJSON; `KernelJournalController` consolidated away. `EffectExecutor` off `IDecisionJournal`. **`PipelineEventWriter` + `BarEvaluationHandler` deleted** (the two `DropOldest` lossy writers, D83); legacy `IDecisionJournal`/`IPipelineJournal` consumers bind to `NullDecisionJournal`/`NullPipelineJournal`. Fixed a latent bug: `IJournalQueryRepository` was missing from the Web root DI.

**K6 � real replay + duplicate:** `POST /api/runs/{id}/duplicate` (same dataset, new config, `ParentRunId` lineage); run identity persisted (`DatasetId`=hash(data-window spec), `ConfigSetId`=hash(effective config), `Seed`); EF regen-init for `ParentRunId` (D84).

**K7 � reconciliation:** C3/C4/H1/H2/H5/H6/M7 shadowed `RiskManager` bugs confirmed **production-dead** (no caller in `src`; oracle-only). OPEN-ISSUES + this file + SYSTEM-REFERENCE updated.

Verified: build 0 errors � Unit 208/4-skip � Simulation non-cTrader 82/2 (2 pre-existing: EntryPlanner-harness DI gap + NetMQ transport) � in-host replay writes StepRecord journal � `run-shamshir` driver 11/11 � `npm run build` green.
