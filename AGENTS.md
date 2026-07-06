# AGENTS.md — Session Startup Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/quant-model--p1-tf-agnostic` (active) / `develop` (authoritative, merged)
**Created:** 2026-06-18
**Updated:** 2026-07-06 (P6.1 in-progress: compare-both infrastructure bugs fixed, reconcile pending owner verification)

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
14. **`docs/audit/RECONCILE-FINDINGS.md`** — Pre-registered fidelity gaps (F1–F5) + V4 run template

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

## Current state (iter/quant-model--p1-tf-agnostic)

- P0–P5 delivered and gated. **P6 in progress** — compare-both infrastructure bugs fixed, reconcile pending owner verification.
- P4.5 (all 7 sub-phases + carry-forward cleanup) complete.
- **All gates green:** Unit 504/0/6, Integration 101/0, Simulation 127/0 byte-identical
- **Gate filter:** `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ&Category!=CtraderContract"`
- cTrader-backed tests are triaged: keep-set tagged `Category=CtraderContract`, 5 tests retired.
- Parent branch `iter/quant-model` has P0 (3 gated commits pushed to origin)

## What's next

See `docs/iterations/iter-quant-model/PLAN.md` §3 for the full iteration spec.
**Current phase: P6 — Oracle backstop (compare-both reconcile).** Three infrastructure bugs fixed;
next step is to drive a successful compare-both run and produce reconcile output.

- P6.1: Compare-both reconcile — **blocked on: verify 3 fixes work with a real compare-both API call.** Once the owner runs `POST /api/runs/compare-both` and it completes with both tape + cTrader trades, `GET /api/backtest/analytics/reconcile?left=...&right=...` produces the diff. Record findings in `RECONCILE-FINDINGS.md`.
- P6.2: Per-bar recorded spread — code not started, unblocked once P6.1 verify passes.
- P6.3: Weekly drift habit + reconcile-health — unblocked once P6.1 verify passes.
- P7: FTMO ops readiness — blocked on P6.1 evidence (MaxDD accuracy confirmation).

Carried-forward debts (not P6 blockers, but keep visible):
- `MISSING_DATA` verdict (P1.5.4) — zero hits repo-wide; deferred to verdict-funnel UI
- `ReferenceScales` population (P3.4b) — 14/84 cells populated; full population needs CLI invocation
- Kernel-path limit orders reach cTrader as Market (P2.7 carry-forward) — investigate in P6 reconcile
- `AddOnResolver.Ride` Calibrated — explicitly deferred (line 88 comment, "P3 slot")
- `VenueSessionEntity` missing `IAuditableEntity` — Architecture test pre-existing failure
- M15 triage excluded from P5.3 — needs dedicated sweep run
- 1-month triage window — insufficient for H4; needs 6m-1y re-run

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
