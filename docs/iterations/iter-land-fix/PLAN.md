# iter-land-fix — Land the unfixed, verify the unverified, then build the deferred

**Written:** 2026-07-09 — deep research across all iter-parity-pipeline deliverables + source code.
**Predecessor plan:** `docs/iterations/iter-parity-pipeline/PLAN.md` (P-0 through P6, P7 cleanup).
**Branch:** `iter/parity-pipeline` (HEAD: `877c120`).

**Gate baseline at plan start:**
- `dotnet build TradingEngine.slnx` — 0 errors, 5 pre-existing net6.0 warnings
- Unit: 716 pass, 0 fail, 6 skip
- Integration: 121 pass, 0 fail, 0 skip
- Sim-fast (`RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ`): 144 pass, 0 fail, 0 skip
- Golden: `git diff --stat -- **/*golden*.json` = empty

---

## 0. What was delivered in iter-parity-pipeline

Per `docs/qa-reports/FINAL-AUDIT.md` (P7.8):

| Phase | Rating | What was built |
|-------|--------|---------------|
| P-0 | CONFORMS | Landed the dirty tree. 3 deliberate commits. Q1 revert (JSONs to Market). |
| P0.1-P0.5 | CONFORMS | F1 ¼-sizing fix. F5 completed-with-warnings. F6 trade persistence barrier. F2 entry-latency instrumentation. VenueSizingParityTests 5/5. |
| P1.1-P1.2 | CONFORMS | One DB. 84/84 ReferenceScales. ConfigSyncService with hash-based drift detection. |
| P2.1 | CONFORMS | RunStateMachine 32/32. Cancel + watchdog + orphan-kill. Single writer. |
| P2.2 | CONFORMS-WITH-FINDINGS | Gate exercised via independent runs (F18 blocked compare-both). F1 sizing gate blocked by F17. |
| P3.1-P3.3 | CONFORMS | ResearchCli 36/36. Playbook engine 15/0. UI research page. |
| P3.4 | CONFORMS-WITH-FINDINGS | 11 playbooks shipped. walk-forward.json missing. CLI gates blocked by F17/F18. |
| P4.1 | CONFORMS | Exploration funnel + MAE/MFE backfill 84/84 (P7.1). P3.6 deferred per D97. |
| P5.1 | CONFORMS | F13-F16 fixed. Status chips. Signals migration partially done. |
| P6.1-P6.8 | CONFORMS | All 8 wild-list features code-complete + playbooked. Zero end-to-end runs. |

**Two blockers hold everything back:**
- **F17 (CRITICAL):** Tape/replay venue produces 0 trades for any backtest run
- **F18 (HIGH):** Compare-both flow doesn't spawn cTrader children

Everything below P2.2 — playbook CLI gates, exit-lab funnel, P6 wild-list validation, the deferred P3.6 Entry-Tactic Lab — is code-complete but runtime-untested because the tape venue produces no data and the compare-both flow is broken.

---

## 1. Deep root cause: why both regressions exist

### F17 — Tape zero-trade (4-layer cascade)

**Layer 1:** Commit `c1d67c9` (Jul 3, pre-P-0) changed `OrderEntryOptions.Method` C# default from `OrderEntryMethod.Market` to `OrderEntryMethod.LimitOffset` with `LimitOffsetPips = 2.0`. This was part of a blanket LimitOffset rollout. The Q1 revert (P-0 commit `9570ad6`) reverted the 8 JSON files to `"method":"Market"` — but the C# default was never reverted.

**Layer 2:** Strategy factories use `entry.OrderEntry ?? new()`. When the DB column `OrderEntryJson` is null/empty, `DeserializeOptional` returns null, and `new()` creates LimitOffset with 2-pip offset.

**Layer 3:** `StrategyConfigSeeder` (line 44) bails out entirely when any DB entries exist. If the DB was seeded during the LimitOffset window (between `c1d67c9` and `9570ad6`), those stale entries persist. `ConfigSyncService` (P1.2) detects JSON→DB drift at startup, but only if the app has been restarted since the ConfigSyncService deployment.

**Layer 4:** `EntryPlanner.Plan()` converts Market intents to Limit orders. Both `BacktestReplayAdapter` and `TapeReplayAdapter` queue limits with `BarsRemaining = 3`. Most limits expire unfilled → `ENTRY_EXPIRED` → zero fills.

**Why cTrader survives:** The cTrader platform has its own order lifecycle. Limit orders in cTrader live longer or fill at different thresholds, producing 2-8 trades vs. tape's 0.

**Evidence:** P7.5 session — May 1-8 EURUSD H1: tape run `7479593e` = 0 trades, cTrader `d5de5628` = 8 trades.

**Files:** `src/TradingEngine.Domain/Trading/OrderEntryOptions.cs:5-6`, `src/TradingEngine.Strategies/TrendBreakout/TrendBreakoutStrategy.cs:182`, `src/TradingEngine.Infrastructure/Configuration/StrategyConfigSeeder.cs:44-48`, `src/TradingEngine.Services/Helpers/EntryPlanner.cs:16-23`, `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:190-203,260-292`, `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs:309-320,425-456`

### F18 — Compare-both flow regression

**Root cause:** The B1-B3 deadlock fixes from the P6 session (which originally got compare-both working) were likely regressed by the P2.1 RunStateMachine refactor. `TransitionRun` replaced direct property writes on run status. The cTrader child's state is removed from `_runs` in the `finally` block, destroying post-mortem evidence.

**Evidence:** P7.5 session — two compare-both attempts produced tape leg only, zero cTrader children. Runs `9673d15a` and `b2b29376` both had tape leg complete in 70-87s with no cTrader child.

**File:** `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:955-1048`

### Conductor thrashing (process debt, not code)

The Conductor log (820 lines) shows 7+ sessions wasted on re-launching already-completed stages. Sessions 37-46 all targeted P7.2 after it was already done. Sessions 48-49, 51, 56-57 targeted P7.3 after it was done. **Root cause:** `state.json` was not updated alongside `TRACKER.md` handoff blocks.

---

## 2. Gap inventory — what awaits

| ID | Source | Description | Blocked by | Unblocks at |
|:--:|--------|-------------|:----------:|:-----------:|
| F17 | AUDIT §P2.2 | Tape/replay zero-trade | — | A1 |
| F18 | AUDIT §P2.2 | Compare-both broken | — | A2 |
| G1 | PLAN §5 P2.2 | cTrader queue (Q2: serialize runs) | A2 | B1 |
| G2 | PLAN §5 P2.2 | Auto-reconcile on parent completion | A2 | B1 |
| G3 | PLAN §6 P3.4 | walk-forward.json playbook | A1 (tape) | B1 |
| G4 | PLAN §6 P3.4 | WalkForward equity endpoint (returns []) | — | B1 |
| G5 | PLAN §8 P5 | OnPush+inject() on all routes | — | B1 |
| G6 | PLAN §8 P5 | Global HTTP error toast | — | B1 |
| G7 | PLAN §9 P6 | All 11 playbooks end-to-end verified | A1 (tape) | B1 |
| G8 | D97 | P3.6 Entry-Tactic Lab (32 files mapped) | A2 (reconcile) | C1, C2 |
| G9 | AUDIT §F3 | Trailing/breakeven cadence | — | D1 |
| G10 | AUDIT §F4 | Gap-through fills | — | D1 |
| G11 | PLAN §3 P0.4 | Q4 M1-cadence cBot fix | — | D1 (deferred) |

---

## 3. Phase map (dependency order)

```
A1 (F17 — tape zero-trade)
  │
  ├─► A2 (F18 + P2.2 gate — compare-both fix + headline gate re-run)
  │
  ├─► B1 (pipeline landing — queue, auto-reconcile, walk-forward, P5.1 gaps, P6 playbooks)
  │
  ├─► C1 (P3.6 data model + recording hooks — 15 files)
  │      │
  │      └─► C2 (P3.6 counterfactuals + API + UI — evaluator + Angular page)
  │
  └─► D1 (final audit + fidelity gaps — F3/F4 fix, full battery, bugfix queue)
```

A1 and A2 are strict prerequisites. After A2, B1, C1, and D1 can proceed. C2 depends on C1. D1 is last.

---

## 4. Stage A1 — Fix F17: tape/replay zero-trade regression

**Effort:** 60 min · **cTrader:** No · **Risk:** Medium

### Pre-read (mandatory, in order)

1. `src/TradingEngine.Domain/Trading/OrderEntryOptions.cs:5-6` — the C# default
2. `src/TradingEngine.Strategies/TrendBreakout/TrendBreakoutStrategy.cs:182` — `entry.OrderEntry ?? new()`
3. `src/TradingEngine.Infrastructure/Configuration/StrategyConfigSeeder.cs:44-48` — one-shot seed guard
4. `src/TradingEngine.Services/Helpers/EntryPlanner.cs:16-23` — Market→Limit conversion path
5. `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:190-203` — limit queuing
6. `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:260-292` — 3-bar expiry
7. `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs:309-320,425-456` — same logic
8. Original PLAN.md §3 P0.1 (¼-sizing fix), §4 P1.2 (config propagation)

### Background

The P0 audit runs (`2cdba11a`, `2c9551d1`, `020fd4eb`) produced 3–28 trades on the tape venue. Current HEAD produces 0. The 4-layer cascade above explains the mechanism. The fix has three parts: revert the C# default (defense-in-depth), verify the DB is consistent (runtime truth), and add a diagnostic log for all future sessions.

### Discipline

**Verify bug exists first:** Query the DB, run a live tape backtest, confirm 0 trades. Do NOT fix blind.

**Create a failing test:** New xUnit test in `VenueSizingParityTests` (or new `TapeVenueSmokeTests`): run `KernelBacktestLoop` over synthetic H1 bars with default-Market config, assert `TradeResults.Count > 0`. This should FAIL at HEAD because the null→default path leaks LimitOffset.

**Then fix:** Revert default, add diagnostic log, force DB sync. Re-run failing test → passes.

### Steps

**Diagnose (10 min):**
1. Query DB: `sqlite3 src/TradingEngine.Web/data/trading.db "SELECT Id, json_extract(OrderEntryJson, '$.method') as Method, OrderEntryJson FROM StrategyConfigs"`
2. Record the Method values. Null/empty = immediate cause. LimitOffset = ConfigSyncService didn't run.
3. Start web app, run tape backtest (EURUSD H1, Jan 15-18, 2026, 3 days): `POST /api/runs` with `"venue":"tape"`
4. Poll to terminal, confirm `TotalTrades = 0` in DB. If >0, re-diagnose.

**Test (10 min):**
- `src/TradingEngine.Domain/Trading/OrderEntryOptions.cs` — add a new fact in the simulation suite: construct a strategy config with null `OrderEntry`, run a mini backtest loop, assert trades exist. This test must FAIL at HEAD.

**Fix (30 min):**
1. `OrderEntryOptions.cs:5` — change `OrderEntryMethod.LimitOffset` back to `OrderEntryMethod.Market`
2. `StrategyConfigSeeder.cs` or a startup diagnostic — add `_logger.LogInformation("Resolved {Strategy} entry method = {Method}", ...)` listing each strategy's resolved method
3. If DB has stale entries: restart the web app so ConfigSyncService detects drift and updates the DB
4. If DB has null entries: find the seeding gap and ensure `OrderEntryJson` is always populated

**Verify (10 min):**
1. Rebuild + re-run the failing test → passes (TotalTrades > 0)
2. Run a live tape backtest (EURUSD H1, 1 week, Jan 15-22) → `TotalTrades > 0` in DB
3. DB query shows `"method":"Market"` for all 9 strategies

### Gate

**Command + expected:**
```
# Failing test now passes
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~TapeVenueSmoke"

# Live tape run produces trades
# DB: SELECT TotalTrades FROM BacktestRuns WHERE Id='{runId}' — result > 0

# DB: all 9 strategies have Market entry
sqlite3 src/TradingEngine.Web/data/trading.db "SELECT COUNT(*) FROM StrategyConfigs WHERE json_extract(OrderEntryJson, '$.method') = 'Market'" — result = 9
```

### Traps

- **If TotalTrades is still 0 after default revert:** the root cause is in a different layer. Add logging to `BarEvaluator.EvaluateAsync` to list which strategies fired signals and which were filtered. Check `MISSING_DATA` verdict for multi-TF strategies, `EntryFilter` (SpreadVolNoTradeFilter), or `BuildBarSnapshot` null return.
- **Build-lock:** kill all dotnet processes before rebuilding. The web app must be killed before `dotnet build`.
- **Do NOT start the web app and leave it running** across sessions.

### Commit format

`fix(F17): revert OrderEntryOptions default to Market; add startup method diagnostic`

Body: paste gate output (test results + DB query).

---

## 5. Stage A2 — Fix F18 + rerun P2.2 headline gate

**Effort:** 90 min · **cTrader:** Yes · **Risk:** Medium (cTrader reliability, P2.2 gate weight)

### Pre-read (mandatory, in order)

1. `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:955-1048` — `RunCompareBothAsync`
2. AGENTS.md §B1-B3 — deadlock fix details from P6 session
3. `src/TradingEngine.Engine/RunStateMachine.cs` — TransitionRun guarded transitions
4. Original PLAN.md §5 P2.2 (gate spec — what must be proven)
5. `docs/audit/RECONCILE-FINDINGS.md` — V4 template at bottom, F1-F6 gate definitions
6. `docs/agents/ctrader-quickstart.md` — credentials, startup, polling pattern

### Background

The P2.2 headline gate is the single most important gate in iter-parity-pipeline. It proves: equal sizing (F1), truthful status (F5), trade persistence (F6), entry-latency instrumentation (F2), and the tape venue produces trades (F17). The P7.5 workaround (independent paired runs) scored PASS-WITH-FINDINGS because F17 blocked sizing parity and F18 blocked the compare-both flow. This session fixes F18, then re-runs the gate properly with a real compare-both.

### Steps

**Verify F18 exists (5 min):**
1. Kill all dotnet. Build + start web app.
2. POST `/api/runs/compare-both` with 3-day window: `{ "start": "2026-01-15", "end": "2026-01-18", "symbols": ["EURUSD"], "periods": ["H1"], "balance": 100000 }`
3. Poll `GET /api/runs` — confirm only tape child exists, no cTrader child.

**Fix F18 (25 min):**
1. Trace `RunCompareBothAsync` for the cTrader child path. Add explicit try/catch with `_logger.LogError` around the child launch block (lines 1007-1024).
2. Verify B3 fix still applied: child uses `_runs[ctraderRunId] = new BacktestRunState { RunId = ctraderRunId, ... }` and NOT `Start(ctraderCfg)`.
3. Verify `ctraderCfg.CustomParams.Remove("Compare")` (prevents recursive invocation).
4. Check if `TransitionRun(ctraderState, RunStatus.Running)` rejects the child because it was registered at `Running` directly (expects `Queued → Starting → Running`). If so, either: (a) register at `Queued` and call `TransitionRun` through the chain, or (b) bypass `TransitionRun` for the initial child setup.
5. Move child-state cleanup AFTER error logging in the `finally` block (or add a `_runs.TryRemove` guard that checks if the run has an end record before removing).
6. Rebuild + restart. Re-run compare-both (3-day). Both children must appear and complete.

**P2.2 gate — full compare-both (60 min):**
1. Configure: EURUSD H1, May 1 – Jun 1, 2026, all 9 strategies, balance=100000, profile=standard.
   ```powershell
   Invoke-RestMethod -Uri "http://localhost:5000/api/runs/compare-both" -Method Post -Body '{"start":"2026-05-01","end":"2026-06-01","symbols":["EURUSD"],"periods":["H1"],"balance":100000}' -ContentType "application/json"
   ```
2. Poll both run IDs. **Provide heartbeat every 30s:** `"Polling {id}... status=running, bars=N/M"`. cTrader leg takes 2-5 min for 1-month window. The 30-min linked CTS timeout is the safety net.
3. After both complete, run reconcile:
   ```
   GET /api/backtest/analytics/reconcile?left={tapeRunId}&right={ctraderRunId}
   ```
4. Verify each gate:

| Gate | Check | How |
|------|-------|-----|
| F17 | Tape TotalTrades > 0 | `SELECT TotalTrades FROM BacktestRuns WHERE Id='{tapeId}'` |
| F1 | Lots equal for matched proposals | `SELECT o.OrderId, t.Lots, c.Lots FROM TradeResults t JOIN TradeResults c ON t.OrderProposedId=c.OrderProposedId WHERE t.RunId='{tapeId}' AND c.RunId='{ctraderId}'` |
| F5 | Both runs end completed/completed-with-warnings | No `failed` status. No NetMQPoller in ErrorMessage. |
| F6 | No TRADES_LOST warnings on clean runs | `SELECT WarningsJson FROM BacktestRuns` — null or empty. |
| F2 | entryDelayBars in reconcile output | Reconcile response has `leftLatency` + `rightLatency`. |
| Lifecycle | Both runs terminal, no orphans | Both have non-null CompletedAtUtc. |

5. Fill V4 template in `docs/audit/RECONCILE-FINDINGS.md` with live-run data.
6. Update TRACKER.md: flip P2.2 from "OWNER-PENDING" to "DONE" with commit hash + evidence path.

### Gate

**Command + expected:**
```
# Compare-both produces tape + cTrader children, both terminal
# tape TotalTrades > 0
# Tape+CTrader matched lots equality (within rounding)
# Both statuses: completed or completed-with-warnings (not failed)
# RECONCILE-FINDINGS.md V4 template filled with live data
# TRACKER.md P2.2 row = DONE
```

### Traps

- **Heartbeat:** The 12-min Conductor stall timeout must be avoided. Output a status line every 30s during cTrader polling.
- **cTrader credentials:** in `appsettings.Development.json` — CtId=seankiaa, Account=5834367, PwdFile path read from config. The "needs creds" historic belief is outdated. `ctrader-quickstart.md` has full instructions.
- **30-min linked CTS:** If the cTrader CLI hangs, the engine will complete-with-warnings with `BAR_STREAM_TIMEOUT`. This is acceptable — the gate just verifies it surfaces correctly.
- **Golden:** no kernel changes in this session — golden stays clean.

### Commit format

`fix(F18): repair compare-both child cTrader registration in RunCompareBothAsync; gate(P2.2): headline gate verdict — EURUSD H1 May-2026`

Body: trace of failure point, fix description, reconcile output, V4 template filling.

---

## 6. Stage B1 — Pipeline completion + UI gaps + P6 playbook validation

**Effort:** 90 min · **cTrader:** No (cTrader is proven working, not exercised) · **Risk:** Low

### Pre-read (mandatory, in order)

1. Original PLAN.md §5 P2.2 (queue + auto-reconcile spec)
2. Original PLAN.md §6 P3.4 (canonical playbooks, gate: end-to-end via CLI)
3. Original PLAN.md §8 P5 (UI truth + refactor)
4. Original PLAN.md §9 P6 (wild list — all 8 sub-features are code-complete)
5. `playbooks/` directory — verify 11 JSONs parse
6. `src/TradingEngine.Web/Api/WalkForwardController.cs:106-110` — equity stub
7. `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:1026-1027` — the manual reconcile log line

### Background

Five backlog items are unblocked now that F17 (tape trades) and F18 (compare-both) are fixed. All are incremental — none require new architecture. The cTrader queue and auto-reconcile complete the P2.2 spec. The walk-forward playbook + equity endpoint close P3.4. The P5.1 gaps are cosmetic but affect developer experience. The P6 playbook validation run finally exercises all wild-list features end-to-end.

### Steps

**A. cTrader queue (20 min):**
1. Add `SemaphoreSlim(1, 1)` to `BacktestOrchestrator` as `_ctraderSemaphore`.
2. In `RunAsync`: if venue is `ctrader`, `await _ctraderSemaphore.WaitAsync(linkedCts)`. If held, status = `queued`. On completion, release in finally.
3. The status `queued` already exists from P2.1 — verify it displays in `GET /api/runs`.
4. Test: start two cTrader runs simultaneously → second shows `queued` → completes after first.

**B. Auto-reconcile (15 min):**
1. In `RunCompareBothAsync`, after both legs complete, call `LedgerReconcileService.BuildEngineLedgerAsync()` for both runs, then `LedgerReconciler.Compare(tapeLedger, ctraderLedger)`.
2. Store the serialized `ReconcileJson` on the parent (tape) run entity.
3. Test: run compare-both → parent run's `ReconcileJson` populated in DB.

**C. walk-forward.json + equity endpoint (20 min):**
1. Create `playbooks/walk-forward.json` — per-cell walk-forward with train+test windows and test-leg verdicts. Pattern-match from `playbooks/triage-sweep.json` (sweep over cells) and `playbooks/venue-parity.json` (paired runs with reconcile). Include steps: ensure-data → start-run (train) → await → start-run (test) → reconcile → assert-gates.
2. Fix `WalkForwardController.GetEquity`: read constituent runs from the walk-forward job, query `TradeResults` or `TradeExcursions` for equity points, return real data.

**D. P5.1 UI gaps (15 min):**
1. Audit remaining Angular routes for `ChangeDetectionStrategy.OnPush` + `inject()` pattern. Convert any that still use constructor DI + `Default` detection. Focus on: `/research`, `/scoreboard`, `/exit-lab`, `/runs` (any pages not already done).
2. Add global HTTP error interceptor: catch 4xx/5xx responses, emit toast notification. Verify with a faked 500 response in devtools or a bad API call.
3. Smoke-test Scoreboard + ExitLab empty-states: they should show explanatory text or empty data tables, not crash.

**E. P6 playbook validation (20 min):**
1. Start web app. For each playbook in `playbooks/`:
   ```
   dotnet run --project src/TradingEngine.ResearchCli -- pipeline run playbooks/{name}.json
   ```
2. Record the VERDICT line from each. Goal: ≥9 of 11 produce `VERDICT: PASS`.
3. Any FAIL — document the failure: which step failed, what the error was, whether it's a playbook bug or a code bug. Fix playbook bugs in-session (they're JSON configuration, not code). Code bugs become new F-ids for the audit session.

### Gate

**Command + expected:**
```
# cTrader queue: two simultaneous cTrader POSTs → second shows queued status
# Auto-reconcile: compare-both parent has ReconcileJson column populated
# walk-forward.json: exists in playbooks/ + ShippedPlaybook_Parses test includes it
# WalkForward equity: GET /api/walkforward/jobs/{id}/equity → {points: [...]} (non-empty)
# tsc clean: npx tsc --noEmit (0 errors)
# P6 playbooks: ≥9/11 VERDICT:PASS
```

### Traps

- Playbook runs each spawn a tape backtest (10-30s per). Tape runs are parallel — batch them. Conductor's stall timeout applies to the session overall, not individual commands.
- The error toast should use Angular's `HttpInterceptorFn` pattern in `web-ui/src/app/` — check if an interceptor already exists and extend it, rather than creating a new one.
- Playbook failure triage should not blow the session budget. Document failures with root cause; fix only playbook JSON (no code changes unless trivial).

### Commit format

`feat(B1): cTrader run queue + auto-reconcile + walk-forward.json + WalkForward equity; refactor(P5.1): OnPush/inject consistency + error toast; gate(P6): 9/11 playbooks VERDICT:PASS`

Body: per-item verification output.

---

## 7. Stage C1 — P3.6 Entry-Tactic Lab: data model + recording hooks

**Effort:** 120 min · **cTrader:** No · **Risk:** High (5 engine seams, golden must stay clean)

### Pre-read (mandatory, in order)

1. `docs/iterations/iter-quant-model/P3.6-HANDOVER.md` — §1 spec, §2 Phase A+B, §3 Q1-Q5, §5 test plan
2. `DECISIONS.md` — D97 (deferral rationale: "unblock when P6.1 reconcile confirms trade count + entry timing fidelity")
3. `src/TradingEngine.Domain/OrderProposed.cs` — the proposal record that gets persisted
4. `src/TradingEngine.Host/BarEvaluator.cs:107-240` — where proposals are built (line 231 = recording point)
5. `src/TradingEngine.Engine/Kernel/Kernel.cs:44-52` — gate reject path
6. `src/TradingEngine.Host/EffectExecutor.cs:59-196` — effect dispatch: gate@104, fills@111, PublishTradeClosed@126
7. `src/TradingEngine.Host/KernelBacktestLoop.cs:355-410` — PumpAsync execution loop (where venue expiries flow)
8. `src/TradingEngine.Infrastructure/Persistence/TradePersistenceHandler.cs` — pattern to copy exactly
9. `src/TradingEngine.Infrastructure/Persistence/TradingDbContext.cs` — where to add DbSet

### Background

P3.6 (Entry-Tactic Lab) is the capstone deferred feature from iter-quant-model. The owner deferred it via D97 (2026-07-07): "not confident about engine/backtest hook-point mechanics; wants P6.1 compare-both reconcile evidence before building instrumentation that sits inside these paths." The P6.1 gate was closed in A2 — the deferral condition is met.

The feature answers: *"Should this strategy/cell enter Market, Limit@0.25×ATR, Limit@0.5×ATR, or StopConfirm?"* by recording every proposal's fate (gated, filled, expired-unfilled) and computing per-tactic fill-rate, expectancy, and counterfactual R.

**Four recording points** (all verified by direct code-trace in the handover):

| # | Hook point | File | What it records |
|---|-----------|------|----------------|
| 1 | BarEvaluator.EvaluateAsync:231 | `BarEvaluator.cs` | Every proposal at source → `ProposalRecorded` |
| 2 | EffectExecutor:108 (gate reject) | `EffectExecutor.cs` | Gate rejection → `ProposalOutcomeUpdated(Gated)` |
| 3 | EffectExecutor:196 (PublishTradeClosed) | `EffectExecutor.cs` | Venue fill outcome → `ProposalOutcomeUpdated(Filled)` |
| 4 | KernelBacktestLoop:384 (venue expiry) | `KernelBacktestLoop.cs` | Limit expiry → `ProposalOutcomeUpdated(ExpiredUnfilled)` |

### Discipline

**Create tests first:** Integration test proving a tape run populates ProposalLedger rows. Golden byte-identical verification (recording hooks are additive, outside decision paths).

**Golden protocol is ABSOLUTE:** If ANY golden fixture moves, stop. The hook is inside a decision path. Move it outside — before the kernel call or after, never inside.

### Steps

**Phase A — Data model + persistence (45 min):**

*New files (7):*
1. `src/TradingEngine.Domain/ProposalLedger/ProposalOutcome.cs` — enum: Pending/Accepted/Gated/Filled/ExpiredUnfilled/Rejected
2. `src/TradingEngine.Domain/ProposalLedger/ProposalRecorded.cs` — event record: RunId + OrderProposed + BarOpenTimeUtc
3. `src/TradingEngine.Domain/ProposalLedger/ProposalOutcomeUpdated.cs` — event record: ProposalId + Outcome + Detail + FillPrice/FillTime/GrossProfit/NetProfit/RMultiple/CloseReason/CounterfactualPathJson
4. `src/TradingEngine.Domain/ProposalLedger/IProposalLedgerRepository.cs` — interface: SaveAsync, GetByRunAsync, UpdateOutcomeAsync
5. `src/TradingEngine.Infrastructure/Persistence/Entities/ProposalLedgerEntity.cs` — EF entity (17 fields per handover A2 table: Id PK, RunId, StrategyId, Symbol, EntryTimeframe, Direction, OrderType, EntryMethod, EntryMethodDetail, SignalPriceMid, SignalTimeUtc, LimitPrice, StopLoss, TakeProfit, EntryReason, EntryRegime, GateVerdict, Outcome, FillPrice, FillTimeUtc, GrossProfit, NetProfit, RMutiple, CloseReason, BarsSurvived, CounterfactualPathJson)
6. `src/TradingEngine.Infrastructure/Persistence/Mappings/ProposalLedgerMapping.cs` — EF mapping, indexes on (RunId) and (RunId, StrategyId, Symbol, EntryTimeframe, EntryMethod)
7. `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteProposalLedgerRepository.cs` — upsert implementation

*Edits (2):*
8. `src/TradingEngine.Infrastructure/Persistence/TradingDbContext.cs` — add `DbSet<ProposalLedgerEntity> ProposalLedger` + `ApplyConfiguration(new ProposalLedgerMapping())`
9. Migration: `dotnet ef migrations add M47_ProposalLedger --context TradingDbContext`

*New file (1):*
10. `src/TradingEngine.Infrastructure/Persistence/ProposalLedgerPersistenceHandler.cs` — follow `TradePersistenceHandler` pattern exactly:
    - Subscribe to `ProposalRecorded` + `ProposalOutcomeUpdated` via `IEventBus`
    - `BoundedChannel<(ProposalLedgerEntity Entity, string Action)>(1000, Wait)`
    - Single `DrainAsync` background task
    - `IAsyncDisposable` — channel complete + cancel + await flush

*Unit test:* ProposalLedgerEntity round-trip through in-memory SQLite (follow existing `TradeExcursionEntity` test pattern).

**Phase B — Recording hooks (60 min):**

*B1 — Record proposals at source:*
11. `src/TradingEngine.Host/BarEvaluator.cs:231` — after `proposals.Add(proposal)`: `_ = _eventBus.PublishAsync(new ProposalRecorded(RunId, proposal, simTime), ct)`.
    - Add `IEventBus` to BarEvaluator constructor (available in EngineRunner as `_eventBus`).
12. `src/TradingEngine.Host/EngineRunner.cs` — pass `IEventBus` through to BarEvaluator constructor.

*B2 — Record gate rejections:*
13. `src/TradingEngine.Domain/DecisionRecord.cs:3` — add `Guid? OrderId = null` to the record (backward-compatible default, zero call-site breakage).
14. `src/TradingEngine.Engine/Kernel/Kernel.cs:48` — pass `p.OrderId` through: `new DecisionRecord(..., OrderId: p.OrderId)`.
15. `src/TradingEngine.Host/EffectExecutor.cs:108` — after `_decisionJournal?.Record(record.Decision)`: if `record.Decision.OrderId` is not null, `_ = _eventBus.PublishAsync(new ProposalOutcomeUpdated(oid, ProposalOutcome.Gated, record.Decision.GuardResult), ct)`.

*B3 — Record venue fills:*
16. `src/TradingEngine.Host/EffectExecutor.cs:196` — after `_eventBus.PublishAsync(new TradeClosed(...))`: `_ = _eventBus.PublishAsync(new ProposalOutcomeUpdated(effect.OrderId, ProposalOutcome.Filled, null, tradeResult.EntryPrice?.Value, tradeResult.CloseTimeUtc, tradeResult.GrossProfit, tradeResult.NetProfit, tradeResult.RMultiple, tradeResult.CloseReason), ct)`.

*B4 — Record venue expiries:*
17. `src/TradingEngine.Host/KernelBacktestLoop.cs` — add optional parameter: `Action<ExecutionEvent>? onExecution = null`.
18. `src/TradingEngine.Host/KernelBacktestLoop.cs:384` — call `_onExecution?.Invoke(exec)` inside PumpAsync read loop, before the execution event enters the kernel queue.
19. `src/TradingEngine.Host/EngineRunner.cs:BuildKernelLoop` — wire callback: on Cancelled with "ENTRY_EXPIRED" reason, publish `ProposalOutcomeUpdated(ExpiredUnfilled)`.

*Wire up:*
20. `src/TradingEngine.Host/EngineHostFactory.cs` — subscribe `ProposalLedgerPersistenceHandler` to `ProposalRecorded` + `ProposalOutcomeUpdated` events.

**Tests (15 min):**
- Integration test: Tape run with Market-only strategy → ProposalLedger has Filled entries for all proposals.
- Simulation test: Tape run with LimitOffset strategy → ProposalLedger has correct outcomes (filled + expired + gated).
- Golden: run `git diff --stat -- **/*golden*.json` — must be empty.

### Gate

**Command + expected:**
```
dotnet build TradingEngine.slnx                        # 0 errors
dotnet test tests/TradingEngine.Tests.Unit             # all green
dotnet test tests/TradingEngine.Tests.Integration      # all green, incl. ProposalLedger round-trip
dotnet test tests/TradingEngine.Tests.Simulation --filter "Category!=E2E&Category!=Slow&Category!=NetMQ"  # all green
git diff --stat -- **/*golden*.json                     # empty
sqlite3 src/TradingEngine.Web/data/trading.db "PRAGMA table_info(ProposalLedger)"  # M47 schema present
```

### Traps

- **Golden protocol:** If ANY golden fixture moves, the hook is inside a decision path. The `_ = PublishAsync(...)` fire-and-forget pattern MUST be after the decision, not inside it. Check placement: proposal recording after `proposals.Add()` (correct), gate rejection AFTER `RecordDecisionEvent` dispatch (correct), fill recording AFTER `TradeClosed` publish (correct), expiry recording BEFORE execution enters kernel queue (correct). Any of these in the wrong order = golden movement.
- **DecisionRecord.OrderId:** the `Guid?` default null means existing tests compile unchanged. Add a unit test: `new DecisionRecord(...)` without OrderId → OrderId is null (proves backward compatibility).
- **KernelBacktestLoop.onExecution:** use the same callback pattern as `evaluateTrailing`, `_onBarProcessed`, `_onEvent`. Do not invent a new pattern.
- **Phase A files must exist before Phase B hooks can reference them.** Create all domain + EF types first, then wire hooks. Don't interleave.

### Commit format

`feat(P3.6-AB): proposal ledger data model + recording hooks — 15 files, M47 migration`

Body: file list, migration name, test results, golden verification.

---

## 8. Stage C2 — P3.6 counterfactual paths + API + Angular UI

**Effort:** 120 min · **cTrader:** No · **Risk:** Medium (counterfactual complexity, evaluator math)

### Pre-read (mandatory, in order)

1. P3.6-HANDOVER.md §2 Phase C (C1-C4), Phase D (D1-D3), Phase E (E1-E3)
2. `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs:425-510` — limit expiry + excursion recorder
3. `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:260-292` — ProcessPendingLimits
4. `src/TradingEngine.Domain/Events/ExecutionEvent.cs` — fields structure
5. `src/TradingEngine.Domain/Events/EngineEvent.cs:57` — OrderCancelled record
6. `src/TradingEngine.Host/KernelFeedback.cs:35` — maps ExecutionEvent → EngineEvent
7. `src/TradingEngine.Web/Api/ExitLabController.cs` — pattern for evaluator controller
8. `web-ui/src/app/features/exit-lab/exit-lab.component.ts` — pattern for Angular component

### Background

With the proposal ledger recording every proposal (C1), this stage adds counterfactual paths — tracking what WOULD have happened for expired limits — and exposes the data through an EntryTacticEvaluator + API + Angular UI. The evaluator answers: "for this strategy/cell, which entry method has the best fill-rate × expectancy?" by comparing actual filled R-multiples vs counterfactual R-multiples for missed fills.

The handover document estimates Phase C at ~300 lines in TapeReplayAdapter alone. The evaluator is pure math. The UI follows the ExitLab pattern.

### Steps

**Phase C — Counterfactual paths (45 min):**

1. `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs` — extend private `PendingLimit` class:
   ```csharp
   public decimal SignalPriceMid { get; init; }
   public DateTime SignalTimeUtc { get; init; }
   public decimal PipSize { get; init; }
   public List<ExcursionPoint>? CounterfactualPath { get; set; }
   ```
   Populate from `request.Intent` in `SubmitOrderAsync`.

2. Per-bar recording in `OnBarObserved` (dual-resolution path): for still-pending limits, add `ExcursionPoint(minutes, hiPips, loPips)` to path. Cap at 10,000 points; downsample 2× if exceeded.

3. On expiry (`BarsRemaining <= 0`): serialize path via `ExcursionPathCodec.Serialize()`, attach to `ENTRY_EXPIRED` event:
   ```csharp
   EmitExecutionEvent(new ExecutionEvent(orderId, OrderState.Cancelled, null, 0, "ENTRY_EXPIRED", BrokerTimeUtc)
   {
       Symbol = _symbol,
       CounterfactualPathJson = cfPath
   });
   ```

4. Thread through kernel:
   - `src/TradingEngine.Domain/Events/ExecutionEvent.cs` — add `string? CounterfactualPathJson`
   - `src/TradingEngine.Domain/Events/EngineEvent.cs:57` — add to `OrderCancelled`: `string? CounterfactualPathJson = null`
   - `src/TradingEngine.Host/KernelFeedback.cs:35` — thread: `e.CounterfactualPathJson`

5. `BacktestReplayAdapter`: same pattern but single-resolution (one point per decision bar). Tag fidelity: `"Backtest"` vs tape's `"Tape"`.

**Phase D — API + evaluator (45 min):**

6. `src/TradingEngine.Web/Api/ProposalsController.cs` — `GET /api/proposals?runId=...&strategyId=...&symbol=...&timeframe=...&outcome=...` with pagination (skip/take). Pattern from `TradesController`.

7. `src/TradingEngine.Services/EntryTactic/EntryTacticEvaluator.cs` — static class:
   ```csharp
   public static EntryTacticReport Evaluate(IReadOnlyList<ProposalLedgerEntity> proposals, decimal riskPerTrade = 0.005m);
   ```
   Groups by `EntryMethod` + `EntryMethodDetail` (normalized ATR fraction). Computes per-method:
   - TotalSignals, Gated, Accepted, Filled, ExpiredUnfilled
   - FillRate (Market always 1.0, Limit/Stop = Filled/(Filled+ExpiredUnfilled))
   - AvgR_Filled, AvgR_Counterfactual (from ExitReplayer)
   - WinRate, PassProbability (bootstrap from R-values)

8. `src/TradingEngine.Web/Api/EntryTacticController.cs` — `POST /api/backtest/analytics/entry-tactic`
   - Body: `{ runIds: string[], strategyId?: string, symbol?: string, timeframe?: string }`
   - Returns `EntryTacticReport` with per-method metrics.

9. Unit tests:
   - Fill-rate math: hand-built proposal list → correct fillRate
   - Empty list → empty report (no crash)
   - Counterfactual R: expired limit with known path → correct R from ExitReplayer
   - Multiple entry methods with mixed outcomes → per-method breakdown correct

**Phase E — Angular UI (30 min):**

10. `web-ui/src/app/features/entry-tactic/entry-tactic.component.ts` — standalone, OnPush, inject, signals. Pattern-match from `exit-lab.component.ts`.

11. Layout:
    - Filter bar: strategy/symbol/timeframe dropdowns, run checkboxes (completed runs)
    - "Analyze" button → calls API
    - Results table per entry method: Method | Signals | Gated | Filled | Expired | Fill% | AvgR | CFR | Win% | P(pass)
    - Green/red color coding per column
    - "Apply" button per row → PUT strategy config with chosen method

12. Route: `/entry-tactic` (lazy-loaded). Nav link in `app.component.ts`. DTOs in `api.types.ts`.

13. Driven smoke: `npx tsc --noEmit` (0 errors). Start app. Navigate to `/entry-tactic`. Select a completed run. Click Analyze. Verify table populates with real data.

### Gate

**Command + expected:**
```
dotnet build TradingEngine.slnx                              # 0 errors
dotnet test --filter "FullyQualifiedName~EntryTactic"        # 4+ tests green
curl POST /api/backtest/analytics/entry-tactic -d '{...}'    # returns populated report
npx tsc --noEmit (cwd: web-ui)                               # 0 errors
Driven smoke: /entry-tactic page loads, analyze produces data
```

### Traps

- **BacktestReplayAdapter counterfactuals** are coarse (single-resolution). Tag entity with `Fidelity = "Backtest"`. The UI should show a fidelity chip on these rows.
- **Counterfactual path size:** cap at 10,000 points. The existing `ExcursionTracker` already has this cap + downsampling logic. Reuse it.
- **EntryMethod grouping:** normalize by ATR-fraction where ATR data is available (e.g., `LimitOffset:0.25ATR`). If no ATR, fall back to raw pips (`LimitOffset:2.0pip`). The `EntryMethodDetail` field on the entity stores the normalized group key.
- **The evaluate function is pure math** — group, count, average. It does NOT touch the DB. The controller handles DB queries. This separation makes unit testing trivial.

### Commit format

`feat(P3.6-CDE): counterfactual paths + entry-tactic API + evaluator + Angular UI`

Body: test results, smoke confirmation, file list.

---

## 9. Stage D1 — Final audit + fidelity gaps

**Effort:** 75 min · **cTrader:** No · **Risk:** Low

### Pre-read (mandatory, in order)

1. Original AUDIT.md §F3 (trailing cadence), §F4 (gap-through fills)
2. Original PLAN.md §3 P0.4 (Q4: measure-first → fix M1-cadence only if >1-bar lag)
3. `docs/audit/RECONCILE-FINDINGS.md` — F2 measurement: tape 3660s, cTrader 7200s, gap 3540s (constant 1-bar)
4. `docs/qa-reports/FINAL-AUDIT.md` — pattern for this audit (409 lines, rating taxonomy, bugfix queue)
5. `docs/iterations/iter-parity-pipeline/TRACKER.md` — previous checkpoint table
6. This PLAN.md (§A1 through C2) — verify what was delivered
7. `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` — SL fill logic
8. `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs` — SL fill logic

### Background

Two fidelity gaps from the original AUDIT.md were pre-registered but never addressed. F4 (gap-through fills at optimistic stop price) should be fixed. F3 (trailing cadence) should be documented, not fixed (touches EngineRunner event folding — larger scope). Q4 (M1-cadence cBot fix) is formally deferred per the owner's measure-first decision.

The audit rates every stage against this plan, re-runs the full gate battery, and produces the bugfix queue for the next iteration.

### Steps

**F4 gap-through fills (25 min):**
1. In both `BacktestReplayAdapter` and `TapeReplayAdapter`, find the stop-loss fill logic. Current behavior: if a bar opens beyond SL, fill at the stop price (optimistic).
2. Fix: when bar.Open < SL (for long) or bar.Open > SL (for short), fill at bar.Open (worse, honest price). This matches what real markets do — gaps through stops at the open.
3. Unit test: synthetic bar that opens 20 pips below long SL → assert fill price = bar.Open, not stop price.
4. Re-run Sim-fast to confirm no regressions. This change may shift some TradeResult outcomes (slightly worse fills on gap-through days).

**F3 trailing cadence (15 min):**
- Document in `docs/audit/RECONCILE-FINDINGS.md`: the engine evaluates trailing stops per decision bar; cTrader evaluates per-tick. This means tape trailing exits are systematically later/looser. The venue-parity playbook now measures the impact per run. Fix (per-M1 trailing evaluation in tape venue's dual-resolution loop) is deferred to a dedicated iteration due to EngineRunner event folding complexity.

**Q4 final decision (5 min):**
- Record in `RECONCILE-FINDINGS.md`: M1-cadence cBot fix is DEFERRED. The entry latency is constant (exactly 1 H1 bar = 3540s gap), predictable, and correctable. Per Q4's "measure first" instruction, constant 1-bar lag does not trigger a rebuild.

**Final audit (30 min):**
1. Run full gate battery:
   ```
   dotnet build TradingEngine.slnx
   dotnet test tests/TradingEngine.Tests.Unit
   dotnet test tests/TradingEngine.Tests.Integration
   dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
   git diff --stat -- **/*golden*.json
   npx tsc --noEmit  # cwd: web-ui
   ```
2. Rate each stage (A1, A2, B1, C1, C2, D1) against this PLAN.md using the taxonomy:
   - CONFORMS — matches spec, no gaps
   - CONFORMS-WITH-FINDINGS — minor gaps or cosmetic
   - DEVIATES — material gap vs spec
3. Write `docs/qa-reports/ITER-LAND-FIX-AUDIT.md` following the FINAL-AUDIT.md pattern:
   - Per-stage rating table with evidence cross-reference
   - Shallow implementation scan (any stubs in critical paths?)
   - System-level checks (R1-R10 from original PLAN §10)
   - Bugfix queue: ≤5 items, ordered by severity, each with ID, description, effort, files
4. Update TRACKER.md:
   - Flip all checkpoint rows to DONE (or document which are still open)
   - Overwrite handoff block with final state + next-iteration pointer
5. Update AGENTS.md RESUME block for the next iteration.

### Gate

**Command + expected:**
```
# Full battery green
dotnet build TradingEngine.slnx                                  # 0 errors
dotnet test tests/TradingEngine.Tests.Unit                       # all green
dotnet test tests/TradingEngine.Tests.Integration                # all green
dotnet test tests/TradingEngine.Tests.Simulation --filter "..."  # all green
git diff --stat -- **/*golden*.json                              # empty

# F4 gap-through test passes
# RECONCILE-FINDINGS.md has Q4 final + F3 documentation
# ITER-LAND-FIX-AUDIT.md committed with ≤5 item bugfix queue
# TRACKER.md handoff + AGENTS.md RESUME updated
```

### Traps

- **F4 fix scope:** a conditional on the fill price assignment, not a restructure of the adapter. Check for existing `bar.Open` vs `stopPrice` logic that already handles this case.
- **F3 is documentation, not code.** The agent MUST NOT attempt to fix trailing cadence here. It's a multi-session refactor of EngineRunner event folding.
- **Bugfix queue items** must follow the pattern from FINAL-AUDIT.md: `| # | ID | Sev | Description | Root Cause Guess | Files Touched | Pattern |`
- **If any gate is red at audit time:** fix in-place within this session (the user said "one final audit/QA session with fixing blended in it"). Red gate = fix session merged into audit.

### Commit format

`fix(F4): gap-through fills at bar open on tape/replay venues; audit(D1): iter-land-fix final — stage ratings + bugfix queue`

Body: full gate battery output, per-stage ratings, bugfix queue.

---

## 10. Gate battery (for Conductor plan config)

```json
{
  "gates": [
    {
      "name": "build",
      "command": "dotnet build TradingEngine.slnx -c Debug",
      "tier": "fast",
      "timeoutMinutes": 5
    },
    {
      "name": "unit",
      "command": "dotnet test tests/TradingEngine.Tests.Unit",
      "tier": "fast",
      "timeoutMinutes": 5
    },
    {
      "name": "integration",
      "command": "dotnet test tests/TradingEngine.Tests.Integration",
      "tier": "full",
      "timeoutMinutes": 5
    },
    {
      "name": "sim-fast",
      "command": "dotnet test tests/TradingEngine.Tests.Simulation --filter \"RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ\"",
      "tier": "fast",
      "timeoutMinutes": 5
    },
    {
      "name": "golden",
      "command": "powershell -Command \"$diff = git diff --stat -- **/*golden*.json; if ($diff) { Write-Host $diff; exit 1 } else { Write-Host 'golden clean'; exit 0 }\"",
      "tier": "full",
      "timeoutMinutes": 1
    },
    {
      "name": "web-tsc",
      "command": "npx tsc --noEmit",
      "cwd": "web-ui",
      "tier": "full",
      "timeoutMinutes": 2,
      "optional": true,
      "skipIfMissing": "web-ui/node_modules/.bin/tsc"
    }
  ]
}
```

---

## 11. Stage summary for Conductor config

| # | Stage ID | Title | Sessions | DependsOn | cTrader? |
|---|----------|-------|:--------:|:---------:|:--------:|
| 1 | A1 | Fix F17 — tape/replay zero-trade regression | 1 | — | No |
| 2 | A2 | Fix F18 + rerun P2.2 headline gate | 1 | A1 | Yes |
| 3 | B1 | Pipeline completion + UI gaps + P6 playbook validation | 1 | A2 | No |
| 4 | C1 | P3.6 data model + recording hooks | 1 | A2 | No |
| 5 | C2 | P3.6 counterfactuals + API + Angular UI | 1 | C1 | No |
| 6 | D1 | Final audit + fidelity gaps | 1 | A2,B1,C1,C2 | No |

---

## 12. Inherited deferred items (do not chase in this iteration)

These items from iter-parity-pipeline are pre-registered as known-deferred. Do NOT add to bugfix queue unless they block a gate:

| Item | Source | Status |
|------|--------|--------|
| MISSING_DATA verdict funnel for multi-TF strategies | Original PLAN | Known behavior, not a bug |
| ReferenceScales 84/84 | P1.1 | Done, do not re-check |
| AddOnResolver.Ride Calibrated | iter-quant-model | Not tested end-to-end |
| VenueSessionEntity audit interface | Original PLAN | Never built |
| M15 triage sweep | Original PLAN | Never done |
| Longer triage window for H4 | Original PLAN | Never done |
| P3.6 Entry-Tactic Lab | D97 | **Being built in C1+C2** |
| Live market data provider | D8 | Throws NotSupportedException (design) |
| News filter beyond stub | D9 | Stub returns "no news" (design) |
| Full walk-forward sweep over all cells | — | Just the playbook + equity endpoint in B1 |

---

## 13. Session protocol (same as iter-parity-pipeline PLAN §10)

**Start (5 min):**
1. Read AGENTS.md RESUME block → this PLAN → TRACKER.md.
2. If `conductor/state.json` doesn't match TRACKER handoff, fix it first.
3. Kill all dotnet processes.
4. Run fast gates: `dotnet build` + Sim-fast.

**During:**
- Verify bug exists before fixing (repro + failing test).
- One stage = one commit with gate output in body.
- Heartbeat every 30s for commands >2 min.
- Do not touch kernel reducer semantics (golden protocol).
- Do not commit BuildInfo.g.cs or build-info.ts.

**End (10 min):**
1. Update TRACKER.md handoff block (≤12 lines).
2. Update checkpoint status.
3. Update `.conductor/state.json`.
4. Kill web app if running.
5. Commit + push.

---

## 14. What we are NOT doing this iteration

1. **Rebuilding the cBot loop** (Q4: constant 1-bar lag, accepted).
2. **True concurrency of cTrader runs** (Q2: queue serialization, not parallel).
3. **Per-M1 trailing evaluation** (F3: documented, not fixed).
4. **Walk-forward sweep over all cells** (just the playbook + equity endpoint).
5. **Live-market data provider** (NotSupportedException stub).
6. **Pip-convention verification across all 20 forex symbols** (EURUSD only proven).
7. **AddOnResolver.Ride Calibrated end-to-end** (deferred to next iteration).

---

## 15. Cost estimate

| Stage | Est. time |
|:-----:|:---------:|
| A1 | 60 min |
| A2 | 90 min |
| B1 | 90 min |
| C1 | 120 min |
| C2 | 120 min |
| D1 | 75 min |
| **Total** | **~9.25 hours** |

At observed Conductor rates: ~$1.80–$2.50 for the full plan.
