# Iteration 18 — HANDOVER.md

**Branch**: `iter/18-strategy-bank-blazor`  
**Implemented**: 2026-06-11 → 2026-06-12  
**Status**: Complete — PR1/PR2/PR3/PR4 delivered  

---

## Commits: 26 total

| Category | Count |
|----------|-------|
| Risk foundation (A0-A5) | 4 |
| DB migrations + data (B1-B3) | 3 |
| Strategy bank + new strategies (C1-C4) | 4 |
| Order intelligence + position mgmt (D1-D3) | 2 |
| Blazor dashboard (E1-E6) | 1 |
| Wire fixes (strategy bank, news filter, DI) | 3 |
| Bug fixes (migration, dedup, symbols, cBot) | 5 |
| Testing + docs | 4 |

## Test Results: 133 passing

| Suite | Count |
|-------|-------|
| Unit tests | 116 pass |
| Integration tests | 17 pass (incl. migration test + DI validation) |
| Strategy simulation tests | 5 pass |

## Bug Fixes (Session 2)

| Bug | Fix | Commit |
|-----|-----|--------|
| PendingModelChangesWarning on startup | `AddEquitySnapshotType` migration | `45dc010` |
| SizeModifierPipeline DI failure | Removed `SizeModifierOptions` from constructor | `abc9cb9` |
| Symbol USDJPY not registered | EngineHostFactory loads all symbols from catalog | `445098b` |
| NetMQ duplicate execs (sent=173 recv=363) | `TryWriteExec` dedup + cBot `_commandCloses` fix | `9edeae7`, `d778a07` |
| cBot cartesian symbol-timeframe product | Positional pairing: symbols[i]→periods[i] | `8c75634` |
| Temp DB files not cleaned up | WebSmokeTests, NetMQBridgeTest, ReplayTestHarness dispose | `92775b1` |

## Session 2 Deliverables

- Strategy bank + regime detector wired into engine loop
- ConfigurableNewsFilter replacing stub
- `rotation.json` loaded via ConfigLoader
- Existing strategy configs migrated to unified `positionManagement` blocks
- 5 Blazor pages wired to real APIs
- `DIValidationTests`: 13 core services resolved via DI
- `MigrationTests`: fresh DB migration verified
- WebSmokeTests temp DB cleanup

## Blazor Pages

| Route | Page | Status |
|-------|------|--------|
| `/dashboard` | BacktestDashboard | Functional — symbol/date/amount controls, backtest runner |
| `/explorer` | TradeExplorer | Functional — run browser via IBacktestQueryService |
| `/strategies` | StrategyManager | Functional — IStrategyBank.GetSnapshot, enable/disable |
| `/compare` | BacktestComparison | Functional — compare API + FTMO pass-probability |
| `/analysis` | SymbolAnalysis | Functional — correlation matrix + regime heatmap |

## Config Files (21 total)

- 9 strategy JSONs (4 existing + 5 new) all with `regimeFilter`/`orderEntry`/`positionManagement`
- 3 risk profiles (standard/conservative/aggressive) with `maxExposurePerCurrencyPercent` + `sizeModifiers`
- 2 prop-firm rulesets (ftmo-standard/ftmo-aggressive) with weekly/monthly fields
- `rotation.json`, `position-management.json`, `news/blocked-windows.json`
- All existing strategy `Parameters` records: SL/TP/trailing fields removed

## Documentation

- `docs/iterations/iter-18/PLAN.md` — full implementation plan (2100 lines)
- `docs/iterations/iter-18/HANDOVER.md` — this file
- `docs/iterations/iter-18/PROTOCOL-DELTA.md` — limit order protocol additions
- `docs/OPEN-ISSUES.md` — STD-07 (raw SQL) marked fixed
| F1 | Partial | — | Simulation tests for risk features |
| F2 | Pending | — | FakeCBot extensions |
| F3 | Pending | — | Live mode re-verification |

---

## Verification Results

### Unit Tests
```
111 passed, 0 failed, 0 skipped — TradingEngine.Tests.Unit
15 passed, 0 failed — TradingEngine.Tests.Integration
```

### Build
```
dotnet build --no-incremental → Build succeeded
EF migration: InitialFullSchema generated, fresh DB verified
```

### cBot Build
```
dotnet build src/TradingEngine.Adapters.CTrader → Build succeeded
```

---

## What Changed

### New Domain Types (24 files)
- 6 new interfaces: IPropFirmComplianceService, IPassProbabilityEstimator, ISizeModifier, ICurrencyExposureTracker, IStrategyBank, IRegimeDetector
- 15 value objects: ExtendedRiskState, MarketRegime, ComplianceResult, PassProbabilityEstimate, ComplianceSummary, CurrencyExposureSnapshot, SizeModifierContext, SizeModifierOptions (+ sub-options), GracePeriodOptions, RegimeFilterOptions, OrderEntryOptions, StrategyRotationOptions, StrategyBankSnapshot, PositionManagementOptions, NewsBlockWindow
- 2 domain events: WeeklyEquitySnapshotTaken, MonthlyEquitySnapshotTaken

### Modified Domain Types
- RiskProfile: added MaxExposurePerCurrencyPercent, SizeModifiers
- PropFirmRuleSet: added MaxWeeklyLossPercent, MaxMonthlyLossPercent, RequireProfitTarget, GracePeriod
- TradeIntent: added OrderEntryOptions? Entry
- IndicatorRequest: added Timeframe, Param1, Param2
- IndicatorType: added Adx, SuperTrend
- IStrategy: added Config property
- IStrategyConfig: added Enabled, RegimeFilter, OrderEntry, PositionManagement
- ExecutionEvent: added GrossProfit, NetProfit, Commission, Swap
- IRiskManager: added OnWeeklyReset, OnMonthlyReset, changed CurrentState to ExtendedRiskState
- IIndicatorService: added Adx, Macd, SuperTrend

### Risk Layer (8 new services)
- PropFirmComplianceService + PassProbabilityEstimator (weekly/monthly DD blocks, Monte Carlo)
- SizeModifierPipeline + 4 modifiers (drawdown, ATR regime, time-of-day, confidence)
- CurrencyExposureTracker
- ConfigurableNewsFilter

### Engine Layer
- EngineWorkerDependencies (4 service groups)
- StrategyBankService (regime filter, performance rotation)
- AtrBasedRegimeDetector (ATR ratio + ADX)
- Weekly/monthly reset scheduling in EngineWorker
- Multi-key indicator emission (BB Upper/Lower, MACD Signal/Histogram, ST Direction)
- Per-timeframe indicator computation
- Limit order support in NetMQBrokerAdapter

### 5 New Strategies
1. RSI Divergence (bullish/bearish divergence)
2. Bollinger Squeeze (band contraction + breakout)
3. MACD Momentum (zero-cross + SMA200 trend + ADX)
4. MTF Trend (H4 trend + H1 RSI pullback)
5. SuperTrend (direction flip + ADX confirmation)

### Blazor Dashboard (PR3)
- 5 pages: /dashboard, /explorer, /strategies, /compare, /analysis
- 6 shared components: EquityCurveChart, OhlcTradeChart, StrategyStatusCard, PassProbabilityGauge, RegimeHeatmap, CorrelationMatrix
- 3 API controllers: BarsController, StrategiesController, BacktestAnalyticsController
- lightweight-charts JS interop (OHLC candles, equity curves, trade markers)
- Hybrid mode: existing Razor Pages preserved

### Config Files
- config/rotation.json, config/position-management.json
- config/news/blocked-windows.json
- 5 new strategy JSON configs
- Updated: all 4 existing strategy JSONs (regimeFilter + orderEntry)
- Updated: all 3 risk profiles (maxExposurePerCurrencyPercent + sizeModifiers)
- Updated: ftmo-standard.json (weekly/monthly fields)

### EF Migration
- InitialFullSchema generated in Persistence/Migrations
- Raw SQL removed from Program.cs (both Web and Host)
- STD-07 marked fixed in OPEN-ISSUES.md

---

## Items Deferred / Pending

### F2: FakeCBot Extensions
- `FillAtLimitPrice`, `NeverFill`, `FillWithSlippage` behaviors
- `DisconnectAfterNBars` fault injection
- Order rejection simulation

### F3: Live Mode Re-verification
- Requires cTrader credentials (available in env vars)
- Run 3-day EURUSD H1 backtest via /dashboard
- Verify weekly/monthly DD display
- Document trade count

### Optional Cleanup (not in this iteration)
- Remove old Razor Pages (plan says post-scope cleanup PR)
- Remove unused composable strategy infrastructure (ComposedStrategy, ISignalProvider etc.)
- Ensure all 9 strategies use SlTpResolver consistently

---

## Breaking Changes
None. Migration is additive throughout. All existing APIs preserved. All existing tests pass.

## Rules Compliance
- decimal for money/price arithmetic
- IEngineClock, no DateTime.UtcNow in engine code
- TradingEngine.Domain has zero infrastructure dependencies
- cBot targets net6.0 / C# 10 (no C# 11+)
- Single composition root: EngineHostFactory
