# Iteration 12 — Sign-Off & Handover

**Branch**: `iter/12-replay-ui-wire` (based on `phase/8b-bar-tracing` + iter-11 merge)
**Completed**: 2026-06-09

---

## Summary

Wired `BacktestReplayAdapter` (built in iter-11) into the UI backtest flow. The UI now
branches on `CTrader:UseForBacktest` config flag: `false` uses the in-process engine replay
(no cTrader credentials needed), `true` uses the existing ctrader-cli subprocess path.

Also fixed BUG-04 (fabricated max drawdown) and DESIGN-05 (failed runs leave no DB record).

---

## Files changed

| Phase | File | Change |
|-------|------|--------|
| A | `src/TradingEngine.Web/TradingEngine.Web.csproj` | Added project refs: Host, Services, Strategies, Risk |
| A | `src/TradingEngine.Domain/Interfaces/IBacktestRunRepository.cs` | Added `UpdateAsync` method |
| A | `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteBacktestRunRepository.cs` | Implemented `UpdateAsync` |
| A | `src/TradingEngine.Web/Program.cs` | Registered `IBarRepository` → `SqliteBarRepository` |
| B | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Full rewrite: branch on config, inner host replay, start/end records |
| C | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Fixed `GetTradeStatsAsync` max drawdown (peak-to-trough) |

---

## Issues fixed

| Issue | Root cause | Fix |
|-------|-----------|-----|
| OBS-04 | UI always used ctrader-cli, never the replay adapter | `RunAsync` branches on `CTrader:UseForBacktest` flag; `false` path starts engine in-process via `RunEngineReplayAsync` |
| DESIGN-05 | `SaveAsync` only called on success; failed runs left no DB record | `WriteStartRecordAsync` writes in-progress row (ExitCode=-1) at start; `WriteEndRecordAsync` updates on completion or failure |
| BUG-04 | `MaxDrawdownPct = abs(worst trade PnL) / 100_000` | Correct peak-to-trough from cumulative equity curve built from trades ordered by `ClosedAtUtc` |

---

## Verification results

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | 0 errors (5 pre-existing CTrader warnings) |
| Unit tests (87 baseline) | 87/87 |
| Integration tests | 15/15 |
| `ReplayBacktest_FullPipeline_ProducesBarEvaluations` (gate) | PASS (9s) |
| Simulation suite | 9/11 (2 pre-existing: FullBacktestPipelineTest needs CTrader env vars) |

---

## Key design decisions

### 1. Inner host shutdown pattern (from iter-11)
`RunEngineReplayAsync` follows the `ReplayTestHarness.RunAsync` pattern:
```csharp
await innerHost.StartAsync(cts.Token);
var adapter = innerHost.Services.GetRequiredService<IBrokerAdapter>();
await adapter.BarStream.Completion;     // all bars consumed
await Task.Delay(5_000, cts.Token);     // flush grace for BarEvaluationHandler
await innerHost.StopAsync(CancellationToken.None);
innerHost.Dispose();
```
This avoids the `WaitForShutdownAsync` hang that the plan's original code would cause
(detailed in iter-11 HANDOVER.md item 1).

### 2. Inner host DI mirroring
The inner host's DI registrations mirror `ReplayTestHarness.CreateAsync` (not `Host/Program.cs`).
`SymbolInfo` and `RiskProfile` are created directly rather than loaded from config files,
avoiding path resolution issues in the Web app's execution context.

### 3. Config branching
`CTrader:UseForBacktest` flag controls which path is used. When absent or `false`:
engine replay (credential-free, in-process). When `true`: ctrader-cli subprocess
(unchanged existing path).

### 4. Two-phase DB write
- Start: `WriteStartRecordAsync` via `SaveAsync` with ExitCode=-1
- End: `WriteEndRecordAsync` via `UpdateAsync` with final stats
This ensures every run (success or failure) appears in the UI.

### 5. Max drawdown calculation
Now uses cumulative equity curve: `peak = max(peak, equity)`, `dd = (peak - equity) / peak`.
This correctly captures multi-trade consecutive losses.

---

## Deviations from the plan

| Plan assumed | Actual | Fix |
|---|---|---|
- **Mapped `StrategyConfigEntry.Id` (not `.StrategyId`)** — plan used wrong property
- **Added `using TradingEngine.Risk;`** for `DrawdownTracker` and `RiskManager`
- **Replaced `WaitForShutdownAsync`** with `BarStream.Completion` + delay + `StopAsync` (see iter-11)
- **Inner host creates `SymbolInfo` directly** instead of loading from config JSON
- **`OBS-04` in plan context** refers to "UI always uses ctrader-cli" (Part 5 of OPEN-ISSUES.md).
  OPEN-ISSUES.md's own OBS-04 (equity curve) is a different, unfixed issue.

---

## iter-13 readiness

DEPENDS-ON notes from iter-13 plan need checking:
- `IConfiguration` injection in `BacktestOrchestrator` constructor — done
- `IBacktestRunRepository.UpdateAsync` exists — done
- `IBarRepository` registered in Web DI — done
- Gate test green — yes
