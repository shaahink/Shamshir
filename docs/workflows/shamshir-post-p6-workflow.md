# Shamshir — Post-P6 Cleanup + Verification Workflow

## Phase State
- P6 COMPLETE — all P6.1-P6.8 delivered (sessions 27-31)
- P6 audit gating (session #32)
- 32 sessions total, $3.14 cost, Unit 714/0/6
- This phase resolves all OWNER-PENDING items, fixes traps, and runs final audit

---

## Universal Pre-Session Ritual (≤5 min)

1. Read this workflow doc (first session only).
2. Read `docs/iterations/iter-parity-pipeline/TRACKER.md` handoff block.
3. Read your session's item below.
4. Run **selective** gate:
   - Engine change → `dotnet build TradingEngine.slnx` + relevant tests
   - UI change → `cd web-ui; npx tsc --noEmit`
   - Config/docs → relevant command only
   - QA/review (no code) → skip gates
   - **Never build on red.**

## Universal Post-Session Ritual (≤15 min)

1. Run selective gate again — confirm nothing regressed.
2. Produce evidence artifact under `docs/iterations/iter-parity-pipeline/evidence/<session>/`.
3. Overwrite TRACKER.md handoff block (≤12 lines, never append).
4. Update checkpoint status in P7 table.
5. Commit (`feat(p7): <item>` or `qa: ...`). Push.

## Discipline Invariants

- `dotnet build TradingEngine.slnx` — 0 errors, 5 pre-existing net6.0 warnings
- `dotnet test tests/TradingEngine.Tests.Unit --no-build` — all pass
- Golden tests: `git diff --stat **/*golden*.json` — empty (no rebaseline without investigation)
- tsc: `cd web-ui; npx tsc --noEmit` — 0 errors when UI touched
- BuildInfo.g.cs and build-info.ts will dirty — leave uncommitted (known)
- One commit per session.

---

## Session Plan (8 sessions)

| # | Item | Effort | cTrader? | Type |
|---|------|--------|:--------:|------|
| 1 | P4.1 live verification — run exploration backtest, verify funnel banner in UI, run backfill endpoint | ~30m | No | QA |
| 2 | PROVE cTrader works — start app, run `"venue":"ctrader"` backtest via HTTP, confirm in DB, write quickstart doc | ~40m | ✅ | research |
| 3 | Trap 3: triage-sweep playbook + Trap 1+2: session labels + SpreadVolNoTradeFilter wiring | ~45m | No | code |
| 4 | Trap 4+5+6: BlockBootstrapper fixes + EntityAuditableTests + P5.1 RunQueryService status dedup | ~40m | No | code |
| 5 | P2.2 headline gate — compare-both with cTrader, commit reconcile verdict | ~60m | ✅ | live run |
| 6 | P3.5 F6-R Option A — emit PublishTradeClosed from reconcile-close path | ~40m | No | code |
| 7 | cTrader test audit — identify RequiresCTrader tests replaceable by tape/quicker methods | ~30m | No | review |
| 8 | Final audit — rate all phases against PLAN.md, check shallow impls, write report + bugfix queue | ~45m | No | review |

---

## Session Details

### Session 1 — P4.1 Live Verification

**Background:** P4.1 delivered the exploration funnel (F11) and MAE/MFE doctrine (F12) but the driven smoke was NEVER RUN. The funnel banner code is structurally correct but unconfirmed against a live app. The backfill endpoint (`POST /api/system/backfill-mae-mfe`) was never called against real DB rows.

**Task:**
1. Build + start web app: `dotnet run --project src/TradingEngine.Web`
2. Create a new backtest via UI with exploration mode (needle button on `/runs/new`)
3. After completion, verify:
   - The run-report page shows purple "Exploration complete" banner with Exit Lab link
   - The Exit Lab link pre-fills the run ID
   - Exit Lab evaluates with actual excursion data (>0 rides)
4. Call `POST /api/system/backfill-mae-mfe` and confirm `updated > 0`
5. Verify MaeR/MfeR values look reasonable for a sample of trades
6. Write finding to `docs/iterations/iter-parity-pipeline/evidence/p7-s1-live-verification.md`

**Evidence:**
- Screenshot of exploration banner on run-report page → `evidence/p7-s1/`
- curl output from backfill endpoint
- SQL query confirming MaeR/MfeR populated

**Gate:** Exploration banner visible · backfill returns `updated > 0` · Exit Lab shows data

---

### Session 2 — Prove cTrader Works

**Background:** Every handover since P0 claims OWNER-PENDING "needs cTrader creds." This was true during P0-P2 (deadlock bugs B1-B3). Those bugs are fixed. The credentials exist in the normal appsettings. This session proves the mechanism once, then documents it so future agents never skip it.

**cTrader credentials (pre-verified):**
- CtId: `seankiaa` (from `src/TradingEngine.Web/appsettings.Development.json` CTrader:CtId)
- Account: `5834367` (from CTrader:Account)
- PwdFile: `C:\Users\shahi\Documents\ctrader.pwd` (from CTrader:PwdFile)
- CLI binary: resolved by `CTraderCliLocator` (finds `%LOCALAPPDATA%\Spotware\cTrader\[hash]\ctrader-cli.exe` automatically)

**Task:**
1. Build the cBot: `dotnet build src/TradingEngine.Adapters.CTrader`
2. Start the web app from `src/TradingEngine.Web`:
   ```powershell
   cd src/TradingEngine.Web
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   dotnet run
   ```
3. Start a cTrader backtest via HTTP:
   ```powershell
   Invoke-RestMethod -Uri "http://localhost:5000/api/runs" -Method Post -Body '{
     "start": "2026-01-15", "end": "2026-01-18",
     "symbols": ["EURUSD"], "periods": ["H1"],
     "balance": 100000, "venue": "ctrader"
   }' -ContentType "application/json"
   ```
4. Poll `GET /api/runs/{runId}` until status = `completed` or `completed-with-warnings`
5. Verify in DB: `sqlite3 Web/data/trading.db "SELECT Status, TotalTrades FROM BacktestRuns WHERE Id = '{runId}'"`
6. Confirm `Status` is NOT `failed` and `TotalTrades > 0`
7. Write `docs/agents/ctrader-quickstart.md` for future agents.

**Debugging if it fails:**
- Check `BacktestRun.ErrorMessage` in the DB
- Check the engine log at `docs/audit/engine-*.log`
- The 30-minute linked CTS timeout and 30-second BarStream completion safety net are in place

**Evidence:**
- `docs/agents/ctrader-quickstart.md` — step-by-step for future agents
- DB query output showing completed status + trades
- curl command + response in evidence file

**Gate:** cTrader backtest reaches `completed` with `TotalTrades > 0` · quickstart doc committed

---

### Session 3 — Traps 3+1+2: Triage-Sweep Playbook + Wiring

**Tasks:**
1. Create `playbooks/triage-sweep.json` — cell sweep over strategies×symbols×TFs → scoreboard snapshot → kill/park/keep report (PLAN §6 P3.4 #3)
2. Wire session labels into TradeExcursions table so fingerprinting data flows through
3. Wire SpreadVolNoTradeFilter into strategy config so it reads from the saved profile

**Files:**
- `playbooks/triage-sweep.json` (new)
- `src/TradingEngine.Core/...` (session label wiring)
- `src/TradingEngine.Strategies/...` (SpreadVolNoTradeFilter config)

**Evidence:** `evidence/p7-s3-traps.md`

---

### Session 4 — Traps 4+5+6 + P5.1 Status Dedup

**Tasks:**
1. Fix BlockBootstrapper writing bars to real MarketDataShard (should use a temp shard)
2. Fix BlockBootstrapController using DateTime.UtcNow (use IEngineClock)
3. Fix EntityAuditableTests red on ExitCalibrationEntity (pre-existing — align test entity with migration)
4. Refactor RunQueryService.GetRunsAsync to use centralized RunStatusResolver (P5.1 debt)

**Files:**
- `src/TradingEngine.Infrastructure/MarketData/BlockBootstrapper.cs`
- `src/TradingEngine.Web/Api/BlockBootstrapController.cs`
- `tests/TradingEngine.Tests.Integration/...` (EntityAuditableTests)
- `src/TradingEngine.Web/Services/RunQueryService.cs`

**Evidence:** `evidence/p7-s4-fixes.md`

---

### Session 5 — P2.2 Headline Gate: Compare-Both Run

**Background:** This is the item every OWNER-PENDING traces back to. One real compare-both run (EURUSD H1, 1 month) proving: equal lots (F1), truthful status (F5), trade persistence barrier (F6). The agent CAN do this with cTrader creds (proven in Session 2).

**Task:**
1. Ensure app is up and cBot built
2. Run a compare-both backtest:
   ```powershell
   Invoke-RestMethod -Uri "http://localhost:5000/api/runs/compare-both" -Method Post `
     -Body '{"configName": "trend-breakout.json"}' -ContentType "application/json"
   ```
3. Poll both run IDs until terminal
4. Run reconcile: `GET /api/backtest/analytics/reconcile?left={tapeId}&right={ctraderId}`
5. Verify via DB: sizing equal, statuses truthful, trades reconciled
6. Commit reconcile verdict to `docs/audit/RECONCILE-FINDINGS.md §P2.2`
7. Update TRACKER.md: flip P2.2 from OWNER-PENDING to DONE

**Evidence:** `docs/audit/RECONCILE-FINDINGS.md §P2.2` with full verdict

**Gate:** sizing equal (F1) · 3× consecutive `completed` (F5) · trade counts reconciled (F6)

---

### Session 6 — F6-R Economics Recovery (Option A)

**Background:** When cTrader crashes mid-run, closes arrive as raw OrderFilled events (no PublishTradeClosed effect). The current barrier detects this as TRADES_UNRECONSTRUCTABLE. Option A fixes the root cause: have the VenueManaged reconcile-close path emit PublishTradeClosed into the journal before teardown.

**Files touched:**
- `src/TradingEngine.Adapters.CTrader/CTraderBrokerAdapter.cs` (reconcile-close path)
- `src/TradingEngine.Core/EffectExecutor.cs` or `TradeResultFactory.cs` (if helper needed)
- `tests/TradingEngine.Tests.Integration/TradePersistenceBarrierTests.cs`

**Risk:** Touches kernel/adapter-adjacent code. The STOP condition in the original tracker is conservative — the actual change is an emit at a specific point in the teardown path. Review the code before cutting.

**Evidence:** `evidence/p7-s6-f6r.md`

---

### Session 7 — cTrader Test Audit

**Background:** The simulation test suite has ~20+ tests with `[Trait("RequiresCTrader", "true")]`. Each takes 1-5 minutes and depends on the cTrader CLI. Some test transport/connection (genuinely need cTrader), others test trading logic that could use the tape backend or FakeTransport.

**Task:**
1. Read `docs/CTRADER-TEST-POLICY.md` — defines which tests keep cTrader dependency
2. Read all files under `tests/TradingEngine.Tests.Simulation/E2E/`
3. For each `[Trait("RequiresCTrader", "true")]` test, classify:
   - **KEEP** — genuinely needs cTrader (transport, NetMQ, cBot round-trip)
   - **REPLACEABLE** — could use tape + FakeTransport instead
   - **MERGE INTO** — covered by another test
4. For replaceable ones: estimate effort to convert, files touched
5. Write report: `docs/audit/ctrader-test-audit.md`

**Evidence:** `docs/audit/ctrader-test-audit.md` with full classification table

**Gate:** Report committed. Replaceable tests identified with effort estimate.

---

### Session 8 — Final Audit: Rate All Phases Against PLAN.md

**Rating system (same as Loom):**
| Rating | Meaning |
|--------|---------|
| ✅ CONFORMS | Matches PLAN.md design spec. No gaps. |
| ⚠️ CONFORMS-WITH-FINDINGS | Minor gaps or cosmetic issues. |
| ❌ DEVIATES | Material gap vs PLAN.md spec. |

Stage verdict: all ✅/⚠️ → PASS · 1 ❌ → PASS-WITH-FINDINGS · ≥2 ❌ → FAIL

**Procedure:**
1. Read PLAN.md sections for each phase P0-P6 + the verification matrix (PLAN §11)
2. For each phase, check delivered code + handover + evidence against what PLAN.md promised
3. Check for shallow implementations — did the agent structure the code to pass the gate without fully implementing the design?
4. For each finding: describe the gap, estimate fix effort, list files
5. Write `docs/qa-reports/FINAL-AUDIT.md`

**System-level checks:**
- R1: Evidence or it didn't happen — do evidence artifacts exist for every checkpoint?
- R3: Fixing blind — are there UNVERIFIED labels on any fix commit?
- R6: UI shipped without driving — are there driven smoke screenshots for every UI change?
- R10: Done labels ahead of gates — does any DONE checkpoint lack a gate output?

**Output:** `docs/qa-reports/FINAL-AUDIT.md` + bugfix queue

**Gate:** Report committed. All checkpoints rated. Bugfix queue written.

---

## Rating Taxonomy Reference

| Rating | Meaning | Next Action |
|--------|---------|-------------|
| ✅ CONFORMS | Matches PLAN.md. No gaps. | None. |
| ⚠️ CONFORMS-WITH-FINDINGS | Minor doc/cosmetic gaps. | Document findings. No code change. |
| ❌ DEVIATES | Material gap vs PLAN.md. | Report: exact section, what delivered, fix estimate. |

| Stage Verdict | Condition |
|---------------|-----------|
| ✅ PASS | All checkpoints CONFORMS or CONFORMS-WITH-FINDINGS |
| ⚠️ PASS-WITH-FINDINGS | At most 1 DEVIATES with clear fix path |
| ❌ FAIL | ≥2 DEVIATES or core functionality broken |
