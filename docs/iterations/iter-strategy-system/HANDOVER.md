# Iter Strategy System Overhaul — HANDOVER

> **⟶ iter-strategy-system P0–P4 are COMPLETE & gated.** The live, authoritative record is
> **`PROGRESS.md`** in this folder (status board, per-phase notes, commits, and the one owner-only
> verification step). The `iter/38-addons` work described below was **merged forward** in P0 (`5c0e024`)
> and is now the baseline the P0–P4 commits build on:
> P0 `cc24c22` · P1 `c492d16` · P2 `e4b91f4` · P3 `60adc35` · P4 `acec961`.
> Final gate: build 0err · Unit 272 · Arch 8 · Integration 73 · Golden 56/0 · SPA 0err. Runtime smoke
> test confirmed the launched binary serves the SPA, discovers all 9 strategies, and round-trips a
> row-based run's metadata.

---

**Branch:** `iter/38-addons` (committed as `1dcd11e`, pushed)
**From:** Baseline `58b2745` (iter-38/39 complete state)
**This branch:** `iter/strategy-system` (clean baseline for next iteration)

---

## What was delivered (on `iter/38-addons`)

### 1. Strategy agnosticism

Strategies no longer carry `Symbols`/`Timeframe` in config. They are pure logic — symbols
and timeframes assigned at backtest time via RunPlan.

- `StrategyConfigEntry` — removed `Symbols` (IReadOnlyList<string>), `Timeframe` (string)
- `IStrategyConfig` — removed `Symbols`, `Timeframe` properties
- `IStrategy` — added `EntryTimeframe` property (hardcoded per strategy, e.g. H1)
- All 9 strategy config classes + 9 strategy classes updated
- RsiDivergence `_symbols`/`SymbolEntry` removed; uses `_symbolRegistry.Get()` at evaluate time
- Multi-TF strategies (MtfTrend): `EntryTimeframe` = H1, `RequiredTimeframes` = [H1, H4]
- All strategies hardcode their timeframe — no more `_timeframe = config.Timeframe`
- 9 `config/strategies/*.json` files stripped of `symbols` and `timeframe` fields
- `EffectiveConfigEntry` — removed `Symbols`, `Timeframe`. `SymbolTimeframePair` deleted.
- `EffectiveConfigResolver.Resolve()` — removed symbol/timeframe merge, signature now 2 args

### 2. StrategyRegistry — reflection-based factories

Hardcoded factory lambdas for each strategy ID replaced with auto-discovery:

- Each strategy class has `public static T Create(StrategyConfigEntry, IServiceProvider)`
- `StrategyRegistry.DiscoverFactories()` finds them via reflection
- `StrategyFactoryHelper.DeserializeParams<T>()` shared deserialization utility
- Dead `Activator.CreateInstance` fallback removed (throws clear error instead)
- Imports for TradingEngine.Strategies.* namespaces removed from registry

### 3. RunPlan — routing backbone

Previously dead code. Now constructed from user selections and passed to engine.

- `BacktestOrchestrator.BuildRunPlan()` — builds `(strategy, symbol, timeframe)` tuples
  from selected strategies × symbols × periods
- `EngineHostOptions.RunPlan` set for both replay and NetMQ paths
- `StrategyBankService.IsInRunPlan()` — `return true` when null (legacy path),
  `return false` when entries array present but empty
- `StrategyBankService.GetActive()` — filters by `EntryTimeframe == barTimeframe`
  instead of `RequiredTimeframes.Contains(timeframe)`
- Multi-combination sequential passes: `RunEngineReplayAsync` groups RunPlan entries
  by unique `(symbol, timeframe)`, runs each as independent EngineHost pass.
  Progress accumulates across passes. Per-pass equity CTS cancels before host disposal.
  `state.BarsTotal` updated incrementally for correct progress %.

### 4. StartRunRequest — singular fields removed

- `Symbol`, `Period` removed from DTO. Only `Symbols[]`, `Periods[]` arrays.
- `RunsController.Start()` derives primary symbol/period from `validSymbols[0]`/`validPeriods[0]`
- `BacktestConfig.Symbol`/`Period` kept (still needed by cTrader CLI args)
- `RunsService.startRun()` in SPA — no longer sends `symbol`/`period` singletons

### 5. SPA changes

**new-backtest.component.ts:**
- Removed `symbol`/`period` singletons from POST body
- Sends `symbols[]`/`periods[]` arrays only

**strategy-detail.component.ts:**
- Removed timeframe/symbols from read-only display
- Removed symbols chip selector + timeframe dropdown from create form
- Removed `toggleCreateSym()` method
- `createForm` no longer carries `symbols`/`timeframe`
- `configPreview` no longer includes `timeframe`/`symbols`
- Removed timeframe/symbols from edit form config section

**strategies.service.ts:**
- `create()` signature — removed `symbols`/`timeframe` params

**run-report.component.ts:**
- `addonDetail()` — parses ADDON_RESOLVED eventJson, renders human-readable summary
  (Trail=AtrMultiple@2.5×ATR, BE@1R+1pip, Partial@1R×0.5, Ride ADX>25→×3.08, DynamicSl=2×ATR TP=2R)
- `partialDetail()` — shows close lots from PARTIAL eventJson
- Template updated with emerald (ADDON_RESOLVED) and amber (PARTIAL) detail spans

**api.types.ts:**
- `StartRunRequest` — removed `symbol`, `period`
- `StrategyDetail` — removed `timeframe`, `symbols`

### 6. Legacy code removal

- `EngineServiceCollectionExtensions` — deleted non-options overloads:
  `AddMarketData`, `AddRisk`, `AddStrategies`, `AddEventInfrastructure`, `AddEngineWorker`
- `Program.cs` — refactored to use `AddEngineHost(options)` with `BrokerAdapterFactory`
- `BrokerAdapterFactory` — Live/Paper→NetMQ, Backtest→Simulated adapter factory
- `BacktestController` — deleted (route `/api/backtest/start`, SPA never used it)

### 7. Add-on cross-validation

`AddOnPacksController.ValidatePack()` — new cross-add-on conflict checks:
- Ride requires Trailing enabled
- Ride requires ATR-based trailing (AtrMultiple or Structure), not StepPips/SteppedR
- ActivateAfterBreakeven requires Breakeven enabled

`PositionManagementOptions.TrailingOptions.ActivateAfterBreakeven` default changed
from `true` to `false` (was a footgun — every strategy enabling Trailing got this
unintentional dependency on Breakeven).

### 8. Config quality

- `StrategyConfigEntity.Version` int column, auto-incremented on each `UpsertAsync`
- `TradingDbContext` updated with Version column mapping
- EF migration reset: deleted old `InitialCreate` + snapshot, regenerated fresh

### 9. Task.Delay cleanup

- Removed 500ms settle in multi-pass loop (FlushAsync drains deterministically)
- Removed 1s settle in `FlushRunPersistenceAsync` (explicit FlushAsync sufficient)
- cTrader 3s blind delay replaced with `await adapter.BarStream.Completion`

### 10. Critical bug fix: Progress reporting (iter-36 regression)

`EngineServiceCollectionExtensions.AddEventInfrastructureFromOptions`:
```csharp
// BEFORE (broken since iter-36):
if (options.Progress is not null)
    services.AddSingleton(options.Progress);           // register real callback
services.AddSingleton<IProgress<BacktestProgressEvent>>(...);  // ALWAYS overwrites

// AFTER (fixed):
if (options.Progress is not null)
    services.AddSingleton(options.Progress);           // register real callback
else
    services.AddSingleton<IProgress<BacktestProgressEvent>>(...);  // only as fallback
```

ASP.NET Core DI uses last-registration-wins. The second `AddSingleton` always
overwrote the real progress callback with a no-op. Every backtest since iter-36
had 0% progress, no live monitor counters, no SignalR updates. Missing `else`.

---

## Gate (as of `1dcd11e`)

```
dotnet build              0 errors
SPA build (npm)            0 errors
Unit tests               272 pass / 6 skip
Architecture tests         5 pass
Integration tests         68 pass / 0 skip
Simulation (golden)       61 pass / 0 skip
Driver smoke test         11/11 pass
```

---

## What's NOT done (carry-forward)

1. **SPA new-backtest combination preview table** — the backend now supports multi-pass
   but the SPA doesn't show the `strategy × symbol × timeframe` combinations before start.

2. **Strategy detail "Quick Test" button** — parameter schema API + inline backtest from
   the strategy page not yet built.

3. **cTrader NetMQ path single-pass** — `RunEngineNetMqAsync` still runs only the first
   `(symbol, timeframe)` combo. Multi-pass for cTrader requires multiple CLI invocations.

4. **ADDON_RESOLVED journal in cTrader path** — the journal rendering works in SPA but
   the cTrader path may emit different eventJson shapes for add-on resolution.

5. **Parameter schema API** — `GET /api/strategies/{id}/schema` returning typed field
   descriptions for SPA auto-generated form controls.

6. **Live Monitor API polling (31-B2)** — the monitor currently uses only SignalR push;
   a lossless journal API polling fallback for reconnection wasn't built.

7. **Per-symbol bar count estimation** — the new-backtest form doesn't preview available
   bar counts for selected symbols/timeframes before starting.

8. **Full wwwroot cleanup** — old hashed SPA chunks from previous builds are deleted but
   there may be more accumulated over time. The `npm run build` output should be gitignored.

---

## Key files to understand the new architecture

| File | What |
|------|------|
| `src/TradingEngine.Domain/LoadedConfig.cs` | `StrategyConfigEntry` — no more Symbols/Timeframe |
| `src/TradingEngine.Domain/Interfaces/IStrategy.cs` | Added `EntryTimeframe` |
| `src/TradingEngine.Domain/Strategy/IStrategyConfig.cs` | Removed Symbols, Timeframe |
| `src/TradingEngine.Domain/RunPlan.cs` | `(StrategyId, Symbol, Timeframe)` routing tuples |
| `src/TradingEngine.Host/StrategyRegistry.cs` | Reflection-based factory discovery |
| `src/TradingEngine.Host/StrategyBankService.cs` | EntryTimeframe filter, RunPlan filter |
| `src/TradingEngine.Host/EngineServiceCollectionExtensions.cs` | Deleted non-options methods, progress bug fixed |
| `src/TradingEngine.Host/BrokerAdapterFactory.cs` | New — Live/Paper/Backtest adapter factory |
| `src/TradingEngine.Strategies/StrategyFactoryHelper.cs` | Shared param deserializer |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Multi-pass replay, RunPlan construction |
| `src/TradingEngine.Web/Api/AddOnPacksController.cs` | Cross-add-on validation rules |
| `src/TradingEngine.Web/Dtos/Runs/StartRunRequest.cs` | Removed Symbol, Period singletons |
| `src/TradingEngine.Domain/PositionManagement/PositionManagementOptions.cs` | ActivateAfterBreakeven default false |
| `src/TradingEngine.Infrastructure/Persistence/Entities/StrategyConfigEntity.cs` | Removed DefaultSymbols/Timeframe, added Version |
| All 9 `config/strategies/*.json` | Stripped of symbols/timeframe |
| `web-ui/src/app/features/runs/new-backtest/new-backtest.component.ts` | Array-only payload |
| `web-ui/src/app/features/strategies/strategy-detail/strategy-detail.component.ts` | No symbols/tf in UI |
| `web-ui/src/app/features/runs/run-report/run-report.component.ts` | ADDON_RESOLVED + PARTIAL detail |
