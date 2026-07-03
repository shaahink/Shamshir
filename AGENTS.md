# AGENTS.md — Session Startup Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/tape-trust` (active) / `develop` (authoritative, merged)
**Created:** 2026-06-18
**Updated:** 2026-07-03 (merged to develop, branches/worktrees cleaned, docs consolidated)

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
8. **`docs/iterations/iter-merge-plan/NEXT-ITERATION.md`** — Session handover: what happened, what's next
9. **`docs/audit/PROGRESS.md`** — Progress metrics, gate history, branch state
10. **`docs/QUANT-ROADMAP.md`** — Strategy calibration & experiment methodology
11. **For cTrader work:** load the `shamshir-ctrader` skill first — covers cBot, NetMQ, engine adapter, launch paths, cache
12. **`docs/RESOLVED-ISSUES.md`** — Audit trail of fixed issues (reference only)

## Build and test

```powershell
dotnet build                                 # Full build
dotnet test tests/TradingEngine.Tests.Unit   # Unit tests (~314 pass)
dotnet test tests/TradingEngine.Tests.Simulation  # Simulation/FTMO tests
dotnet test tests/TradingEngine.Tests.Integration  # Integration tests (109)
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

## Current state (iter-tape-trust)

- Tape venue runs correctly — reports `completed`, `venue="tape"`, `barsPerSec` measured
- Download pipeline: async job polling, status tracking, 12 symbols + M5/M15 TFs
- Merge plan M1–M4 delivered: nav, Settings, Monitor, Report, Charts, Narrative, Delete, Prune
- M3.3 EntryReason/EntryRegime/EntrySnapshotJson populated with real data (not placeholder)
- Run-overlap protection (A6) — 409 Conflict on concurrent starts
- `RunNarrativeService` fixed — journal lines show real symbol/direction/price/SL/TP
- Daily-DD chart uses 22:00 UTC prop-firm roll, not calendar date
- Data Manager: per-symbol delete, storage totals, m1 overlap badges
- `VenueSessions` orphan rows fixed on run delete
- B1–B11 bugs all fixed, F1–F4+F8 fidelity gaps closed

## What's NOT done

See `docs/OPEN-ISSUES.md` — the single source of truth.
Key remaining: C1 short-spread (2 lines, golden-sensitive), D1 DB fragmentation, D2 hardcoded defaults.

## Rules you must not break

1. `decimal` for all price, money, lot arithmetic
2. Never add infrastructure deps to `TradingEngine.Domain`
3. Schema changes via EF migrations only
4. No `Console.WriteLine` — Serilog message templates only
5. Don't touch `aspire/AppHost` (NU1903)
6. Keep Unit + Simulation + Integration suites green — stop-the-line on red
7. Golden must stay 63/63 byte-identical (kernel untouched)
