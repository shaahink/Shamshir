# AGENTS.md — Session Startup Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/quant-model--p1-tf-agnostic` (active) / `develop` (authoritative, merged)
**Created:** 2026-06-18
**Updated:** 2026-07-04 (P1 TF-agnostic bank delivered)

---

## Read this first (mandatory, in order)

At the start of every session:

1. **`docs/reference/SYSTEM-REFERENCE.md`** — Start with §1 (system overview) → then skim the rest
2. **`docs/reference/CODE-MAP.md`** — Feature→file index + process walkthroughs — find where anything lives
3. **`docs/reference/BACKTEST-ARCHITECTURE.md`** — How backtesting actually works (both venue paths)
4. **`docs/reference/TEST-ARCHITECTURE.md`** — Test tiers, harnesses, which tests need cTrader credentials
5. **`docs/WORKFLOW.md`** — Agent workflow rules, code standards, handover format
6. **`DECISIONS.md`** — All resolved decisions (D1–D96)
7. **`docs/OPEN-ISSUES.md`** — ALL remaining bugs + tasks (single source of truth, kept current)
8. **`docs/iterations/iter-quant-model/PROGRESS.md`** — Session handover: what happened, what's next (current iteration)
9. **`docs/audit/PROGRESS.md`** — Progress metrics, gate history, branch state
10. **`docs/QUANT-ROADMAP.md`** — Strategy calibration & experiment methodology
11. **For cTrader work:** load the `shamshir-ctrader` skill first — covers cBot, NetMQ, engine adapter, launch paths, cache
12. **`docs/RESOLVED-ISSUES.md`** — Audit trail of fixed issues (reference only)

## Build and test

```powershell
dotnet build                                 # Full build
dotnet test tests/TradingEngine.Tests.Unit   # Unit tests (~347 pass)
dotnet test tests/TradingEngine.Tests.Simulation  # Simulation/FTMO tests
dotnet test tests/TradingEngine.Tests.Integration  # Integration tests (94)
```

## Architecture at a glance

```
src/
  TradingEngine.Domain/          # Pure domain — zero infra deps
  TradingEngine.Application/     # Assembly marker only
  TradingEngine.Infrastructure/  # EF Core, Skender, adapters, persistence
  TradingEngine.Risk/            # Risk engine, position sizing, prop firm rules
  TradingEngine.Strategies/      # Strategy implementations
  TradingEngine.Services/        # PipCalc, SL/TP, trailing, EntryPlanner, TradeCost
  TradingEngine.Host/            # EngineWorker, DI wiring, Program.cs
  TradingEngine.Web/             # Razor Pages, API controllers, SSE/SignalR
  TradingEngine.Adapters.CTrader/ # C# 6 cBot (cTrader integration)
  TradingEngine.Engine/          # Kernel engine (EngineReducer, EngineState)
tests/
  TradingEngine.Tests.Unit/      # xUnit, isolated
  TradingEngine.Tests.Simulation/ # End-to-end backtest
  TradingEngine.Tests.Integration/ # EF Core + SQLite integration tests
```

## Key facts

- **Three venue paths:** `BacktestReplayAdapter` (credential-free, per-run bars from DB), `TapeReplayAdapter` (fast, from `marketdata.db`), and `CTraderBrokerAdapter` (cTrader NetMQ). Default is replay.
- **All money math in `decimal`** — `double` only at Skender indicator boundaries.
- **Lot sizing uses `Math.Floor`, never `Math.Round`.**
- **Schema via EF migrations only** — no raw SQL `ALTER TABLE`.
- **`CancellationToken` as last parameter on every async method.**
- **`BoundedChannelFullMode.Wait`** for order/trade channels; `DropOldest` only for analytics.
- **`IEngineClock`** for all time — never `DateTime.UtcNow` directly.

## Current state (iter/quant-model--p1-tf-agnostic)

- P0 (truth repair) delivered: R-vs-initial-stop, full-spread convention, honest entry timing (3 commits)
- P1 (TF-agnostic strategy bank) delivered: instance-per-row, de-hardcoded H1 bar lookups in all 14 strategies, proposal TF metadata, aux-TF feed for mtf-trend, HonestFills checkbox (4 commits)
- P1.5 (static-review fixes) delivered: indicator *requests* now bound to EntryTimeframe in all 9 strategies (bar lookups alone weren't enough), aux-TF (H4) bars revealed point-in-time via a cursor instead of bulk-loaded (fixed a lookahead-bias bug), run-plan TF parse failures now throw instead of silently binding H1
- P2.1 (indicator series API) delivered: IndicatorSnapshotService keeps a capped ring buffer (64, latest last) per sig key via one write point; MarketContext.IndicatorSeries carries it to strategies. Ported macd-momentum/super-trend/mtf-trend/bb-squeeze off private cadence-fragile fields onto the series (deleted `_lastHist`/`_prevDirection`/`_prevRsi`/`_bbWidthQueue`)
- P2.2 (rsi-divergence rewrite) delivered: deleted the P0-era tautology; new `PivotFinder` (pure fractal swing detector) + real pivot-based divergence (price lower-low/RSI higher-low or mirror, confirmed by a breakout close). `DivergenceLookback` grew 10→50 (a real double-bottom/top span is dozens of bars, not a handful)
- P2.3 (edge semantics) delivered: bb-squeeze latch now expires after BbPeriod bars unarmed (D8); trend-breakout single-fires on a false→true rolling-window transition instead of every bar of a continuing trend, plus a CooldownBars gate (D5); ema-alignment converted from a state CONDITION to a real crossover+first-pullback-touch edge, fully derived from bars+series with no private state (D5)
- P2.4 (time-flatten) delivered: `IStrategyConfig.FlattenAtUtc` (default interface member) + `SessionBreakoutConfig` wiring its previously-dead `FlattenTimeUtc`; new `KernelTimeFlattenEvaluator` (mirrors `KernelTrailingEvaluator`) closes a strategy's open positions once the bar's time-of-day reaches it, reusing the existing-but-never-called `CloseRequested` event/reducer path (no new kernel event needed)
- P2.5 (thesis metadata) delivered: `thesis`/`expectedTradesPerWeek`/`expectedHoldBars` on `StrategyConfigEntry`/`StrategyConfigEntity` (EF migration M35, persisted+editable, not a hardcoded map), all 9 `config/strategies/*.json` seeded with a real falsifiable thesis, surfaced in the Strategies API + Angular UI. Drive-by fixes: `rsi-divergence.json`'s stale `divergenceLookback: 10` (silently overrode P2.2's new default of 50) and `StrategyMetadataMap`'s dead `"bollinger-squeeze"` key (real id is `"bb-squeeze"`)
- Cross-symbol state pollution fixed (per-row instances instead of singletons)
- Tape venue: correct full-spread convention via shared `SpreadConvention` helper, both adapters unified
- Honest entry fills: tape market entries queue at signal bar, fill at next M1 bar open (toggleable)
- Non-H1 strategy runs are now actually verified end-to-end (M15 tape run produces proposals) — this is the claim P1 made but hadn't tested
- All gates green: Unit 386/0/6, Integration 98/0, fast Simulation 125/0 (~9s, cTrader-touching categories excluded — see below), Architecture 6/8 (2 pre-existing, undisturbed)
- Parent branch `iter/quant-model` has P0 (3 gated commits pushed to origin)
- **Gate filter note (owner request, 2026-07-05):** cTrader E2E tests are slow/flaky here even with real credentials present (confirmed — they run for real, not skip, and cost 10-25+ min under contention). For P2's remaining phases, gate with `--filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"` (NOT `RequiresCTrader!=true` alone — `PipelineE2ETests` has no `RequiresCTrader` trait, only `Category=E2E/Slow`). Run the full suite once at the end of P2, not per-phase.

## What's next

See `docs/iterations/iter-quant-model/PLAN.md` §3 for the full iteration spec.
**Next phase: P2.6** — units doctrine + config linter, then P2.7 (stop orders).
P1.5.4 (MISSING_DATA verdict) is folded into P2's verdict-funnel work.
Uncommitted Angular changes from `iter/data-mgmt` were stashed on this branch (`pre-P1 uncommitted changes from parent branch`).

## Rules you must not break

1. `decimal` for all price, money, lot arithmetic
2. Never add infrastructure deps to `TradingEngine.Domain`
3. Schema changes via EF migrations only
4. No `Console.WriteLine` — Serilog message templates only
5. Don't touch `aspire/AppHost` (NU1903)
6. Keep Unit + Simulation + Integration suites green — stop-the-line on red
7. Golden must stay 63/63 byte-identical (kernel untouched)
