# AGENTS.md — Session Startup Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/parity-pipeline`
**Created:** 2026-06-18
**Updated:** 2026-07-09 (P7 Cleanup + Verification phase)

---

## Read this first (mandatory, in order)

At the start of every session:

1. **`docs/iterations/iter-parity-pipeline/TRACKER.md`** — Current state + handoff block
2. **`docs/workflows/shamshir-post-p6-workflow.md`** — THE WORKFLOW for this phase (8 sessions, protocols, rating system)
3. **`conductor-DEBT.md`** — Open debt items
4. **`docs/iterations/iter-parity-pipeline/PLAN.md`** — Master plan (P-0→P6 + verification matrix)
5. **`docs/iterations/iter-parity-pipeline/AUDIT.md`** — Evidence audit (F1-F16, R1-R10)
6. **`docs/reference/SYSTEM-REFERENCE.md`** — System overview
7. **`docs/reference/CODE-MAP.md`** — Feature→file index
8. **`docs/reference/BACKTEST-ARCHITECTURE.md`** — Venue backtest paths
9. **`docs/reference/TEST-ARCHITECTURE.md`** — Test tiers + harnesses
10. **`docs/WORKFLOW.md`** — Agent workflow rules, code standards
11. **`DECISIONS.md`** — All resolved decisions (D1-D96)
12. **`docs/audit/RECONCILE-FINDINGS.md`** — Fidelity gaps + run templates
13. **`docs/CTRADER-TEST-POLICY.md`** — cTrader test triage

**cTrader credentials are accessible to the agent.** The historic "needs creds" belief was from deadlock bugs (B1-B3, now fixed). Credentials: CtId=seankiaa, Account=5834367, PwdFile=ctrader.pwd. Session P7.2 proves this. See `docs/agents/ctrader-quickstart.md` after P7.2 completes.

## Build and test

```powershell
dotnet build                                 # Full build
dotnet test tests/TradingEngine.Tests.Unit   # Unit tests (504 pass)
dotnet test tests/TradingEngine.Tests.Simulation  # Simulation/FTMO tests
dotnet test tests/TradingEngine.Tests.Integration  # Integration tests (101)
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

## Current state (iter-parity-pipeline — NEW iteration, 2026-07-07)

The owner ran paired tape/cTrader backtests after iter-quant-model and kept the DB for audit. The
audit (`docs/iterations/iter-parity-pipeline/AUDIT.md`) found critical parity bugs the previous
iteration's gates never caught:

- **F1 (CRITICAL):** cTrader path sizes orders at exactly ¼ of tape risk for byte-identical proposals
- **F2 (CRITICAL):** cTrader entries fill one full decision bar later than tape, every trade
- **F5 (CRITICAL):** every cTrader run saved `failed` (NetMQPoller teardown) despite complete stats — the committed B4 fix did NOT work
- **F6 (CRITICAL):** a run journalled 12 proposals + 17 fills but persisted 0 TradeResults
- **F9:** the agent's LimitOffset switch never propagated — DB StrategyConfigs still Market; the F5 kernel fix was never exercised
- **F10:** two databases; Host CLI crashes on startup against the un-migrated root `data/trading.db`

The working tree is UNCOMMITTED (~24 modified + 3 new files) — land it per PLAN P-0, do not batch-commit.

## QA protocol (added 2026-07-09 — saves tokens on clean sessions)
- Skip previous-session QA when the last session ended `advanced` or `progress` with all gates green.
- Run full QA only when last session was `gatesRed`, `stalled`, `noProgress`, or `interrupted`.

## Tracker update rule
After every session, update BOTH the handoff block AND the checkpoint row in TRACKER.md. The row must show `DONE`, the commit hash, and evidence path. If only the handoff changes, Conductor re-reads the same TODO row and launches a duplicate session.

## What's next

See `docs/iterations/iter-parity-pipeline/PLAN.md`. Phase order: P-0 (land tree) → P0 (parity truth:
¼-sizing, status truth, trade-persistence barrier, latency instrumentation) → P1 (one DB + config
propagation) → P2 (run state machine + cTrader queue + compare-both first-class → the inherited P6.1
gate) → P3 (ResearchCli pipeline — the centerpiece) → P4 (labs) → P5 (UI truth) → P6 (wild list).

Owner decisions Q1–Q6 have locked defaults in PLAN §0 — read them before P-0 (Q1 reverts the 8
strategy JSONs to Market).

Inherited debts (tracked in PLAN, do not lose): `MISSING_DATA` verdict funnel, `ReferenceScales`
84-cell population (blocked by F10), `AddOnResolver.Ride` Calibrated, `VenueSessionEntity` audit
interface, M15 triage sweep, longer triage window for H4, P3.6 entry lab (full handover at
`docs/iterations/iter-quant-model/P3.6-HANDOVER.md`).

---

## P6 session bugs found + fixes (2026-07-06)

### B1 — Compare-both config deserialization ignores dates (FIXED)

**File:** `src/TradingEngine.Web/Api/RunsController.cs:211`
**Root cause:** `JsonSerializer.Deserialize<StartRunRequest>(json)` without `PropertyNameCaseInsensitive`.
The pinned config files use lowercase `"start"`/`"end"` but the DTO has `Start`/`End`. Default STJ is
case-sensitive → dates fall through to defaults (`2024-01-01`) where no data exists → "No bars found."
**Fix:** Added `new JsonSerializerOptions { PropertyNameCaseInsensitive = true }` to deserialization call.
**Verified:** Build succeeds. Not runtime-verified (compare-both never completed due to B3 below).

### B2 — cTrader stuck-running deadlock (FIXED, PARTIALLY VERIFIED)

**Files:**
- `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:110-255` (ReadSubLoop, ReadRouterLoop)
- `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:1290-1300` (RunEngineNetMqAsync finally block)

**Root cause:** `RunEngineNetMqAsync`'s `finally` block sequence after CLI exit:
1. `await adapter.BarStream.Completion;` → waits FOREVER
2. `await innerHost.StopAsync(CancellationToken.None);` → never reached
3. `await DisposeHostAsync(innerHost);` → never reached (this calls `DisconnectAsync` which completes channels)

Why: `BarStream.Completion` fires only when `_barChannel.Writer.Complete()` is called. That only
happens in `CTraderBrokerAdapter.DisconnectAsync()` (line 98). `DisconnectAsync` is only called via
`DisposeHostAsync` at step 3 — AFTER step 1. Classic circular deadlock.

**Evidence:** DB row `0db44736` had `CompletedAtUtc = 0001-01-01`, `ExitCode = -1` — the orchestrator
never wrote the end record. Tape leg (`1a696c1a`) completed normally (11 trades).

**Fixes applied (2 layers):**

1. **Adapter layer:** `ReadRouterLoop` now has a `finally` block that calls `_barChannel.Writer.TryComplete()`
   and `_execChannel.Writer.TryComplete()`. `ReadSubLoop` completes `_tickChannel` and `_accountChannel`.
   When the cTrader CLI exits, the transport disconnects, the read loops exit naturally, and the channels
   are marked complete — so `BarStream.Completion` fires without needing `DisconnectAsync` first.

2. **Orchestrator safety net:** `RunEngineNetMqAsync`'s `finally` block now wraps `BarStream.Completion`
   with a 30-second timeout. On timeout, forces `adapter.DisconnectAsync()` to unblock.

**To verify:** Run a standalone cTrader backtest (not compare-both) and confirm the run status reaches
"completed" or "failed" within the 30-min timeout (should complete in ~60-90s for a short window).
Previous stuck runs would hang indefinitely at "running."

### B3 — Compare-both recursive invocation (FIXED, NOT VERIFIED)

**File:** `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:795-844`

**Root cause:** `RunCompareBothAsync` creates the cTrader child config by copying the parent's
`CustomParams` dict. The parent has `["Compare"] = "both"` (set in `RunsController.CompareBoth`).
When `Start(ctraderCfg)` is called → `RunAsync` detects `compareBoth=true` → calls
`RunCompareBothAsync` recursively for the child. Meanwhile the outer `RunCompareBothAsync`
calls `RunEngineNetMqAsync` for the SAME runId. Two concurrent cTrader runs colliding.

**Evidence:** Error "The given key '71dc6285' was not present in the dictionary" at 5s into run —
the recursive call's tape runner or state manipulation collided with the outer call.

**Two-part fix:**

1. `ctraderCfg.CustomParams.Remove("Compare")` — prevents the child's `RunAsync` from detecting
   compare-both mode.

2. Manually register `ctraderState` via `_runs[ctraderRunId] = new BacktestRunState { RunId = ctraderRunId }`
   instead of calling `Start(ctraderCfg)`. `Start()` spawns `RunAsync` as a background task, which
   would see `Venue="ctrader"` and run a duplicate `RunEngineNetMqAsync`. The manual registration
   skips the background task — RunCompareBothAsync owns the cTrader leg lifecycle directly.

3. Also fixed: `tapeCfg = cfg` (shallow copy mutating original) → `cfg with { CustomParams = new Dict(cfg.CustomParams) { ["Venue"] = "tape" } }`.

---

## Session struggle log

1. **Windows PowerShell quoting:** `curl.exe` with JSON payloads fails with `Invalid start of property name`.
   Must use PowerShell `Invoke-RestMethod` with `$body = '{"key":"value"}'` (single quotes around JSON).

2. **App output noise:** EF Core Debug-level logging floods the terminal with query compilation trees
   (thousands of lines per request). Makes it impossible to see the actual run's progress log lines.
   **Recommendation:** Set `Logging:LogLevel:Microsoft.EntityFrameworkCore` to `Warning` in
   `appsettings.Development.json` when iterating on the API.

3. **Process killing on Windows:** `Get-Process -Name "dotnet" | ForEach-Object { Stop-Process -Id $_.Id -Force }`
   is the reliable pattern. CIM/WMI (`Get-CimInstance`) has PowerShell escaping issues with `$_.ProcessId`.

4. **Build-lock race:** When the app is running and `dotnet build` tries to overwrite output files,
   MSB3021/MSB3027 errors. Kill all dotnet processes first, then build.

5. **Compare-both untested end-to-end:** Despite 4+ fix-build-restart-test cycles, never got a
   successful compare-both run. The 3 bugs (B1, B2, B3) were progressively discovered and fixed.
   The fixes are structurally correct (each traced through the full call chain, not guessed) but
   require a clean end-to-end verify: start app, POST compare-both, poll to terminal, GET reconcile.

---

## cTrader CLI / background backtests — findings

**The deadlock problem:** When the cTrader CLI process exits, the engine's `ReadRouterLoop` ends but
the bar channel writer is never marked complete. The orchestrator's shutdown sequence awaits
`BarStream.Completion` before ever calling `DisconnectAsync` (which is the only place the channel
writer IS completed). Fix: B2 above completes channels in the read loop's `finally` block.

**Background cTrader runs:** The infrastructure already supports them — `RunAsync` is spawned via
`Task.Run`, and all runs share the same `_runs` dictionary. The UI polls `GET /api/runs/{id}` for
status. The stuck-run bug meant the cTrader leg never reached a terminal state, which blocked the UI
(no "stuck" detection). With B2 fixed, runs should complete or fail within the 30-min timeout.

**CLI hanging is NOT a CliWrap issue.** The `CTraderCli.BacktestAsync` call returns correctly when
the CLI process exits. The hang is purely in the engine-side shutdown sequence (B2). No CliWrap
changes needed.

**To improve reliability further (P6.3):**
- The 30-minute linked CTS timeout catches hung `BacktestAsync` calls (CLI stalls)
- The 30-second `BarStream.Completion` safety timeout catches hung channel completions
- Orphan process reaping already exists at lines 1301-1364

---

## Rules you must not break

1. `decimal` for all price, money, lot arithmetic
2. Never add infrastructure deps to `TradingEngine.Domain`
3. Schema changes via EF migrations only
4. No `Console.WriteLine` — Serilog message templates only
5. Don't touch `aspire/AppHost` (NU1903)
6. Keep Unit + Simulation + Integration suites green — stop-the-line on red
7. Golden must stay 63/63 byte-identical (kernel untouched)
8. When driving the web app: kill all dotnet processes before rebuilding (MSB3021 lock)
9. Use `Invoke-RestMethod` for API calls, not `curl.exe` (Windows quoting)
10. The app's cwd MUST be `src/TradingEngine.Web` so it finds `data/trading.db` and `wwwroot`

---

## RESUME (P7 Cleanup — overwrite this block each session)

**Phase:** P7 Cleanup + Verification — 8 sessions. P7.1 **DONE** (c830098). P7.2 **DONE** (60dfc7b, qa: 22d5822, s46).
**Branch:** `iter/parity-pipeline`
**P7.3 — Traps 3+1+2 (CURRENT):** triage-sweep playbook, session labels, SpreadVolNoTradeFilter wiring. ~45 min.
  cTrader credentials verified (see docs/agents/ctrader-quickstart.md).

### Session Plan

| # | Item | Effort | cTrader? | Status |
|---|------|--------|:--------:|--------|
| 1 | P4.1 live verification — exploration funnel + backfill | ~30m | No | **DONE** (c830098) |
| 2 | Prove cTrader works — HTTP backtest + quickstart doc | ~40m | ✅ | **DONE** (60dfc7b, qa: 22d5822) |
| 3 | Traps 3+1+2 — triage-sweep playbook + session labels + wiring | ~45m | No | **IN PROGRESS** |
| 4 | Traps 4+5+6 + P5.1 — bootstrapper fixes + status dedup | ~40m | No | TODO |
| 5 | P2.2 headline gate — compare-both run + reconcile verdict | ~60m | ✅ | TODO |
| 6 | F6-R economics recovery — Option A | ~40m | No | TODO |
| 7 | cTrader test audit — replaceable-with-tape analysis | ~30m | No | TODO |
| 8 | Final audit — rate all phases against PLAN.md + bugfix queue | ~45m | No | TODO |

### Quick report (for future agents)
Run these to get live status:
```powershell
Get-Content .conductor/state.json | ConvertFrom-Json | Select-Object status, currentStage, sessionCounter
Get-Content .conductor/conductor.log -Tail 20
Get-Content .conductor/REPORT.md 2>$null
```

**Full workflow:** `docs/workflows/shamshir-post-p6-workflow.md`
**Tracker:** `docs/iterations/iter-parity-pipeline/TRACKER.md`
**Baseline:** Unit 715/0/6 · Integration 120/0/0 · Sim-fast 144/0/0 · Golden 61/61

