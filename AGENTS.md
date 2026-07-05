# AGENTS.md — Session Startup Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/quant-model--p1-tf-agnostic` (active) / `develop` (authoritative, merged)
**Created:** 2026-06-18
**Updated:** 2026-07-05 (P4.5 delivery — 3 sub-phases done + cTrader test triage)

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
13. **`docs/CTRADER-TEST-POLICY.md`** — cTrader test triage: which tests stay, which move to tape

## Build and test

```powershell
dotnet build                                 # Full build
dotnet test tests/TradingEngine.Tests.Unit   # Unit tests (~459 pass)
dotnet test tests/TradingEngine.Tests.Simulation  # Simulation/FTMO tests
dotnet test tests/TradingEngine.Tests.Integration  # Integration tests (100)
```

## Architecture at a glance

```
src/
  TradingEngine.Domain/          # Pure domain — zero infra deps
  TradingEngine.Application/     # Assembly marker only
  TradingEngine.Infrastructure/  # EF Core, Skender, adapters, persistence
  TradingEngine.Risk/            # Risk engine, position sizing, prop firm rules
  TradingEngine.Strategies/    # Strategy implementations
  TradingEngine.Services/      # PipCalc, SL/TP, trailing, EntryPlanner, TradeCost, ExitLab
  TradingEngine.Host/          # EngineWorker, DI wiring, Program.cs
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

- P0–P2 (all phases) delivered and gated
- P3.1 (excursion recorder), P3.2 (exploration mode), P3.3 (ExitReplayer*), P3.4 (calibration tables*), P3.5 (Exit Lab UI*) delivered — * = P4.5 fixed fidelity gaps
- P4.1–P4.4 (research metrics) delivered but P4.5 fixed the walk-forward harness + scoreboard + P(pass)
- **P4.5 (static-review fixes): 7 sub-phases** — P4.5.2/.3/.1/.4/.5/.6/.7. P4.5.2/.3/.1 DONE (Exit Lab JSON mismatch, ExitReplayer 4 fixes, walk-forward test leg + PlateauPicker). P4.5.4/.5/.6/.7 + cTrader test triage DONE (calibration wiring, P(pass) framing, scoreboard frequency+filter, path cap+fetch-by-run, cTrader retire+keep tags).
- **All gates green:** Unit 459/0/6, Integration 100/0, Simulation 127/0 byte-identical
- **Gate filter:** `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ&Category!=CtraderContract"`
- cTrader-backed tests are now triaged: keep-set tagged `Category=CtraderContract`, 5 tests retired (skipped). cTrader tests NEVER run as phase gates for engine/research/UI work.
- Parent branch `iter/quant-model` has P0 (3 gated commits pushed to origin)

## What's next

See `docs/iterations/iter-quant-model/PLAN.md` §3 for the full iteration spec.
**Next phase: P5 — Data + triage (owner-driven program).** Download 7 symbols × {M1,M15,H1,H4}, non-FX
correctness tests, exploration triage, portfolio assembly. P4.5 is now COMPLETE — P5 is unblocked.
See PLAN.md §3 P5 + §9.3 for per-phase agent direction.

Carried-forward debts (not P5 blockers, but keep visible):
- `MISSING_DATA` verdict (P1.5.4) — zero hits repo-wide; deferred to verdict-funnel UI
- `ReferenceScales` population (P3.4b) — schema exists, table empty; must land inside P5.1 ingest
- Kernel-path limit orders reach cTrader as Market (P2.7 carry-forward) — investigate in P6 reconcile

## Rules you must not break

1. `decimal` for all price, money, lot arithmetic
2. Never add infrastructure deps to `TradingEngine.Domain`
3. Schema changes via EF migrations only
4. No `Console.WriteLine` — Serilog message templates only
5. Don't touch `aspire/AppHost` (NU1903)
6. Keep Unit + Simulation + Integration suites green — stop-the-line on red
7. Golden must stay 63/63 byte-identical (kernel untouched)
