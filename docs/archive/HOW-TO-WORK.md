# Agent Guide — Shamshir Trading Engine

**Read this before touching any file.**

This is a prop-firm algorithmic trading engine. Getting financial calculations wrong produces
silent, compiling errors that corrupt backtest results. Read before coding; verify before committing.

---

## Project layout (only the parts you'll commonly touch)

```
src/
  TradingEngine.Domain/          # Pure domain — zero infrastructure deps (enforced)
  TradingEngine.Services/        # PersistenceService, PositionTracker, PipCalculator
  TradingEngine.Strategies/      # Strategy implementations (MeanReversion etc.)
  TradingEngine.Infrastructure/  # EF Core, SQLite repos, BacktestReplayAdapter
  TradingEngine.Host/            # EngineWorker, event handlers, DI wiring
  TradingEngine.Web/             # Razor Pages / Blazor UI, BacktestOrchestrator
  TradingEngine.CTraderRunner/   # BacktestRunner, BacktestConfig, BacktestResult
tests/
  TradingEngine.Tests.Unit/      # xUnit, no cTrader needed, 87 tests
  TradingEngine.Tests.Integration/
  TradingEngine.Tests.Simulation/  # end-to-end with BacktestReplayAdapter (no credentials)
docs/
  agents/          # this folder — read before implementing
  iterations/      # PLAN.md (pre-impl) + HANDOVER.md (post-impl) per iteration
  reference/       # BACKTEST-ARCHITECTURE.md, OPEN-ISSUES.md, DECISIONS.md
```

---

## Build and test commands

```powershell
# Build (run this first — never skip)
dotnet build --no-incremental

# Unit tests (must pass before any PR)
dotnet test tests/TradingEngine.Tests.Unit --no-build

# Simulation tests (no cTrader credentials needed)
dotnet test tests/TradingEngine.Tests.Simulation --no-build

# Single test filter
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "BacktestReplay"
```

---

## The five rules you must not break

**1. `decimal` for all price, money, and lot arithmetic.**
`double` is only allowed at Skender indicator boundaries. Any price comparison, SL/TP calculation,
or PnL arithmetic in `double` is a bug. The domain rules document explains why.

**2. Never add infrastructure dependencies to `TradingEngine.Domain`.**
Domain has zero project references to Infrastructure, Host, or Web. Analyser rules enforce this.
If you think you need to add one, you're solving the wrong problem.

**3. `CancellationToken` as last parameter on every async method. No exceptions.**

**4. `BoundedChannelFullMode.Wait` for order/trade channels. `DropOldest` only for analytics.**
Dropping a trade event silently is worse than backpressure. See code standards.

**5. Schema changes via EF migrations only.**
Do not write `ALTER TABLE` or `CREATE TABLE` raw SQL in startup files.
`ctx.Database.ExecuteSqlRaw(...)` in `Program.cs` is existing tech debt — do not add more.
Run: `dotnet ef migrations add <Name> --startup-project src/TradingEngine.Web --project src/TradingEngine.Infrastructure`

---

## The two backtest paths (read this carefully)

There are two completely separate ways to run a backtest. They share the engine code but use
different broker adapters:

### Path A — cTrader (production-equivalent)
```
BacktestOrchestrator
  → BacktestRunner.RunAsync()
    → launches ctrader-cli as external Process
      → cBot connects to engine via NetMQ
      → engine uses NetMQBrokerAdapter
```
Requires: CTrader credentials (`CTrader:CtId`, `CTrader:PwdFile`, `CTrader:Account` in config).
Used for: final verification, production-quality results.
Cannot be automated in CI.

### Path B — Engine Replay (development, CI)
```
BacktestOrchestrator (or test harness)
  → spins up engine in-process
  → engine uses BacktestReplayAdapter
    → reads bars from SQLite Bars table
    → fills orders at bar close price
    → emits AccountUpdate after each fill
```
Requires: pre-seeded bars in SQLite (from a prior cTrader run or fixture data).
Used for: integration tests, development iteration, CI.

**As of Iteration 10**: Path B (BacktestReplayAdapter) is implemented but not wired to the UI.
It has critical bugs (BUG-01, BUG-02 in `docs/OPEN-ISSUES.md`). Fix those before using it.

---

## Where live issues are tracked

`docs/OPEN-ISSUES.md` — the canonical bugs/design-problems list. Issue IDs are `BUG-NN`, `DESIGN-NN`, etc.
When fixing an issue, reference its ID in the commit message: `fix: BUG-01 BacktestReplayAdapter fills orders`.

---

## Iteration lifecycle

Each iteration lives in `docs/iterations/iter-NN/`:
- `PLAN.md` — written by the planning agent (or human) before any code changes
- `HANDOVER.md` — written by the implementing agent after all phases complete

The PLAN.md is your spec. If something in the plan is ambiguous, **stop and note it** in HANDOVER.md
rather than guessing. Financial systems silently accept wrong numbers.

Template for both files: `docs/agents/ITERATION-TEMPLATE.md`

---

## Key domain facts (do not guess these)

- **Lot sizing uses Floor, never Round** — rounding up exceeds risk target
- **Long fills at Ask, short fills at Bid** — using Mid makes backtest results better than live
- **FTMO equity = Balance + FloatingPnL − Commissions − Swaps** — all four, always
- **JPY pairs: pip size 0.01; XAUUSD: pip size 0.01; most forex: 0.0001**
- **Cross-rate pip values change per tick** — never cache them

Full rules: `docs/reference/DOMAIN-KNOWLEDGE.md`.

---

## What the implementing agent should produce at the end of every iteration

1. `docs/iterations/iter-NN/HANDOVER.md` filled in
2. All issues referenced in PLAN.md marked fixed in `docs/OPEN-ISSUES.md`
3. `dotnet build --no-incremental` passes with 0 errors
4. `dotnet test tests/TradingEngine.Tests.Unit` passes
5. Simulation tests pass (or explicitly noted as deferred with reason)
