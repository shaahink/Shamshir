# AGENTS.md — Session Startup Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/merge-plan` (active worktree: `C:\Code\shamshir-trust`)
**Created:** 2026-06-18
**Updated:** 2026-07-02 (session close — M3.3 done, Groups 1-2 done, deep audit complete, 15 pre-flight bugs fixed, ready for owner manual testing)

---

## Read this first (mandatory, in order)

At the start of every session:

1. **`docs/reference/SYSTEM-REFERENCE.md`** — Start with §1 (system overview) → then skim the rest
2. **`docs/reference/CODE-MAP.md`** — Feature→file index + process walkthroughs — find where anything lives
3. **`docs/reference/BACKTEST-ARCHITECTURE.md`** — How backtesting actually works (both venue paths)
4. **`docs/reference/TEST-ARCHITECTURE.md`** — Test tiers, harnesses, which tests need cTrader credentials
5. **`docs/WORKFLOW.md`** — Agent workflow rules, code standards, handover format
6. **`DECISIONS.md`** — All resolved decisions (D1–D96)
7. **`docs/OPEN-ISSUES.md`** — Historical open issues (most resolved; remaining gaps → PROGRESS.md §ALL REMAINING)
8. **`docs/NEXT-STEPS.md`** — Historical roadmap (most items mapped into merge-plan tracks)
9. **`docs/iterations/iter-merge-plan/PLAN.md`** — **CURRENT plan** (M1–M5 phases)
10. **`docs/iterations/iter-master-plan/PLAN.md`** — **Reference** master plan (Tracks A–G: venue fidelity, portfolio, symbol program, quant phases)
11. **`docs/iterations/iter-marketdata-tape/HANDOVER-REVIEW.md`** — Bug/gap IDs B1–B11, F1–F8 defined here
12. **`docs/QUANT-ROADMAP.md`** — Strategy calibration & experiment methodology (Q1–Q4)
13. **`docs/audit/PROGRESS.md`** — **Current status**: gates, what's done, ALL REMAINING ITEMS in priority order
14. **For cTrader work:** load the `shamshir-ctrader` skill first — covers cBot, NetMQ, engine adapter, launch paths, cache
15. **`docs/RESOLVED-ISSUES.md`** — Audit trail of fixed issues (reference only)

## Build and test

```powershell
dotnet build                                 # Full build
dotnet test tests/TradingEngine.Tests.Unit   # Unit tests (~314 pass)
dotnet test tests/TradingEngine.Tests.Simulation  # Simulation/FTMO tests
dotnet test tests/TradingEngine.Tests.Integration  # Integration tests (105)
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

## Current state (iter-merge-plan, session close 2026-07-02)

- **M1–M4 done** — nav, backtest/monitor/report redesign, charts, narrative service, monitor↔narrative switch, settings+reset UI, run delete/prune, data-manager delete, SkipJournal verified.
- **M3.3 signed off** — `EntryReason`/`EntryRegime`/`EntrySnapshotJson` populated at open (threaded OrderProposed→PositionState→PublishTradeClosed→EffectExecutor). "Why entered"/"Why exited" surfaced on trade-detail + report expanded row.
- **All B1–B11 bugs fixed** · **F1–F4, F8 fidelity gaps fixed** · **F6–F7 documented in RECONCILE-FINDINGS.md**
- **Golden 63/63** · **Unit 314/0/6** · **Integration 108/0** · DB migrations applied (InitialCreate + AddTradeNarrativeColumns)
- **15 pre-flight bugs fixed** — trade-detail safeParse reject arrays, exitReason always visible, unhandled promise caught, NaN guards on accumulators, TradeChartCardComponent reloads on trade switch, gateRejections null-safe, missing OpenedAtUtc in global trade endpoint, Duplicate button null guards, trade-gallery OnDestroy import, dlResult type mismatch

### What's ready for owner testing
- Data Manager: download symbols 12, timeframes 6 (M1/M5/M15/H1/H4/D1), M1 overlap + spread-pips chips
- New Backtest: two-pane, coverage check, tape M1 guard disables start when missing
- Run Monitor: 2×2 grid, narrative polling, counter bar, overlap protection (409 on concurrent start)
- Run Report: 4 tabs, 10-col trade table with column chooser, expanded row shows why entered/exited + trade chart
- Trade Detail: 16 stat tiles + why entered/exited sections
- Settings: system info, prune keep-last-N, 3 reset scopes with confirm modal

### What's NOT done
See **`docs/audit/PROGRESS.md` §ALL REMAINING GAPS** — 18 items in 5 tiers with reasons + remedies.
Top next items (Tier 1 quick wins, ~2hr): verify violations render in journal, hardcoded values audit, DB path unification.
Top features (Tier 2, ~days): Portfolio entity (Track F1), Symbol scorecard (Track G1), Excursion recorder (Q1).
Owner-only (Tier 5): V2–V5 cTrader downloads + reconcile, M5.1–M5.3 oracle set + drift alarm + per-bar spread, cBot E2E.

## Rules you must not break

1. `decimal` for all price, money, lot arithmetic
2. Never add infrastructure deps to `TradingEngine.Domain`
3. Schema changes via EF migrations only
4. No `Console.WriteLine` — Serilog message templates only
5. Don't touch `aspire/AppHost` (NU1903)
6. Keep Unit + Simulation + Integration suites green — stop-the-line on red
7. Golden must stay 63/63 byte-identical (kernel untouched)
