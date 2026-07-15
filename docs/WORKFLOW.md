# Shamshir — Agent Workflow

> Version: 1.0 — 2026-06-06
> This document is the first thing any sub-agent reads before touching code.
> It defines how iterations work, what is required during work, and what must be produced at the end.

---

## 1. What You Are

You are a sub-agent implementing a specific iteration of the Shamshir trading engine.
You have been given an iteration brief (`ITERATION-N.md`). Your job is to execute it fully,
keep records current as you go, and leave a handover document for the next session.

You are NOT designing the system. You are implementing a scoped plan that was designed
in a human review session. If you encounter something not covered by the brief, document
it in the handover — do not expand scope silently.

---

## 2. Before Writing Any Code — Mandatory Reading

In this exact order:

1. **`AGENTS.md`** — session startup guide. Branch state, build commands, key facts.
2. **`docs/reference/SYSTEM-REFERENCE.md`** — §1 system overview, then skim the full reference
3. **`docs/reference/CODE-MAP.md`** — feature→file index — find where anything lives
4. **`docs/reference/BACKTEST-ARCHITECTURE.md`** — how backtesting actually works (both venue paths)
5. **`docs/reference/TEST-ARCHITECTURE.md`** — test tiers, harnesses, credential deps
6. **`docs/WORKFLOW.md`** — this file. Understand the process and code standards.
7. **`DECISIONS.md`** — all locked decisions D1–D80
8. **`docs/OPEN-ISSUES.md`** — active bugs and design problems
9. **`docs/NEXT-STEPS.md`** — roadmap backlog
10. **`docs/RESOLVED-ISSUES.md`** — audit trail of fixed issues (reference only)
11. **`docs/iterations/iter-NN/PLAN.md`** — the brief for your specific iteration
12. Prior handover — `docs/iterations/iter-NN/HANDOVER.md` if it exists

---

## 3. Repository Layout — Where Things Live

```
C:\Code\Shamshir\
├── src/
│   ├── TradingEngine.Domain/           # Pure domain: value objects, interfaces, events
│   ├── TradingEngine.Application/      # Assembly marker only — stays empty
│   ├── TradingEngine.Infrastructure/   # EF Core, Skender, adapters, persistence
│   ├── TradingEngine.Risk/             # DrawdownTracker, RiskManager, PositionSizer
│   ├── TradingEngine.Strategies/       # Strategy implementations
│   ├── TradingEngine.Services/         # PipCalc, SL/TP, trailing, EntryPlanner, TradeCost
│   ├── TradingEngine.Adapters.CTrader/ # cBot (net6.0, cTrader.Automate NuGet)
│   ├── TradingEngine.Host/             # EngineWorker, Program.cs, DI wiring
│   ├── TradingEngine.Web/              # Razor Pages, API controllers, SSE/SignalR
│   └── TradingEngine.CTraderRunner/    # Orchestrator for CLI backtest
├── aspire/
│   └── TradingEngine.AppHost/          # .NET Aspire dev orchestration only
├── tests/
│   ├── TradingEngine.Tests.Unit/       # xUnit — fast, no I/O, ~207 tests
│   ├── TradingEngine.Tests.Integration/# EF Core + SQLite integration tests
│   └── TradingEngine.Tests.Simulation/ # End-to-end backtest harness tests
├── config/
│   ├── strategies/                     # JSON per strategy config (seed → DB)
│   ├── risk-profiles/                  # JSON per risk profile
│   ├── prop-firms/                     # JSON per prop firm ruleset
│   └── symbols.json                    # Symbol metadata incl. cost fields
├── tests/data/                         # Committed test CSV files
├── docs/
│   ├── reference/                      # DOMAIN-KNOWLEDGE, DESIGN-V1, SYSTEM-REFERENCE, etc.
│   ├── agents/                         # HOW-TO-WORK, ITERATION-TEMPLATE
│   ├── iterations/                     # iter-NN/PLAN.md + HANDOVER.md per iteration
│   └── archive/                        # Old phase briefs, start docs, historical specs
├── AGENTS.md                           # Session startup — read first
├── DECISIONS.md                        # Decision record D1–D80 — update as you go
├── README.md
└── TradingEngine.sln
```

---

## 4. Code Standards — Non-Negotiable

These apply to every file you touch. Violations block the PR.

### Language
- .NET 10, C# 13 for all engine projects
- `net6.0` for `TradingEngine.Adapters.CTrader` only (cTrader CLI requirement)
- `record`, `sealed`, `required`, primary constructors where appropriate
- `var` for locals when type is obvious from the right side

### Naming
- Interfaces: `IFoo` — one interface per file
- Implementations: `FooService`, `FooAdapter`, `FooRepository` — suffix indicates role
- Tests: `SubjectTests`, `[Fact]`, `[Theory]` with `[InlineData]`
- No `Manager` suffix unless it already exists in codebase (don't introduce new ones)

### Logging
- Serilog only. Use message templates: `_logger.LogInformation("Trade {Id} filled at {Price}", id, price)`
- No string interpolation in log calls: `$"Trade {id}"` is forbidden
- No `Console.WriteLine` — anywhere, ever

### Financial arithmetic
- `decimal` for all money, price, lot, pip values
- `double` for indicator values (Skender returns `double`)
- `Math.Floor` for lot rounding — never `Math.Round`

### Comments
- Default: no comments
- Add one only when the WHY is non-obvious: a hidden constraint, a specific bug workaround, a subtle invariant
- Never describe WHAT the code does — names do that

### Error handling
- Validate at system boundaries only (user input, pipe messages, external JSON)
- Inside the engine: trust types and framework guarantees
- `try/catch` in: `Evaluate()` in every strategy (returns null on error), pipe read loops, fire-and-forget persistence
- Everywhere else: let exceptions propagate

### DI lifetime rules
- Singletons: everything that holds mutable state shared across requests (`RiskManager`, `DrawdownTracker`, `PositionManager`, `PersistenceService`, broker adapters, all `IHostedService`)
- Scoped: `DbContext`, repositories — ONLY inside `PersistenceService` which creates its own scope
- Never inject Scoped into Singleton

### Channels
- Bounded channels: capacity 1000, `FullMode.Wait`, `SingleWriter: true, SingleReader: true` where applicable
- Always use `await foreach` for reading — never `.ReadAsync()` in a loop without `ConfigureAwait`

---

## 5. During Implementation — Housekeeping Rules

As you work through phases, maintain these records:

### DECISIONS.md — update continuously
- If you make a design decision that isn't in DECISIONS.md, add it with the next available D-number
- Format: `| D{N} | Short description | ✅ Decision made: brief rationale |`
- Never contradict an existing decision without flagging it in the handover

### Bugs found — log them
- Keep a running list in your working notes (not in the codebase — in the handover)
- Format: `[SEVERITY] File:Line — description`
- CRITICAL = blocks engine start or silently corrupts financial state
- SERIOUS = incorrect behavior in a common path
- MODERATE = edge case, cosmetic, or test-only concern

### Branch discipline
- One branch per sub-phase: `phase/{N}{letter}-{name}` (e.g., `phase/4a-money-management`)
- PR into `develop`. Never push directly to `main`
- Commit message: imperative mood, under 72 chars, reference the phase

### Tests — write them as you go, not at the end
- Add tests in the same commit as the code they test
- Run `dotnet test TradingEngine.sln` before every commit
- Never commit with failing tests unless you explicitly note them as known-failing in the handover

---

## 6. Definition of Done — Per Phase

A phase is complete when ALL of the following are true:

- [ ] `dotnet build TradingEngine.sln` — 0 errors, 0 warnings
- [ ] `dotnet test TradingEngine.sln` — all tests pass (count increases vs prior iteration)
- [ ] New code covered by at least one test (unit or integration)
- [ ] DECISIONS.md updated with any new decisions made during this phase
- [ ] No TODO / FIXME / NotImplementedException left in phase scope
- [ ] No hardcoded credentials, paths, or magic numbers without a config fallback
- [ ] No `Console.WriteLine` introduced
- [ ] Serilog message templates used (no string interpolation in log calls)

---

## 7. End of Iteration — Handover Document

When all phases are complete (or you have reached the scope limit), write `ITERATION-N-HANDOVER.md`.
This is the primary artifact for the next human review session.

### Handover format:

```markdown
# Iteration N — Handover

> Date: YYYY-MM-DD
> Branch: phase/...
> Tests: X passing (was Y at start)

## 1. What Was Completed
[Per-phase table: Phase | Status | Tests Added | Key deliverables]

## 2. What Was NOT Completed (and why)
[Scope items that were not reached — with reasons]

## 3. Bugs Found During Implementation
[Running list from §5 above]

## 4. New Decisions Added (D{N}+)
[Decisions not in the brief that were made during implementation]

## 5. Known Failing Tests (if any)
[Name + reason — must be zero for a clean handover]

## 6. How to Verify
[Exact commands to run to reproduce the working state]

## 7. Recommended Focus for Next Iteration
[What the next agent/session should prioritize, based on what you learned]
```

Do NOT skip sections. An incomplete handover is worse than no handover.

---

## 8. What You Must NOT Do

- Do not expand scope beyond the iteration brief without flagging it explicitly
- Do not silently fix bugs from prior iterations unless they are blocking your current phase
  (if you fix them anyway, document them in §3 of the handover)
- Do not introduce abstractions, patterns, or refactors beyond what the task requires
- Do not create planning documents, architecture diagrams, or analysis files
  (the handover is the only document you create beyond source code)
- Do not commit secrets, credentials, or passwords — even in comments
- Do not use `DateTime.Now` or `DateTime.UtcNow` directly — use `IEngineClock`
- Do not add Skender types outside the Infrastructure project
- Do not use `Math.Round` for lot sizing — always `Math.Floor`
