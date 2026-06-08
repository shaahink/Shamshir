# Iteration NN — [Short title]

<!--
PLAN.md is written BEFORE implementation starts.
HANDOVER.md is written AFTER all phases complete.
Keep them in the same folder: docs/iterations/iter-NN/
-->

---

## Context: what is broken right now

<!--
One paragraph. What symptom does the user observe? What is the root cause?
Link to issue IDs from docs/OPEN-ISSUES.md.
Example: "Running a replay backtest produces 0 trades (BUG-01). The root cause is that
BacktestReplayAdapter.SubmitOrderAsync registers orders but SimulateFill is never called,
so ExecutionStream is always empty."
-->

---

## What this iteration delivers

<!--
One sentence per phase. User-visible outcomes only.
Example:
- Phase A: Replay backtest produces trades (BUG-01 fixed)
- Phase B: Equity curve shows real drawdown (BUG-04 fixed)
- Phase C: E2E test gates future regressions
-->

---

## Baseline (capture before starting)

Run these queries against the SQLite DB and record the output here.
The implementing agent must verify that numbers change after the iteration.

```sql
SELECT COUNT(*) as Trades FROM Trades;
SELECT COUNT(*) as BarEvals FROM BarEvaluations;
SELECT RunId, TotalTrades, MaxDrawdownPct FROM BacktestRuns ORDER BY StartedAtUtc DESC LIMIT 3;
```

Results before:
```
(paste output here)
```

---

## Files to change

<!--
Exhaustive list. If a file is not in this list, do not touch it.
-->

| File | Change summary |
|------|---------------|
| `src/.../Foo.cs` | Add SimulateFill call in ConnectAsync |
| `src/.../Bar.cs` | Fix exit reason logic |
| `tests/.../Test.cs` | New E2E test |

---

## Phase A — [Name]

### What problem this fixes
<!-- One sentence -->

### Do NOT touch
<!-- Explicit list of files/methods that look related but must not be changed -->
- `src/TradingEngine.Host/EngineWorker.cs` — no changes needed here
- Do not add raw SQL to any `Program.cs`

### Changes

#### `src/.../Foo.cs` — `ConnectAsync` method

**Before (pseudocode):**
```
foreach (var bar in bars)
    write bar to channel
    write tick to channel
```

**After (pseudocode):**
```
foreach (var bar in bars)
    write bar to channel
    write tick to channel
    fill any pending orders at bar.Close   // NEW
    emit AccountUpdate with updated balance // NEW
```

**Why:** Orders were submitted but never filled, leaving ExecutionStream empty. (BUG-01)

---

## Phase B — [Name]

<!-- Same structure as Phase A -->

---

## Verification

Each verification must be runnable by the implementing agent with no cTrader credentials.

```powershell
# 1. Build
dotnet build --no-incremental
# Expected: 0 errors

# 2. Unit tests
dotnet test tests/TradingEngine.Tests.Unit --no-build
# Expected: all pass

# 3. Simulation test (the primary gate)
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "BacktestReplay"
# Expected: pass

# 4. DB state (run after the simulation test)
# sqlite3 data/trading.db "SELECT COUNT(*) FROM Trades WHERE RunId IS NOT NULL;"
# Expected: > 0
```

---

## Forbidden list (for all phases)

<!--
Things the implementing agent must not do, even if they look like improvements.
-->

- Do not add EF migrations unless this iteration explicitly calls for schema changes
- Do not change channel capacities or FullMode settings without a design reason
- Do not use `DateTime.UtcNow` — use `IEngineClock.UtcNow`
- Do not modify existing migrations
- Do not change `BacktestRunner.RunAsync` ctrader-cli path unless a phase explicitly targets it

---

## Known risks

<!--
What could go wrong? What adjacent code might be affected?
Example: "Changing ConnectAsync signature may require updating BacktestReplayTest.cs"
-->

---

## Handover notes (filled in AFTER implementation)

<!--
The implementing agent fills this section.
-->

### Completed phases
- [ ] Phase A
- [ ] Phase B

### What changed (files modified/created)

| File | What changed |
|------|-------------|
| | |

### Verification results

| Check | Result |
|-------|--------|
| `dotnet build` | |
| Unit tests | |
| Simulation tests | |
| DB trade count after test | |

### Issues closed

| Issue ID | Status |
|----------|--------|
| BUG-01 | ✅ Fixed |

### Deferred items

| Item | Reason |
|------|--------|
| | |

### Decisions made

| ID | Decision |
|----|----------|
| D89 | |
