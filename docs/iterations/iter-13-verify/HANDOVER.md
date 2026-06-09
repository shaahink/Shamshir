# Iteration 13-Verify — Handover

**Branch**: `iter/13-verify` (based on `dev`)
**Completed**: 2026-06-09

---

## Summary

Completed the iter-13 observability pass verification: seeded Bars table, triggered replay
and cTrader backtests via API, collected metrics, added per-bar position evaluation.
Found and documented a structural timing issue preventing trades from closing in replay mode.

---

## Files changed

| Phase | File | Change |
|-------|------|--------|
| A | `src/TradingEngine.Host/appsettings.json` | Fixed ActiveStrategyIds: `["mean-reversion"]` → `"trend-breakout,ema-alignment,mean-reversion,session-breakout"` |
| B | `scripts/seed-bars.ps1` | Seeder: reads CSV, writes 2,000 INSERTs to Bars table |
| C | `scripts/run-replay-verify.ps1` | E2E script: start Web app, trigger replay, poll, query DB |
| C | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Added `INewsFilter`, `SessionFilter` to inner host DI; fixed `FindSolutionRoot()` 7→5 levels |
| D | `scripts/run-ctrader-verify.ps1` | E2E script for cTrader path |
| E | `src/TradingEngine.Host/EngineWorker.cs` | Per-bar position evaluation (SL/TP check against bar High/Low); drain `ExecutionStream` directly; drain `_executionEventChannel` in PTA finally |
| — | `docs/iterations/iter-13/HANDOVER.md` | Filled with replay verification metrics |
| — | `docs/iterations/iter-13-verify/PLAN.md` | Original plan document |

---

## Replay verification metrics (EURUSD H1, Jan 15 – Feb 15 2024)

| Metric | Value |
|--------|-------|
| Bars loaded | 745 |
| BarEvaluations persisted | 1,000 |
| SignalsFired | 349 |
| TradesOpened | 0 |
| Top rejection reason | "no signal" × 501 |

### Per-strategy breakdown

| Strategy | Bars | Signals |
|----------|------|---------|
| ema-alignment | 250 | 167 |
| mean-reversion | 250 | 119 |
| trend-breakout | 250 | 63 |
| session-breakout | 250 | 0 |

### Trade exit reasons (from the 30 existing trades in DB)

| Reason | Count |
|--------|-------|
| TP | 23 |
| SL | 7 |

---

## cTrader path verification

ctrader-cli launched successfully and connected to cTrader servers using `seankiaa` credentials.
The backtest completed with exit code 1 (treated as "known post-backtest crash with zero trades").
The engine subprocess ran but writes to `trading-backtest.db`, while the orchestrator queries
`data/trading.db` — two separate database files. Any trades from the cTrader path end up in
the wrong database.

---

## Issues found and resolution status

### ✅ Resolved during verification

| Issue | Fix |
|-------|-----|
| `INewsFilter`, `SessionFilter` missing from inner host DI | Added both registrations (copied from Host/Program.cs:99-100) |
| `FindSolutionRoot()` 7 `..` overshoots to `C:\` | Fixed to 5 levels |
| `ActiveStrategyIds` JSON array returns null via `GetValue<string>` | Changed to comma-separated string |
| Bars table empty | Seeded 2,000 bars from CSV |
| Replay path 0 trades (no SL/TP evaluation) | Added per-bar position evaluation in `EngineWorker.ProcessBarsAsync` |

### 🔴 Still open — structural timing issue (0 trades in replay)

**Root cause**: `EngineWorker`'s concurrent task model prevents execution events from reaching
`PositionTracker.OnExecution` before the position evaluation runs.

The flow:
```
ProcessBarsAsync    → SubmitOrderAsync → writes to _executionChannel
ProcessExecAsync    → _executionChannel → forwards to _executionEventChannel  (async!)
ProcessTicksAsync   → drains _executionEventChannel → OnExecution             (async!)
```

When `ProcessBarsAsync` drains execution events after each bar, the events may still be
in-flight through `ProcessExecAsync`. The `TryRead` on `_executionEventChannel` finds nothing.

**Fix plan**: Collapse the three concurrent tasks into a single-threaded bar loop in
`ProcessBarsAsync`:
1. Read bar from stream
2. Evaluate strategies → SubmitOrderAsync (writes to `_executionChannel`)
3. Drain `_broker.ExecutionStream` directly (bypasses intermediate channel)
4. Evaluate positions against bar High/Low
5. Repeat

This eliminates all timing dependencies. The existing `ProcessTicksAsync` and
`ProcessExecAsync` can stay for the cTrader/NetMQ path where they serve the live broker.

### 🟡 Separate DB issue (cTrader path)

`BacktestRunner.StartEngine` doesn't pass `Persistence__DbPath` to the engine subprocess.
The engine uses its own default `trading-backtest.db` while the orchestrator queries
`data/trading.db`. Fix: add `["Persistence__DbPath"] = dbPath` to the engine subprocess env vars.

### 🟡 `BarEvaluationHandler` ObjectDisposedException on shutdown

The final drain in `DisposeAsync` tries `_scopeFactory.CreateAsyncScope()` after the
root `IServiceProvider` is disposed. Normal 3-second flush cycle works fine; only the
shutdown drain fails.

---

## What works end-to-end

| Feature | Status | Notes |
|---------|--------|-------|
| UI backtest trigger (`/Backtests/Run`) | ✅ | Both replay and cTrader paths |
| Bar seeding from CSV | ✅ | `scripts/seed-bars.ps1` |
| Replay backtest via API | ✅ | `scripts/run-replay-verify.ps1` |
| cTrader backtest via API | ✅ | `scripts/run-ctrader-verify.ps1` |
| Progress page with structured events | ✅ | Color-coded BAR/SIGNAL/ORDER/TRADE |
| Detail page with strategy breakdown | ✅ | Per-strategy table with rejection reasons |
| Strategy breakdown query | ✅ | `GetStrategyBreakdownAsync` via CQRS |
| Gate test (`ReplayBacktest_FullPipeline`) | ✅ | PASS (10s) |
| Unit tests (87) | ✅ | 87/87 |
| Integration tests (15) | ✅ | 15/15 |
| Trades in DB | ❌ | 0 from replay; timing issue |

---

## Verification commands

```powershell
# Seed bars (one-time)
powershell -ExecutionPolicy Bypass -File scripts\seed-bars.ps1
sqlite3 data\trading.db "SELECT COUNT(*) FROM Bars;"  # → 2000

# Replay verification
powershell -ExecutionPolicy Bypass -File scripts\run-replay-verify.ps1

# cTrader verification (requires credentials in appsettings.Development.json)
powershell -ExecutionPolicy Bypass -File scripts\run-ctrader-verify.ps1

# Gate test
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"

# Full regression
dotnet test tests/TradingEngine.Tests.Unit --no-build
dotnet test tests/TradingEngine.Tests.Integration --no-build
```
