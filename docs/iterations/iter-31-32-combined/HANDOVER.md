# Iter-31+32 Combined — Handover

**Branch:** `iter/31-costs-journal` (off `develop`, commit `9a76064`)
**Base:** `develop` @ `abcbc07`
**Date:** 2026-06-17

---

## What these iterations are about

Combined execution of two iterations that make backtests trustworthy and configs editable:

- **iter-31** — Honest Costs + Inspectable Journal + Order Entry (limit orders)
- **iter-32** — Strategy & Config as Editable Data (DB-seeded, per-run overrides, browse/tweak UI)

Full plan: `docs/iterations/iter-31-32-combined/MASTER_PLAN.md`

---

## Architecture decisions (all confirmed by owner)

| Decision | Choice |
|----------|--------|
| Symbol/timeframe model | Option A — strategy keeps defaults, overridable per run via run plan |
| Config source of truth | DB canonical (JSON = seed + export only) |
| Override granularity | Deep-merge (override only fields you set) |
| Migrations | Redo `InitialCreate` at end (not yet done — new migration was added) |
| Limit entry defaults | All `Market`, only `mean-reversion` on `LimitOffset` as demo |
| Limit expiry | Cancel + journal `ENTRY_EXPIRED` (no market fallback) |
| Cost estimates | FX majors ≈3.5 commission/side, small ±swap, marked as estimates |

---

## What shipped (engine core — 15 phases)

### Costs + Money model (31-A0, A1)
- `SymbolInfo` has 4 new fields: `CommissionPerLotPerSide`, `SwapLongPerLotPerNight`, `SwapShortPerLotPerNight`, `TripleSwapWeekday` (all default 0)
- `symbols.json` — all 16 symbols seeded with realistic cost estimates
- `SymbolCatalog` parses the new fields (nullable in JSON → default 0)
- `SimulatedBrokerAdapter` — close paths compute commission (round-turn × lots), swap (nightsHeld × rate × lots), stamp `GrossProfit/Commission/Swap/NetProfit` on `ExecutionEvent`, increment balance by **net**
- `CountNightsHeld()` — counts daily rollover boundaries crossed, triple-swap day ×3

### Journal taxonomy (31-B0, B1)
- `JournalEventKind` enum: `SIGNAL, ORDER, FILL, CLOSE, REJECTED, BREACH, GOVERNOR, ENTRY_EXPIRED, CANCELLED`
- `JournalNormalizer` — maps both live vocab (`SIGNAL/ORDER/EXEC/CLOSE`) and persisted vocab (`OrderSubmitted/OrderFilled/BreachDetected`) onto the normalized taxonomy
- `PipelineEvent.NormalizedKind` — new column on entity, populated by `PipelineEventWriter.Record()`
- `GET /api/backtest/{runId}/journal?kind=&afterSeq=&limit=50` — paged journal API in `BacktestController`
- Journal viewer tab on Report page with kind filter badges and client-side paging

### Order entry (31-C0, C1)
- `EntryPlanner` — single place that reads `OrderEntryOptions`, rewrites Market→Limit with computed limit price, re-derives SL/TP so R stays unchanged
- Called in `TradingLoop.ProcessBarAsync` right after `strategy.Evaluate()`
- `SimulatedBrokerAdapter` — limit orders rest until price reached (buy: `Ask ≤ limit`, sell: `Bid ≥ limit`), fill at limit price (no slippage), expire after `LimitOrderExpiryBars` with cancellation event
- `PendingOrder` class extended with `OrderType`, `LimitPrice`, `ExpiryBarCount`
- `OrderDispatcher` and `TradingLoop` now pass `intent.OrderType` instead of hardcoded `OrderType.Market`

### Config store (32-P0, P1)
- `StrategyConfigEntity` — EF entity: Id, DisplayName, Enabled, DefaultSymbols, Timeframe, RiskProfileId, ParametersJson, PositionManagementJson, OrderEntryJson, RegimeFilterJson, ReentryJson
- `IStrategyConfigStore` — `GetAllAsync()` / `UpsertAsync()`
- `SqliteStrategyConfigStore` — maps between `StrategyConfigEntry` ↔ `StrategyConfigEntity`
- `StrategyConfigSeeder` — reads `config/strategies/*.json`, seeds DB if empty (idempotent), called at startup from `Program.cs`
- `ConfigLoader` refactored — `LoadBase()` loads non-strategy config, `Load()` sets `StrategyConfigs` from DB store
- `JsonExportService` — serializes store back to JSON for git (not wired to endpoint yet)

### Run plan + overrides (32-P2, P3)
- `RunPlan` record — `(strategyId, symbol, timeframe)[]` — and `RunPlanEntry`
- `StrategyBankService.GetActive` — if `RunPlan` provided, filters by `(strategyId, symbol, timeframe)` in plan; falls back to strategy's stored defaults
- `EffectiveConfigResolver` — deep-merges: stored default ← per-run overrides ← run plan. Pure, unit-tested (7 tests in `EffectiveConfigResolverTests.cs`)
- `EffectiveConfigEntry` record + `StrategyOverride` record
- `BacktestRunEntity.EffectiveConfigJson` — nullable column for persisting resolved config with run

### Equity + stats (31-B3, B4)
- `EquityPersistenceHandler` — persists `AccountSnapshot`s with RunId
- `SqliteEquityRepository.GetByRunIdAsync()` — retrieves snapshots post-run
- `ReconciliationAssert` in test project — asserts `NetPnL(stats) == Σ trade net == equityCurve.end`
- `Report.cshtml.cs` — uses persisted equity curve when available, falls back to trade-walk

---

## What's NOT done — to carry forward

### Engine / live path (3 phases)

| Phase | What to do | Key files |
|-------|-----------|-----------|
| **31-A2** | cBot emits `commission = pos.Commissions`, `swap = pos.Swap` in close EXEC frame. `CTraderBrokerAdapter` maps them onto `ExecutionEvent.Commission/Swap`. | `TradingEngineCBot.cs:561-562`, `CTraderBrokerAdapter.cs:371-374` |
| **31-A3** | Report trades table shows Commission/Swap/Gross/Net columns. Run totals show cost-inclusive KPIs. Delete dead `equityDefinition` string from `PropFirmRuleSet`. | `Report.cshtml/cs`, `PropFirmRuleSet.cs:11` |
| **31-C2** | Live limit path — `CTraderBrokerAdapter.cs:404-413` already branches on `Method==LimitOffset`; now that EntryPlanner populates `LimitPrice`, verify the branch works end-to-end. Adapter test: `LimitOffset` intent → non-zero `limitPrice` in order frame. | `CTraderBrokerAdapter.cs`, adapter tests |

### Web UI (3 phases)

| Phase | What to do | Key files |
|-------|-----------|-----------|
| **32-P4** | Strategy browse/edit UI — replace empty `Strategies.cshtml.cs` with a list + detail/edit form exposing ALL tunables: parameters, SL/TP, breakeven/trailing, regime filter, order entry, symbols, timeframe, risk profile, enabled. Validate via cross-reference checks before `Upsert`. | `Strategies.cshtml/cs`, `IStrategyConfigStore` |
| **32-P5** | New-Backtest per-run override UI — run plan picker (symbols × timeframes), knob tweaks (parameters, SL/TP, entry method), effective config preview. Feed into `EffectiveConfigResolver`. | `New.cshtml/cs` |
| **31-B2** | Monitor fixes — replace 30-item in-memory `RecentJournal` queue with polling the B0 journal API (lossless). Remove `equityPoints.length <= 500` freeze in `Monitor.cshtml:170`. | `Monitor.cshtml`, `BacktestOrchestrator.cs:152-172` |

### Polish (2 phases + chores)

| Item | What to do |
|------|-----------|
| **31-C3** | Set `mean-reversion.json` `orderEntry.method` → `"LimitOffset"` with `limitOffsetPips: 5` as worked example |
| **32-P6** | Wire `JsonExportService` to an endpoint/action. Export round-trips DB→JSON. Regenerate `InitialCreate` migration. |
| **31-A4** | (Optional) Commission-aware risk budget — subtract round-turn commission from risk budget, gated behind config flag |
| **OPEN-ISSUES.md** | Mark C1–C6, J1–J6, E1–E4 as `✅ Fixed (Iteration 31)` |
| **HANDOVER.md** | This file |

---

## Test status

| Suite | Result |
|-------|--------|
| Unit | **188 passed**, 4 skipped |
| Architecture | **3 passed** |
| FtmoGoldenJourney | **4 passed** |
| TradingLoopDirect | **1 passed** |
| DecisionJournal | **2 passed** |

Suites green. No regressions from baseline.

---

## Key files for context

| File | Why it matters |
|------|---------------|
| `SimulatedBrokerAdapter.cs` | Cost computation + limit order semantics live here. Close paths use `ComputeCosts()`, `CountNightsHeld()`. Limit orders use `ExpiryBarCount` + price-reached check. |
| `TradingLoop.cs` | `EntryPlanner.Plan()` called after `strategy.Evaluate()`. EntryPlanner passed via constructor. |
| `EntryPlanner.cs` | `Plan(intent, OrderEntryOptions, signalPrice)` → rewrites OrderType/LimitPrice/SL/TP. |
| `EffectiveConfigResolver.cs` | Deep-merge stored default ← per-run overrides ← run plan. |
| `StrategyConfigSeeder.cs` | `SeedAsync()` — reads JSON, upserts to store if empty. Called from `Program.cs`. |
| `StrategyBankService.cs` | `GetActive()` now accepts optional `RunPlan` for symbol/TF override. |
| `ConfigLoader.cs` | Split into `LoadBase()` (non-strategy) + `Load()` (sets StrategyConfigs). |
| `EngineServiceCollectionExtensions.cs` | `EntryPlanner`, `IStrategyConfigStore` registered. `AddPersistence` no longer calls `BuildServiceProvider` mid-registration. |
| `Report.cshtml/cs` | Journal viewer tab with kind filters added. |
| `BacktestController.cs` | Journal API endpoint with `kind`/`afterSeq`/`limit` params. |
| `JournalNormalizer.cs` | Maps event vocabularies to `JournalEventKind`. |
| `symbols.json` | All 16 symbols have cost fields. |
| `MASTER_PLAN.md` | Full combined plan document. |

---

## Guardrails (do not break)

- Don't touch `aspire/AppHost` (`NU1903`)
- Don't change strategy math or risk pipeline
- `playbook.json`/`position-management.json` are dead — note, don't wire or delete
- `AccountProcessor.cs:125` NRE is pre-existing, out of scope
- Keep Unit + Simulation (FtmoGolden) green — stop-the-line on red
- `dotnet test tests/TradingEngine.Tests.Unit` for fast feedback
