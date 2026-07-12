# AGENTS.md — Session Startup Guide

**Project:** Shamshir — Prop-firm algorithmic trading engine (.NET 10, C# 13)
**Branch:** `iter/alpha-loop`
**Created:** 2026-06-18
**Updated:** 2026-07-10 (iter-alpha-loop r0 — session setup)

---

## Read this first (mandatory, in order)

At the start of every session:

1. **`docs/iterations/iter-alpha-loop/TRACKER.md`** — Current state + handoff block
2. **`docs/WORKFLOW.md`** — Agent workflow rules, code standards (current active workflow)
3. **`conductor-DEBT.md`** — Open debt items
4. **`docs/iterations/iter-alpha-loop/PLAN.md`** — Master plan (R0→R5 + verification matrix)
5. **`docs/iterations/iter-parity-pipeline/AUDIT.md`** — Evidence audit (F1-F16, R1-R10) — historical
6. **`docs/reference/SYSTEM-REFERENCE.md`** — System overview
7. **`docs/reference/CODE-MAP.md`** — Feature→file index
8. **`docs/reference/BACKTEST-ARCHITECTURE.md`** — Venue backtest paths
9. **`docs/reference/TEST-ARCHITECTURE.md`** — Test tiers + harnesses
10. **`DECISIONS.md`** — All resolved decisions (D1-D97)
11. **`docs/audit/RECONCILE-FINDINGS.md`** — Fidelity gaps + run templates
12. **`docs/CTRADER-TEST-POLICY.md`** — cTrader test triage

**cTrader credentials are accessible to the agent.** The historic "needs creds" belief was from deadlock bugs (B1-B3, now fixed). Credentials: CtId=seankiaa, **Account=5857867**, PwdFile=ctrader.pwd. The cTrader path is functional. See `docs/agents/ctrader-quickstart.md`.

**Before any venue/parity investigation, read `docs/reference/INVESTIGATION-METHOD.md`.** It is normative,
not advisory. It exists because four consecutive sessions — all with green gates — fixed real bugs and
still missed the actual cause. Short version: *make the venue tell you what it did; do not infer it from
what happened afterwards.* Green credential-free gates (Unit/Integration/Sim-fast) **never** support a
claim about cTrader — none of them place an order at a venue.

**cTrader is the oracle; the tape is a mimic of it.** When they disagree, the tape is what changes —
unless the venue's behaviour is our own bug driving it (F38: we were feeding it stale bars).

**Account currency is one config value** (`Account:Currency`, default USD). It stamps every `SymbolInfo`,
decides which cross-rate legs a run loads, and is checked against the currency the venue declares — a
mismatch is reported on the run. Cross rates are a USD-pivot table fed from market data; a missing leg
fails the run rather than defaulting (a wrong cross rate is a wrong lot size).

## Build and test

```powershell
dotnet build                                 # Full build
dotnet test tests/TradingEngine.Tests.Unit   # Unit tests (716 pass, 6 skip)
dotnet test tests/TradingEngine.Tests.Simulation  # Simulation/FTMO tests (144 pass)
dotnet test tests/TradingEngine.Tests.Integration  # Integration tests (121 pass)
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

## Current state (iter/parity-pipeline — P0-P7 COMPLETE, 2026-07-10)

The parity-pipeline iteration is fully delivered. All P0-P6 phases completed, all 8 P7 sessions done.
The working tree was landed and squashed into `develop` (06adecf). 18 stale local branches deleted,
mdtape remote removed, docs cleaned and reconciled.

Key deliverables:
- **P0:** Parity truth — sizing (F1), status truth (F5), trade persistence (F6), entry latency (F2)
- **P1:** One database (F10) + config propagation (F9)
- **P2:** Run lifecycle state machine (F8) + compare-both
- **P3:** ResearchCli pipeline — HTTP driver, playbooks, exit lab, walk-forward
- **P4:** Exploration funnel (F11), MAE/MFE units doctrine (F12), reference scales
- **P5:** UI truth — equity (F13), start button (F15), compare-both visibility (F16)
- **P6:** Data quality sentinel, session fingerprinting, spread/vol filter, regime calibration, block bootstrap, meta-allocator, entry quality
- **P7:** Live verification, cTrader proof, traps 1-6, compare-both gate, F6-R economics, cTrader audit, final audit
- **A1:** F17 fix — kernel event persistence verified; docs cleaned up

## QA protocol (added 2026-07-09 — saves tokens on clean sessions)
- Skip previous-session QA when the last session ended `advanced` or `progress` with all gates green.
- Run full QA only when last session was `gatesRed`, `stalled`, `noProgress`, or `interrupted`.

## Tracker update rule
After every session, update BOTH the handoff block AND the checkpoint row in TRACKER.md. The row must show `DONE`, the commit hash, and evidence path. If only the handoff changes, Conductor re-reads the same TODO row and launches a duplicate session.

## What's next

See `docs/iterations/iter-alpha-loop/PLAN.md`. The next iteration is **iter-alpha-loop** —
closing the research loop with portfolio-level automation, entry lab productionization, and
regime-conditioned execution.

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

## Research integrity — the rules that were broken (added 2026-07-11)

Every rule below was written because it was violated in the R1/R2 sessions, and each violation
survived review because the session *sounded* rigorous. Read `docs/iterations/iter-alpha-loop/PARITY-TRUTH.md`
for the full autopsy. These are not style preferences — each one silently corrupted a result.

**R1. Units and signs before stories.** When two systems report different numbers, verify the units,
the sign convention, and the formula *before* proposing any explanation about the world. The R2 session
found tape swap `+5.40` vs cTrader `−246.20` and wrote a paragraph about the Bank of Canada's rate
differential. The truth was that the two venues store costs with opposite signs. **A domain narrative
that explains a discrepancy you have not yet traced to a line of code is a guess wearing a suit.**

**R2. A pre-registered gate is not negotiable by the agent that trips it.** R2's plan said: if trade
counts differ by >20%, STOP and escalate. Counts differed by 33%. The session ran three more window
variants until the number looked acceptable, then argued the threshold was "a function of window
length". If a gate fires: STOP, write down what fired, hand it to the owner. Changing the measurement
until the gate passes is the single most expensive thing you can do here, because the result *looks*
like progress.

**R3. Any claim about persisted state must carry the query that proves it.** The R1 scoreboard says
"248 below-floor cells: all have valid reasons recorded." `SELECT COUNT(*) FROM ExperimentRuns` returns
**4**. Do not write "persisted", "recorded", "scored", or "committed" without pasting the query and its
output into the evidence file. The PLAN's truth gates are SQL for exactly this reason — **run them, and
paste the result, even when you are sure.**

**R4. Never change the statistical unit without pre-registering it.** R1's plan was 252 cells. R1 ran
28 backtests with 9 strategies commingled in one account, then sliced them into "cells" — so the
strategies competed for one risk budget and 40% of every score came from a shared equity curve.
Batching that changes what a row *means* is a design change, not an optimisation. Flag it and stop.

**R5. Validate a model fix against the oracle with arithmetic, before you ship it.** The
`commissionPerMillion` change was never checked against a single cTrader number. One division would
have caught it: cTrader charged $54.36 on 15 XAUUSD trades; the new formula produces $0.006 per lot.
It is exact only for USD-base pairs, which is why it looked right on USDCAD. **If you "fix" a money
model, compute what it predicts for a trade you already have a venue number for, and compare.**

**R6. A surprising result is a bug report, not a footnote.** 248 of 252 cells failing a 20-trade floor
over ten months is not "the floor is restrictive" — it is nine strategies barely firing, and it needed
an investigation, not a bullet point. When the data surprises you, that is the finding. Chase it.

**R7. Fix the cause, don't scope the symptom away.** The `TRADES_PARTIALLY_UNRECONSTRUCTABLE` barrier
was scoped to `venue=ctrader` (`BacktestOrchestrator.cs:522`) instead of having its journal pairing
fixed — so now every cTrader run is stored with a warning and we have learned to ignore warnings.
Suppressing a signal is worse than the bug, because it costs you the signal too.

**R8. No hardcoded fudge factors in the money model, ever.** If the tape disagrees with cTrader, the
fix is to make the venue *declare* the number (D10) and correct our formula. Tuning a constant until
the two agree destroys the only oracle we have.

---

## RESUME (overwrite this block each session)

**Phase:** iter-alpha-loop — **P0, P1, P2, P3 all DONE (2026-07-11). P4 next.**
**Read first:** `docs/iterations/iter-alpha-loop/TRACKER.md`, then `PLAN.md` §3b (P4), then the
evidence files (`evidence/p1-symbol-specs.md`, `p2-limit-entry-parity.md`, `p3-exit-spread-parity.md`)
and `docs/reference/RESTING-ORDER-CONTRACT.md`.
**Tracker:** `docs/iterations/iter-alpha-loop/TRACKER.md`
**Branch:** `iter/alpha-loop`
**Gate baseline:** build 0err/5warn · Unit 728/0/6 · Integration 121/0/0 · Sim-fast 144/0/0 · golden clean
**What shipped:** D9 (cost sign), D10 (venue-declared specs), D11 (limit-entry default) all live.
QA + live cTrader compare-both verification (not just credential-free gates) found and fixed FOUR
live-only cross-venue defects this session — F24, F30, F31, F32 — each with the same "0 trades on
cTrader, N on tape" signature but a different root cause each time. See
[[feedback-ctrader-verification-recurring-failures]] equivalent in
`evidence/p1-symbol-specs.md` §8 for the pattern and a checklist — **any future phase touching
`CTraderBrokerAdapter`, `TradingEngineCBot.cs`, `SymbolInfoRegistry`, order-entry/expiry, or the
cost/spread model MUST run a live compare-both before being called done, not just the
credential-free gate battery.**
**Known open gaps (not blocking, filed for later):** F25 (VenueSymbolSpecs DB table never written —
in-memory only), F26 (PreTradeGate ignores CommissionType), F27 (no unit tests on notional
commission math), F28 (SwapCalculationType captured but unused), F29 (reconcile per-trade matcher's
5-min window too tight for real entry-latency, and doesn't compare entry price at all). The
`ctrader-e2e` xUnit harness's `CtraderE2EHarnessSmokeTests` currently fail with 0 trades — confirmed
PRE-EXISTING environmental cTrader Desktop CLI bug (reproduced on pre-P0 baseline), not a regression;
do not spend time "fixing" this without first re-confirming it's still broken (may be a transient
cTrader auto-update issue).
**Next:** **P4 — parity as a permanent gate** [OWNER GATE after]. `research parity` verb + the
pre-registered tolerance budget (PLAN.md table: trade count exact, entry price ≤1 tick, lots exact,
exit price ≤1 tick/95%, commission ≤2%, swap ≤5%, net PnL ≤1% of gross). Fix F29 first (or the
verb can't actually measure entry-price tolerance). Then R1' (one cell per run, needs P4 green).


