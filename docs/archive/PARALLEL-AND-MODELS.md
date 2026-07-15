# Parallel Execution and Model Selection Guide

---

## Sequential vs parallel: the rule

**Run iterations in parallel when**:
- They touch completely non-overlapping files
- Each can be independently verified without the other
- Merging them is a clean `git merge` with no conflicts

**Run sequentially when**:
- B needs a test from A to pass before it can verify its own work
- B's code calls A's new methods
- They share files (even if different methods)

### Dependency map for this project

```
iter-11  ──────────────────────────────────────────────────── must run first
    ↓
iter-12  ──────────────────────────────────────────────────── needs 11's E2E test
    ↓
iter-13  ──────────────────────────────────────────────────── needs 12's working data
    ↓ ↓ (fan out — these two can run in parallel)
iter-14  ←──────────── Blazor UI (Web project files)
iter-15  ←──────────── Architecture cleanup (Domain/Services/Infrastructure + a few Host files)
```

Iterations 11 → 12 → 13 are strictly sequential. No parallelism opportunity — each iteration's
primary gate is the previous iteration's E2E test.

Iterations 14 and 15 are independent. The files they touch do not overlap:
- 14 touches: `Web/Pages/`, `Web/Components/`, `Web/wwwroot/js/`, `Web/Program.cs` (Blazor setup only)
- 15 touches: `Domain/EngineRunContext.cs`, `Services/PositionTracker.cs`, `Host/EngineWorker.cs`,
  `Host/BarEvaluationHandler.cs`, `Infrastructure/Migrations/`, `Web/Program.cs` (raw SQL removal only)

The only overlap is `Web/Program.cs`. Schedule 15 to merge first — its change is removing raw SQL
lines, which is mechanically non-conflicting with Blazor `AddServerSideBlazor()` additions.

---

## How to run iter-14 and iter-15 in parallel

```powershell
# From the main repo (iter-13 merged and on main/dev)
git worktree add ..\shamshir-iter-14 -b iter/14-ui-blazor
git worktree add ..\shamshir-iter-15 -b iter/15-arch-cleanup

# Agent for iter-14 opens ..\shamshir-iter-14
# Agent for iter-15 opens ..\shamshir-iter-15
# Both run independently

# When both are ready:
# Merge iter-15 first (smaller, mechanical)
git merge iter/15-arch-cleanup
# Then merge iter-14
git merge iter/14-ui-blazor
# Resolve the one expected conflict in Web/Program.cs manually
```

---

## Within an iteration: can phases be parallelised?

Generally no for this project. Phases within an iteration form a chain:
- Phase A creates a method
- Phase B calls it
- Phase C tests them together

The exception is iter-14 where sub-phases are additive Blazor components with no calling relationships.
Sub-phases 14B, 14C, 14D could theoretically run in parallel on separate branches, but:
- 14A (Blazor setup) must complete first so the project compiles
- The sub-phases add new Razor components; conflict risk is low but merge needs attention

For a single-developer workflow, sequential sub-phases within 14 are simpler and safer.

---

## Model recommendations

| Task | Model | Reasoning |
|------|-------|-----------|
| Writing PLAN.md (iteration planning) | Claude Opus 4 or DeepSeek v4 Pro | Needs architectural reasoning, dependency analysis, financial domain awareness |
| Implementing iter-11, 12 (multi-file backend) | DeepSeek v4 Pro | Complex multi-file C# with financial domain — needs strong reasoning |
| Implementing iter-13 (observability, wiring) | DeepSeek v4 Flash | Mostly additive — new methods, new query, new table column. Less reasoning-heavy |
| Implementing iter-14A–B (Blazor setup + list pages) | DeepSeek v4 Flash | Mechanical Blazor scaffolding |
| Implementing iter-14C (SignalR + live progress) | DeepSeek v4 Pro | State management + real-time wiring needs reasoning |
| Implementing iter-14D (equity chart with JS interop) | DeepSeek v4 Pro | TradingView Lightweight Charts API + Blazor interop is non-trivial |
| Implementing iter-15 (cleanup) | DeepSeek v4 Flash | Mechanical: remove async, add CT param, move file. Verification is simple |
| EF migration baseline (iter-15 Phase A) | DeepSeek v4 Pro | High-risk step; needs careful reasoning about migration state |
| Writing E2E tests (iter-11 Phase B) | DeepSeek v4 Pro | Needs to understand the full system to write a meaningful test |

**General rule**:
- Use **Pro** when the task involves: understanding 3+ file interactions simultaneously, financial
  calculations, migration state, SignalR/async patterns, or "what could go wrong" reasoning.
- Use **Flash** when the task is: additive (new file that calls existing APIs), mechanical refactoring
  (rename, move, remove async), or a single-file fix with a known before/after pattern.

---

## About cTrader backtests

The engine replay path (BacktestReplayAdapter) is for development and CI. It uses bars already in
the SQLite DB from prior cTrader runs.

**For real backtest results, cTrader is required.** The replay adapter uses simplified instant-fill
at bar close. cTrader uses tick-level fill simulation with actual spread, slippage, and commission
models. The numbers will differ.

The correct workflow is:
1. Use engine replay for development iteration (fast, no credentials, CI-friendly)
2. Run the cTrader path periodically to validate strategy performance with realistic fill simulation
3. Compare: if replay shows 15 trades and cTrader shows 13 trades, the difference is fill model, not logic

The cTrader path stays in `BacktestRunner.cs` gated by `CTrader:UseForBacktest = true` in config.
An agent should never remove it.

---

## How to know an iteration is safe to merge

All three must be true:
1. `dotnet build --no-incremental` → 0 errors
2. `dotnet test tests/TradingEngine.Tests.Unit` → all pass (no regression)
3. `dotnet test tests/TradingEngine.Tests.Simulation --filter "ReplayBacktest"` → all pass

Plus: the HANDOVER.md is filled in, including the verification results table.

If any of these fail, the iteration is not complete. The implementing agent should document
the failure in HANDOVER.md under "Deferred items" and not mark the iteration as done.
