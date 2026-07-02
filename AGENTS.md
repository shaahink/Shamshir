# AGENTS.md — Session Startup Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/merge-plan` (active worktree: `C:\Code\shamshir-trust`)
**Created:** 2026-06-18
**Updated:** 2026-07-02 (merge review — synced docs from iter/master-plan, cleaned worktrees, consolidated all remaining gaps)

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

## Current state (iter-merge-plan)

- **M1–M4 done** — nav, backtest/monitor/report redesign, charts, narrative service, monitor↔narrative switch, settings+reset UI, run delete/prune, data-manager delete, SkipJournal verified.
- **All B1–B11 bugs fixed** · **F1–F4, F8 fidelity gaps fixed** · Golden 63/63 · Unit 314/0/6 · Integration 105/0
- **M3.3 partial** — `ExitDetailJson` stamped at close; `EntryReason`/`EntryRegime`/`EntrySnapshotJson` columns exist in DB but are never populated, and neither entry nor exit narrative is surfaced in the trade UI.
- **F5 deferred** — commission half-at-open needs golden re-baseline (owner sign-off required)
- **F6/F7 undocumented** — tape-venue edge cases not yet written up in RECONCILE-FINDINGS.md
- **Tracks F, G, and Q1-Q4** — portfolio, symbol program, and quant phases defined in reference docs but not started

## What's NOT done

See **`docs/audit/PROGRESS.md` §ALL REMAINING ITEMS** for the comprehensive 26-item ordered list.
Top priority: M3.3 finish (#1), F6/F7 docs (#2-#3), then data coverage badge (#4), UX glitches (#5), journal completeness (#6).

## Rules you must not break

1. `decimal` for all price, money, lot arithmetic
2. Never add infrastructure deps to `TradingEngine.Domain`
3. Schema changes via EF migrations only
4. No `Console.WriteLine` — Serilog message templates only
5. Don't touch `aspire/AppHost` (NU1903)
6. Keep Unit + Simulation + Integration suites green — stop-the-line on red
7. Golden must stay 63/63 byte-identical (kernel untouched)
