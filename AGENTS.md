# AGENTS.md — Session Startup Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/quant-model--p1-tf-agnostic` (active) / `develop` (authoritative, merged)
**Created:** 2026-06-18
**Updated:** 2026-07-07 (NEW ITERATION: iter-parity-pipeline — owner audit found critical venue-parity bugs; see RESUME block at the bottom)

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
8. **`docs/iterations/iter-parity-pipeline/AUDIT.md`** — CURRENT ITERATION: evidence audit (findings F1–F16, retrospective R1–R10)
9. **`docs/iterations/iter-parity-pipeline/PLAN.md`** — CURRENT ITERATION: phased plan P-0→P6 + session protocol (§10 is MANDATORY)
10. **`docs/iterations/iter-quant-model/PROGRESS.md`** — Previous iteration handover (historical context)
11. **`docs/audit/PROGRESS.md`** — Progress metrics, gate history, branch state
12. **`docs/QUANT-ROADMAP.md`** — Strategy calibration & experiment methodology
13. **For cTrader work:** load the `shamshir-ctrader` skill first — covers cBot, NetMQ, engine adapter, launch paths, cache
14. **`docs/RESOLVED-ISSUES.md`** — Audit trail of fixed issues (reference only)
15. **`docs/CTRADER-TEST-POLICY.md`** — cTrader test triage: which tests stay, which move to tape
16. **`docs/audit/RECONCILE-FINDINGS.md`** — Pre-registered fidelity gaps (F1–F5) + V4 run template

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

## RESUME (iter-parity-pipeline — replace this whole block each session)

**Branch:** `iter/parity-pipeline` · **HEAD after this session:** `a6aa08c` (+ conductor bookkeeping).
**Session (s2, P0):** QA of P0.0 = **confirmed** (gates re-run + 2 independent claims: R5 DB Method=Market,
golden 61/61). Then landed **P0.1 + P0.5** in one commit `a6aa08c`: fixed the ¼-sizing bug (F1) —
backtest now sizes off the CONFIG balance, never the cTrader hello (`EngineRunner.ResolveInitialBalance`,
pure + tested); cfg.Balance plumbed via EngineHostOptions→EngineRunContext→EngineRunner. Sizing DetailJson
now journaled (R7). New `VenueSizingParityTests` [Category=VenueParity] 5/5, NO creds: ×0.25 repro +
equal-lots-after-fix. P0.5 = the VenueParity tier rides the standard gate filter (Sim 139→144).

**Gates GREEN (commands):** `dotnet build TradingEngine.slnx -c Debug` → 0 err/5 warn; Unit `--no-build`
→ 508/0/6; fast Sim filter `RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ&Category!=CtraderContract`
→ 144/0/0; golden `FullyQualifiedName~Golden` → 61/61 byte-identical; Integration → 101/0/0.

**Next step:** **P0.2 (run-status truth, F5)** — separate engine-result from transport-teardown: if a
BacktestResult with stats exists, a teardown exception downgrades to `completed-with-warnings` (Q5) with a
new WarningsJson; `failed` only when no result. THEN reproduce the NetMQPoller disposal race (poller/queue
Dispose ordering — the committed B4 fix did NOT work); one observed repro before + one clean run after (R3).
See PLAN §3 P0.2 + AUDIT F5.

**Open traps:** (1) **P0.1 golden did NOT move** — RESUME/PLAN forecast a rebaseline; it was WRONG. The
committed golden-snapshot.json captures only (PhaseBefore,Event,GuardResult,Reason) from the OLD oracle,
never DetailJson. Do not do a phantom rebaseline. (2) P0.1 real paired cTrader mini-run is OWNER-PENDING
(needs creds) — mechanism proven credential-free; owner confirms equal DB lots. (3) `npx tsc --noEmit` has
2 PRE-EXISTING spec/e2e errors (P5). (4) `BuildInfo.g.cs` (cBot) regenerates every build → re-dirties
(committed generated metadata). (5) `.conductor/` is orchestrator-managed, untracked. (6) P2.2 OWNER-GATE.
