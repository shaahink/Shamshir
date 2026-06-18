# AGENTS.md — Session Startup Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/31-costs-journal` (active)
**Created:** 2026-06-18

---

## Read this first (mandatory, in order)

At the start of every session:

1. **`docs/reference/SYSTEM-REFERENCE.md`** — Start with §1 (system overview) → then skim the rest
2. **`docs/reference/CODE-MAP.md`** — Feature→file index + process walkthroughs — find where anything lives
3. **`docs/reference/BACKTEST-ARCHITECTURE.md`** — How backtesting actually works (both venue paths)
4. **`docs/reference/TEST-ARCHITECTURE.md`** — Test tiers, harnesses, which tests need cTrader credentials
5. **`docs/WORKFLOW.md`** — Agent workflow rules, code standards, handover format
6. **`DECISIONS.md`** — All resolved decisions (D1–D80)
7. **`docs/OPEN-ISSUES.md`** — Active bugs, design problems, carry-forward tasks
8. **`docs/NEXT-STEPS.md`** — Roadmap backlog
9. **`docs/RESOLVED-ISSUES.md`** — Audit trail of all fixed issues (reference only)

## Build and test

```powershell
dotnet build                                 # Full build
dotnet test tests/TradingEngine.Tests.Unit   # Unit tests (~207 pass)
dotnet test tests/TradingEngine.Tests.Simulation  # Simulation/FTMO tests
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
tests/
  TradingEngine.Tests.Unit/      # xUnit, isolated
  TradingEngine.Tests.Simulation/ # End-to-end backtest
```

## Key facts

- **Two venue paths:** `BacktestReplayAdapter` (credential-free, default) and `SimulatedBrokerAdapter` (synthetic). cTrader path requires credentials.
- **All money math in `decimal`** — `double` only at Skender indicator boundaries.
- **Lot sizing uses `Math.Floor`, never `Math.Round`.**
- **Schema via EF migrations only** — no raw SQL `ALTER TABLE`.
- **`CancellationToken` as last parameter on every async method.**
- **`BoundedChannelFullMode.Wait`** for order/trade channels; `DropOldest` only for analytics.
- **`IEngineClock`** for all time — never `DateTime.UtcNow` directly.

## Current state (iter-31/32)

- Costs (commission/swap) are computed by `TradeCostCalculator` and applied in both venues
- `EntryPlanner` supports limit orders with resting/expiry semantics
- Journal taxonomy: `SIGNAL → ORDER → FILL → CLOSE` with itemized costs
- Config seeded from JSON to DB (`IStrategyConfigStore`)
- `EffectiveConfigResolver` for per-run overrides via deep-merge
- `RunPlan` for per-run symbol/timeframe selection

## What's NOT done

See `docs/iterations/iter-31-32-combined/HANDOVER.md` for the carry-forward list.
Key items: 31-A2 (cBot cost itemization), 31-C2 (live limit path), 32-P4/P5 (config UI), 31-B2 (lossless live journal).

## Rules you must not break

1. `decimal` for all price, money, lot arithmetic
2. Never add infrastructure deps to `TradingEngine.Domain`
3. Schema changes via EF migrations only
4. No `Console.WriteLine` — Serilog message templates only
5. Don't touch `aspire/AppHost` (NU1903)
6. Keep Unit + Simulation suites green — stop-the-line on red
