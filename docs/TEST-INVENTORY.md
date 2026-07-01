# Shamshir — Test Inventory

**Updated:** 2026-06-23 (iter-39)
**Totals:** ~455 test methods across 4 projects, 120+ test classes

| Project | Test Classes | Test Methods |
|---------|-------------|--------------|
| `TradingEngine.Tests.Unit` | ~58 | 275 |
| `TradingEngine.Tests.Simulation` | ~40 | 116 |
| `TradingEngine.Tests.Architecture` | 2 | 5 |
| `TradingEngine.Tests.Integration` | 18 | 59 |

---

## Top 15 Rules Tested

| Rank | Rule | Tests |
|------|------|-------|
| 1 | Daily drawdown halts trading, resets next day | FtmoPressure, RulePressure, Scenario, DrawdownScenarios, KernelResetMultiDay |
| 2 | Max drawdown breach is terminal | FtmoPressure, RulePressure, Scenario, FtmoGoldenJourney |
| 3 | Kernel determinism (same seed = identical output) | Determinism, GoldenReplay, all KernelAcceptance |
| 4 | Position lifecycle state machine | PositionLifecycle (20 tests) |
| 5 | Commission round-turn = per-side × 2 | TradeCostCalculator, BacktestReplayCostsAndLimits |
| 6 | Swap night count including Wednesday triple | TradeCostCalculator, RulePressure |
| 7 | Day roll re-bases DD to current equity | ResetReducer, ResetClock, KernelResetMultiDay |
| 8 | Journal lossless (Wait mode, never DropOldest) | JournalLossless |
| 9 | Journal Seq gap-free | JournalSourceOfTruth |
| 10 | Venue routing: default → credential-free replay | VenueRouting |
| 11 | Governor profit-lock + streak cooling-off | TradingGovernorService, Governor |
| 12 | Regime filter DetectionEnabled=false → pass-through | RegimeToggle |
| 13 | Engine purity (no ILogger/EF/DateTime in Engine assembly) | EnginePurity |
| 14 | Add-on resolve-at-entry: auto/custom/off | AddOnResolver |
| 15 | EffectiveConfigResolver deep-merge | EffectiveConfigResolver (7 tests) |

## Critical Rules NOT Yet Tested

| # | Gap | Priority |
|---|-----|----------|
| G1 | Wednesday triple-swap charged at 3x | DONE (iter-39) |
| G2 | Live limit order path (cTrader) | HIGH |
| G3 | cBot cost itemization in cTrader path | HIGH |
| G4 | Lossless live journal (31-B2) | HIGH |
| G5 | Weekly/Monthly drawdown breach | MEDIUM |
| G6 | ProfitTarget breach phase-complete | MEDIUM |
| G7 | Multi-strategy on same symbol | MEDIUM |
| G8 | ForceCloseOnBreach behaviour | MEDIUM |
| G9 | Kernel restart from snapshot | MEDIUM |
| G10 | Governor disabled path | LOW |
| G11 | Config schema migration | LOW |
| G12 | Non-TrendBreakout strategy unit tests | LOW |
| G13 | API authentication | LOW |

## Coverage by Area

| Area | Classes | Tests | Key Gap |
|------|---------|-------|---------|
| Risk & Drawdown | 15 | ~75 | Weekly/monthly DD, ProfitTarget |
| Protection & Breach | 7 | ~22 | ForceCloseOnBreach, multi-symbol |
| Governor | 2 | ~13 | Disabled path |
| Journal & Events | 11 | ~30 | Cross-run isolation |
| Trades & Costs | 5 | ~18 | Covered |
| Venue | 10 | ~37 | Live limit, cBot costs |
| Engine/Kernel | 18 | ~75 | Concurrency, restart |
| Position Management | 12 | ~57 | Multi-position interaction |
| Strategy & Config | 13 | ~47 | Non-TrendBreakout, RunPlan |
| Web & API | 8 | ~40 | SignalR, auth |
| Architecture | 2 | ~5 | — |
| Infrastructure | 20 | ~43 | — |
