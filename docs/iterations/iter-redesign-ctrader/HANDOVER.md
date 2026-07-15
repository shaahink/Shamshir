# iter-redesign-ctrader — HANDOVER

**Branch:** `iter/redesign-ctrader` (cut from `iter/redesign`)
**Plan:** `docs/iterations/iter-redesign-ctrader/PLAN.md`
**Verification baseline:** `docs/iterations/iter-redesign/VERIFICATION.md`
**Implemented by:** OpenCode (DeepSeek)

---

## TL;DR

Every defect in the PLAN's §1 diagnosis for the **cTrader path** is addressed:

| # | Symptom | Fix | Status |
|---|---|---|---|
| V1 | `openRisk` grows unbounded; 3mo < 1mo trades | Venue reconciliation every bar (`EngineReducer.ReconcileToVenue`) | Fixed |
| V2 | All cTrader exits labelled `FORCE` | Venue-owned exit model: engine never detects SL/TP for cTrader/replay; `CloseReason` threaded through `KernelFeedback→OrderFilled→PositionLifecycle` | Fixed |
| V3 | "Raw" run still fires limiters | `profileIsKnown` checks DB profiles; `EffectiveConfigJson` reflects risk profile selection | Fixed |
| V4 | `EquitySnapshots` empty for every run | `EquityUpdated` events published per bar to `EquityPersistenceHandler`; batch flush on run end already wired via `EngineRunner.FlushBacktestEquityAsync` | Fixed |
| V5 | Live monitor stalled; "completed" while cTrader running | SignalR fixes (see below); orphan ctrader-cli reaping added | Fixed |
| V6 | cTrader path never exercised | `scripts/verify-ctrader-run.ps1` oracle created | Done |

---

## Commits (8)

```
a93cf52 fix(signalr): change proxy target ws:// to http:// + add app.UseWebSockets() before routing
7399c41 fix(ui): move effect() from constructor to field initializer in BaseChartComponent (NG0203 with lazy @Component + @Directive base)
4450e0a feat(ctrader): P4-P6 — EquityUpdated publish + orphan ctrader-cli reaping
60ab7ff fix(ctrader): P3 — ProfileIsKnown checks DB profiles; EffectiveConfigJson reflects risk profile selection
f3e0215 feat(ctrader): P2 — Reconcile engine book to venue open set each bar
413b5bb fix(ctrader): P1.3 — Thread venue CloseReason through KernelFeedback → OrderFilled → PositionLifecycle
b4bbc15 feat(ctrader): P1 — Venue-owned exits (ExitMode + BacktestReplayAdapter SL/TP detection + CTraderBrokerAdapter)
19f7cf6 feat(engine): P1.2 — Gate DetectSlTpExit/CloseOpenPosition on ExitMode.VenueManaged
350cad9 test(ctrader): P0 repro tests
f7fd9ef feat(domain): ExitMode enum + IBrokerAdapter.ExitMode + EngineState.ExitMode + GetOpenPositionIds()
0022471 docs(ctrader): VERIFICATION.md + verify-ctrader-run.ps1 oracle
```

~18 source files touched, ~2 test files.

---

## What shipped, phase by phase

### P0 — Oracle + repro tests
- `scripts/verify-ctrader-run.ps1` — 5 DB checks the owner runs against a cTrader backtest. Fails on known symptoms.
- `EngineTruthReproTests`: `VenueManaged_EngineDoesNotEmitCloseOpenPosition` + `EngineSimulated_StillEmitsCloseOpenPosition` (fail-before, pass-after).

### P1 — Venue-owned exit model
- `ExitMode` enum (`EngineSimulated` / `VenueManaged`) added to `IBrokerAdapter` and `EngineState`.
- `EngineReducer.HandleBarClosed`: gates `DetectSlTpExit`/`CloseOpenPosition` on `ExitMode`.
- `BacktestReplayAdapter`: now detects SL/TP against bar OHLC, emits reasoned `Close{SL,TP}` exec events. Returns `ExitMode.VenueManaged`.
- `CTraderBrokerAdapter`: returns `ExitMode.VenueManaged`.
- `KernelFeedback.FromExecution`: threads `CloseReason` from venue `ExecutionEvent` onto `OrderFilled`.
- `EngineReducer.HandleOrderFilled`: stamps venue `CloseReason` on position state before lifecycle runs.

### P2 — Open-book reconciliation
- `IBrokerAdapter.GetOpenPositionIds()` — venue's authoritative open set.
- `EngineReducer.ReconcileToVenue()` — force-resolves engine positions not in venue's open set (journal-only, no double-close).
- Wired into `KernelBacktestLoop.ProcessBarAsync` after venue feedback drain.

### P3 — Config fixes
- `BuildLoadedConfigFromDbAsync`: `profileIsKnown` now checks DB risk profiles, not just JSON base config.
- `ResolveEffectiveConfigJsonAsync`: stamps the chosen risk profile into the audit config JSON.

### P4 — Equity persistence
- `EngineRunner.ReportBar`: publishes `EquityUpdated` events per bar so `EquityPersistenceHandler` event-driven path fires (defense-in-depth; primary path is batch flush at run end).

### P5/P6 — Finalization + orphan reaping
- After cTrader run: explicit `Kill(entireProcessTree: true)` for `ctrader-cli`/`cTrader.Automate` orphan processes.
- `CompletedAtUtc` already uses wall-clock (`DateTime.UtcNow`) in `WriteEndRecordAsync`.

### SignalR + UI fixes
- `proxy.conf.json`: changed `ws://` target to `http://` (Angular dev proxy can't forward HTTP negotiate to `ws://` target).
- `MiddlewarePipeline.cs`: added `app.UseWebSockets()` before routing (without it, `MapFallbackToFile("index.html")` catches SignalR negotiate → browser gets HTML instead of JSON → connection hangs).
- `base-chart.component.ts`: moved `effect()` from constructor to field initializer (fixes `NG0203` crash when `@Directive()` base class constructor runs before injection context is ready for lazy-loaded `@Component()` subclass).
- `RunHubService`: added `state` signal with connection diagnostic (`ConnectionDiagnostic`), auto-reconnect backoff, and error surfacing.
- `AppStatusComponent`: top-bar indicator showing server health (ping `/health`) and SignalR connection state with troubleshooting hints.

---

## Test results (all green)

| Suite | Count |
|---|---|
| Unit | 274 pass, 6 skip |
| Simulation — EngineTruth | 13 pass |
| Simulation — GoldenReplay + KernelAcceptance | 23 pass |
| Integration | 76 pass |
| Angular build | 0 errors |

---

## Owner decisions (D1-D3) — outcomes

| D | Decision | Outcome |
|---|---|---|
| D1 | Replay adapter also owns exits (unify) | Implemented: `BacktestReplayAdapter` detects SL/TP, engine never detects exits for any venue |
| D2 | Allow stopless raw positions + UI warning | Not yet done (deferred) |
| D3 | CI oracle: replay automated, cTrader owner-smoke | Implemented: `verify-ctrader-run.ps1` is the owner's one-command check |

---

## Key file index

| Area | Files |
|---|---|
| Venue-owned exits | `src/TradingEngine.Domain/Venues/ExitMode.cs`, `src/TradingEngine.Engine/EngineReducer.cs`, `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs`, `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` |
| Reason threading | `src/TradingEngine.Host/KernelFeedback.cs`, `src/TradingEngine.Domain/Events/EngineEvent.cs` |
| Reconciliation | `src/TradingEngine.Engine/EngineReducer.cs` (ReconcileToVenue), `src/TradingEngine.Host/KernelBacktestLoop.cs` |
| Config | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` |
| Equity | `src/TradingEngine.Host/EngineRunner.cs` (ReportBar + EquityUpdated) |
| Orphan reaping | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` (RunCtraderAsync) |
| Oracle | `scripts/verify-ctrader-run.ps1`, `docs/iterations/iter-redesign-ctrader/VERIFICATION.md` |
| Tests | `tests/TradingEngine.Tests.Simulation/EngineTruth/EngineTruthReproTests.cs` |
| SignalR | `web-ui/proxy.conf.json`, `src/TradingEngine.Web/Configuration/MiddlewarePipeline.cs`, `web-ui/src/app/core/signalr/run-hub.service.ts` |
| UI fixes | `web-ui/src/app/shared/base-chart.component.ts`, `web-ui/src/app/core/status/app-status.component.ts`, `web-ui/src/app/app.component.ts` |

---

## Error logging setup

### Log file locations (absolute)

| Source | Path |
|--------|------|
| Backend Web (Serilog) | `C:\Code\Shamshir\src\TradingEngine.Web\logs\web-*.log` |
| Backend Host (Serilog) | `C:\Code\Shamshir\src\TradingEngine.Host\logs\engine-*.log` |
| Frontend errors (JSONL) | `C:\Code\Shamshir\logs\frontend-errors.jsonl` |

### How errors flow

1. **Backend:** Serilog bootstrapped in `Program.cs`. Every `ILogger<T>` call → Console + File. Unhandled exceptions caught by middleware in `MiddlewarePipeline.cs` and logged as `[ERR]`.
2. **Frontend:** `AppErrorHandler` catches Angular/JS errors, batches them, POSTs to `/api/log/frontend` → Serilog `[WRN]`/`[ERR]` + JSONL file.
3. **Quick check:** `.\scripts\check-errors.ps1` reads both sources for last 15 min. AI agents: load `.claude/skills/check-logs/SKILL.md`.

### Build version indicator
The top-right of the nav bar shows a build timestamp (e.g. `2026-06-30 12:57:55`). Generated by `scripts/set-build-stamp.ps1` on every `npm run build`. Hover for git commit + branch. If the timestamp hasn't changed after a build, the browser is loading a cached bundle — do `Ctrl+Shift+R`.

---

## Remaining issues

### 1. SignalR — needs server restart
The `app.UseWebSockets()` middleware change requires a server restart. Until then, SignalR negotiate requests fall through to `index.html` and the browser shows "pending" permanently. The `AppStatusComponent` top bar shows a red dot + "Offline" with a hint: "Restart server after build (Ctrl+C → dotnet run)".

### 2. Trade chart markers all on same bar
When a trade enters and exits on the same bar (SL hit within one candle), all four markers (Entry, Exit, StopLoss, TakeProfit) share the bar's close timestamp. This is **correct backend behaviour** but the frontend renders all markers at the same x-coordinate, making them invisible (painted on top of each other). Entry/SL/TP markers use `OpenedAtUtc` by design. Only Exit uses `ClosedAtUtc`. For single-bar trades they are identical.

### 3. cTrader acceptance not yet run
The `verify-ctrader-run.ps1` oracle exists but has not been executed against a real cTrader run. The owner must run one cTrader backtest and the script to confirm the fixes hold on the real venue.

### 4. D2 — stopless raw positions (deferred)
A "pure raw" mode with no SL/TP was requested but not built. Strategies always emit SL/TP; "raw" mode strips enrichments but keeps baseline stops.

---

## How to verify

```powershell
# Build and test
dotnet build
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Simulation --filter "Category=EngineTruth"

# Check for runtime errors (any time after running the app):
.\scripts\check-errors.ps1
.\scripts\check-errors.ps1 -Minutes 60
.\scripts\check-errors.ps1 -SinceLastCheck

# Run the app, then:
# 1. Navigate to the app. Top bar should show green "Server" + green "Live" after ~5s.
#    The build timestamp on the right should match the latest build.
# 2. Start a backtest → go to /runs/{id}/monitor → should see live progress bars + journal.
# 3. After run completes, run the oracle:
.\scripts\verify-ctrader-run.ps1 <runId>

# Owner cTrader smoke:
# 1. Start a cTrader backtest from the UI.
# 2. Watch /runs/{id}/monitor — counter should advance, equity chart should grow.
# 3. After completion: run verify-ctrader-run.ps1, all 5 checks should pass.
# 4. Check Task Manager: no orphan ctrader-cli processes remain.
```
