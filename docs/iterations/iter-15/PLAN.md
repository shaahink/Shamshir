# Iteration 15 — Architecture Cleanup

**Branch**: `iter/15-arch-cleanup`
**Parallel with**: Iteration 14 — no file overlap
**Gate**: Iteration 13 complete. Does not depend on iter-14.

**Merge order**: merge iter-15 before iter-14 (iter-15 touches `Web/Program.cs` to remove raw SQL;
iter-14 adds Blazor lines to `Web/Program.cs` — merge iter-15 first so the conflict surface is
minimal and mechanical).

```powershell
# From main/dev after iter-13 merged:
git worktree add ..\shamshir-iter-14 -b iter/14-ui-blazor
git worktree add ..\shamshir-iter-15 -b iter/15-arch-cleanup
# iter-15 merges first, then iter-14
```

---

## Read first

- `docs/agents/HOW-TO-WORK.md`
- `src/TradingEngine.Host/EngineWorker.cs` — lines touched in phases B, C, D
- `src/TradingEngine.Services/PositionTracker.cs` — DESIGN-04 fix
- `src/TradingEngine.Host/BarEvaluationHandler.cs` — already fixed in iter-13; verify before touching
- `src/TradingEngine.Web/Program.cs` and `src/TradingEngine.Host/Program.cs` — raw SQL removal

---

## Items in this iteration

| ID | Item | Risk |
|----|------|------|
| AGENT-02 | Replace raw SQL patches with EF migration baseline | High — wrong baseline corrupts DB |
| MIN-05 | Move `EngineRunContext` from Domain to Services layer | Low |
| STD-01 | Remove `await Task.CompletedTask` noise in EngineWorker | Low |
| MIN-06 | Add `CancellationToken` to `RecomputeIndicatorsAsync` | Low |
| STD-05 | Materialise `IEnumerable<IStrategy>` at construction time | Low |
| DESIGN-04 | Prune `_processedExecutionIds` after each position close | Low |

---

## Phase A — EF migration baseline (do this first; highest risk)

**Goal**: Remove all `ctx.Database.ExecuteSqlRaw(...)` calls from startup files.
Replace with a proper EF `InitialSchema` migration that marks the current schema as applied
without re-creating it.

### Why this is dangerous

If you run `dotnet ef migrations add` naively when the DB already exists, EF detects schema
divergence and generates a destructive migration (DROP TABLE). Do not follow the naive path.

### Exact procedure

**Step 1 — Snapshot the live schema**

```powershell
# Run this against the actual live DB (replace path if needed)
sqlite3 data\trading.db ".schema"
# Save the output to a scratch doc for reference
```

**Step 2 — Check for existing migrations**

```powershell
Get-ChildItem src\TradingEngine.Infrastructure\Migrations\
```

If any `*.cs` migration files exist from an earlier `EnsureCreated` call, delete them. The baseline
will replace them.

**Step 3 — Create the baseline migration**

```powershell
dotnet ef migrations add InitialSchema `
  --startup-project src/TradingEngine.Web `
  --project src/TradingEngine.Infrastructure
```

This generates `Migrations/<timestamp>_InitialSchema.cs`.

**Step 4 — Edit the generated migration to be a no-op**

Open the generated file. Replace the entire `Up` method body with:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    // Schema already applied via EnsureCreated before migrations were introduced.
    // This migration is a baseline marker only — it does not modify the DB.
}
```

Replace the entire `Down` method body with:

```csharp
protected override void Down(MigrationBuilder migrationBuilder)
{
    // Intentional no-op — cannot safely reverse a baseline.
}
```

**Step 5 — Apply the baseline**

```powershell
dotnet ef database update InitialSchema `
  --startup-project src/TradingEngine.Web `
  --project src/TradingEngine.Infrastructure
```

This inserts a `__EFMigrationsHistory` row, telling EF the baseline is applied.
Existing data is NOT touched.

**Step 6 — Verify**

```powershell
dotnet ef migrations list `
  --startup-project src/TradingEngine.Web `
  --project src/TradingEngine.Infrastructure
# Expected output: InitialSchema (Applied: <timestamp>)
```

**Step 7 — Remove raw SQL from startup files**

In `src/TradingEngine.Web/Program.cs`, delete lines 28–37 (the `using (var ctx = ...)` block that
runs `ExecuteSqlRaw` statements). Replace with:

```csharp
using (var ctx = new TradingDbContext(
    new DbContextOptionsBuilder<TradingDbContext>().UseSqlite($"Data Source={dbPath}").Options))
{
    ctx.Database.Migrate();
}
```

In `src/TradingEngine.Host/Program.cs`, find and remove the equivalent `ctx.Database.EnsureCreated()`
call (around line 118–119). Replace with:

```csharp
using (var ctx = new TradingDbContext(
    new DbContextOptionsBuilder<TradingDbContext>().UseSqlite($"Data Source={dbPath}").Options))
{
    ctx.Database.Migrate();
}
```

**Step 8 — Final verification**

```powershell
dotnet build --no-incremental
dotnet run --project src/TradingEngine.Web
# App starts without error. Navigate to /Backtests — loads OK. No migration exception.
```

**If any step fails**: Do NOT partially remove the raw SQL. Restore the original files and document
the failure in HANDOVER.md under "Deferred items". The raw SQL is safe to leave in place.

---

## Phase B — Move EngineRunContext to Services layer

**File to move**: `src/TradingEngine.Domain/EngineRunContext.cs`

**Current content** (entire file):

```csharp
namespace TradingEngine.Domain;

public sealed record EngineRunContext(string RunId);
```

**Action**:
1. Delete `src/TradingEngine.Domain/EngineRunContext.cs`
2. Create `src/TradingEngine.Services/EngineRunContext.cs` with:

```csharp
namespace TradingEngine.Services;

public sealed record EngineRunContext(string RunId);
```

3. Find all `using TradingEngine.Domain;` in files that reference `EngineRunContext` and add
   `using TradingEngine.Services;` where it is no longer imported by the existing usings.

Files to check (grep for `EngineRunContext`):
```powershell
Select-String -Path "src\**\*.cs" -Pattern "EngineRunContext" -Recurse | Select-Object Path
```

Expected hits: `EngineWorker.cs`, `Host/Program.cs`, `PositionTracker.cs`,
`BarEvaluationHandler.cs`, `TradePersistenceHandler.cs` (if any). Update usings only where
the `Domain` namespace is not already the source of the import via another type.

**Verify Domain project has no remaining reference**:
```powershell
Select-String -Path "src\TradingEngine.Domain\**\*.cs" -Pattern "EngineRunContext" -Recurse
# Must return nothing
```

---

## Phase C — Remove `await Task.CompletedTask` noise

All three items are in `src/TradingEngine.Host/EngineWorker.cs`.

### C1 — `RecomputeIndicatorsAsync`

Current (last line):
```csharp
await Task.CompletedTask;
```

Remove the `await` keyword from the method signature and the last line:

```csharp
// BEFORE
private async Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf)
{
    ...
    await Task.CompletedTask;
}

// AFTER
private Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf)
{
    ...
    return Task.CompletedTask;
}
```

### C2 — Add `CancellationToken` parameter to `RecomputeIndicatorsAsync` (MIN-06)

```csharp
// BEFORE
private Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf)

// AFTER
private Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf, CancellationToken ct)
```

Update the one call site in `ProcessBarsAsync`:

```csharp
// BEFORE
await RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe);

// AFTER
await RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe, ct);
```

### C3 — `WarmUpIndicatorsAsync`

```csharp
// BEFORE
private async Task WarmUpIndicatorsAsync(CancellationToken ct)
{
    ...
    await Task.CompletedTask;
}

// AFTER
private Task WarmUpIndicatorsAsync(CancellationToken ct)
{
    ...
    return Task.CompletedTask;
}
```

---

## Phase D — Materialise strategies (STD-05)

In `src/TradingEngine.Host/EngineWorker.cs`:

```csharp
// BEFORE (field declaration)
private readonly IEnumerable<IStrategy> _strategies;

// AFTER
private readonly IReadOnlyList<IStrategy> _strategies;
```

```csharp
// BEFORE (constructor)
_strategies = strategies;

// AFTER
_strategies = strategies.ToList();
```

Update the two `_strategies.Count()` calls (lines 97, 368) to `_strategies.Count` (property, not method).

---

## Phase E — Prune _processedExecutionIds (DESIGN-04)

In `src/TradingEngine.Services/PositionTracker.cs`, find the `ClosePosition` method.
After `_openPositions.Remove(...)` (the line that removes the closed position), add:

```csharp
_processedExecutionIds.Remove(evt.OrderId);
```

This prevents unbounded growth during multi-hour backtests. The set is only used for deduplication;
once a position is closed, its ID is no longer needed.

---

## Verification

Run after each phase, not just at the end:

```powershell
# After every phase
dotnet build --no-incremental

# After phase A
dotnet ef migrations list --startup-project src/TradingEngine.Web --project src/TradingEngine.Infrastructure
# Expected: InitialSchema (Applied)

# After phase B
Select-String -Path "src\TradingEngine.Domain\**\*.cs" -Pattern "EngineRunContext" -Recurse
# Expected: no output

# After all phases
dotnet test tests/TradingEngine.Tests.Unit --no-build
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"

# App start smoke test
dotnet run --project src/TradingEngine.Web
# Navigate to /Backtests/Index — loads without error
```

---

## Forbidden list

- Do not change any channel modes
- Do not change `BacktestOrchestrator.cs` or any Web service (iter-14's territory)
- Do not change `BacktestReplayAdapter.cs`
- Do not change `BarEvaluationHandler.cs` (iter-13 already fixed DESIGN-07 there)
- Do not partially complete Phase A — if migration baseline fails, revert completely
- Do not change test files

---

## Handover notes

_(Implementing agent fills this section)_

### Verification results

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | |
| EF migration list shows `InitialSchema (Applied)` | |
| Domain project has no `EngineRunContext.cs` | |
| `RecomputeIndicatorsAsync` takes a `CancellationToken` | |
| `_strategies` is `IReadOnlyList` in EngineWorker | |
| `_processedExecutionIds.Remove` called in ClosePosition | |
| Unit tests (87 baseline) | |
| `ReplayBacktest` simulation test | |
| App starts without migration error | |

### Issues closed

| Issue ID | Status |
|----------|--------|
| AGENT-02 | |
| MIN-05 | |
| STD-01 | |
| MIN-06 | |
| STD-05 | |
| DESIGN-04 | |

### Anything that deviated from the plan

_(Especially: any issue with migration baseline that required fallback)_
