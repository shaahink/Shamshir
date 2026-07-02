# iter-tape-trust → iter-merge-plan — Cold Agent Handover

**Written:** 2026-07-02 07:45 UTC
**From:** the agent that completed iter-tape-trust T0–T5 + merged plan M1/M3
**To:** a FRESH implementation agent (OpenCode / DeepSeek)
**One-line instruction:** `git worktree add C:\code\shamshir-trust origin/iter/tape-trust` — then read this file top-to-bottom.

---

## §0 — You are not starting from scratch

The branch `iter/tape-trust` on `origin` has **16 commits** covering the full iter-tape-trust delivery (T0–T5) plus the first merged-plan items (M1/M3 server-side). The golden gates are all green.

```
Commit history (newest first):
65972a0 M1+M3: merge plan, narrative service, DB reset, system info
6a9d71c gaps: SkipJournal, content-address sweep skip, housekeeping, PROGRESS.md
945e554 docs: finalize iter-tape-trust handover (T0-T5) + D92-D96 decisions
94d52b4 T4+T5: compare mode run-both + sweep runner
581fecf T3: fidelity hardening (F1 spread, F4 gap-through, B5 expiry, F2 intrabar equity)
...
```


## §1 — Worktree setup (do this FIRST)

```powershell
# Fetch and create worktree
git fetch origin
git worktree add C:\code\shamshir-trust origin/iter/tape-trust

# Enter the worktree
cd C:\code\shamshir-trust

# Verify you're on the right branch
git branch --show-current   # should show iter/tape-trust
git log --oneline -5         # top commit = 65972a0

# Build and run gates
dotnet build
dotnet test tests/TradingEngine.Tests.Unit                # 314/0/6
dotnet test tests/TradingEngine.Tests.Integration          # 91/0
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Characterization|FullyQualifiedName~Acceptance|FullyQualifiedName~Lifecycle|FullyQualifiedName~Deterministic|FullyQualifiedName~Equivalence|FullyQualifiedName~Journal)"  # 63/63
```

All three gates must be green before you touch any code. If they're not, stop and investigate — something regressed. The cTrader E2E gate (`RequiresCTrader=true`) is BLOCKED — cTrader CLI is not installed on this machine. The owner runs that gate.


## §2 — What the system is (60-second orientation)

Shamshir is a prop-firm algo-trading engine for cTrader. Deterministic event-sourced kernel (`TradingEngine.Engine`) + risk gate/governor/prop-firm compliance + 9 test strategies + Angular 19 SPA served by `TradingEngine.Web`. Three backtest venues:

| Venue | Speed | Source | Use |
|-------|-------|--------|-----|
| `tape` | ~531ms/170 bars | `marketdata.db` (downloaded history) | Fast experiments, sweep |
| `replay` | Moderate | Per-run bars in `trading.db` | Default credential-free path |
| `ctrader` | ~33s/170 bars | cTrader CLI over NetMQ | **Oracle** — source of fill/economics truth |

**Hard rules (break none):**
- NO kernel/strategy/risk-math changes. Golden 63/63 byte-identical always.
- `decimal` for money/price/lots. `Math.Floor` for lot sizing. Serilog message templates only.
- One commit per phase. Update PROGRESS.md in the same commit.
- Schema changes via EF migrations only. Never delete `marketdata.db` from any reset path.


## §3 — What iter-tape-trust delivered (T0–T5 done, don't redo)

| Phase | What | Key files |
|-------|------|-----------|
| **T0** (tape truth) | B1 `IReplayVenue` interface, B2 memory-served metadata, F8 exit resolution surfaced, B9 `EmitExecutionEvent` error logging | `Domain/Interfaces/IReplayVenue.cs`, `BacktestOrchestrator.cs:929`, `RunQueryService.cs:136-192` |
| **T1** (data pipeline) | B3 cBot cross-product, B4 background download jobs, B7 append-mode shards, B8 chunked ingest | `DownloadJobService.cs`, `TradingEngineCBot.cs`, `SqliteMarketDataStore.cs` |
| **T2** (trust loop) | Reconcile mapper (`LedgerReconcileService`), reconcile endpoint `GET /api/backtest/analytics/reconcile` | `LedgerReconcileService.cs`, `BacktestAnalyticsController.cs` |
| **T3** (fidelity) | F1 spread on fills (entry/exit), F4 gap-through slippage, B5 limit expiry in decision bars, F2 intrabar equity watermark | `BacktestReplayAdapter.cs`, `TapeReplayAdapter.cs` |
| **T4** (compare) | `RunCompareBothAsync` — dispatches tape then cTrader with shared `ComparePairId` | `BacktestOrchestrator.cs:RunCompareBothAsync` |
| **T5** (sweep) | `SweepRunnerService` (SemaphoreSlim 4), matrix expansion, content-address skip, journal thinning (`SkipJournal` in `EngineHostOptions`) | `SweepRunnerService.cs`, `SweepController.cs`, `EngineServiceCollectionExtensions.cs` |

**Decisions recorded:** D84–D96 in `DECISIONS.md`.

**Gap status:**

| # | Fixed? | Notes |
|---|--------|-------|
| B1–B11 | All ✅ | B5 tape side only (replay is single-res, N/A) |
| F1–F4, F8 | ✅ | F3 deferred (measure first) |
| F5 (commission half-at-open) | ❌ | Minor — plan it in M4 |
| F6 (limit+SL same bar) | ❌ | Document — plan it in M4 |
| F7 (fine bars in gaps) | ❌ | Plan it in M4 |


## §4 — The merged plan (what you implement next)

The master plan (`docs/iterations/iter-merge-plan/PLAN.md`) defines 5 phases, prioritized: **UI/UX first, then engine, cTrader last.** You continue from M1/M3 (server-side done).

```
M1 (UI: nav + settings + DB reset)     ← M1.3 API done, M1.1 + M1.2 UI pending
M2 (UI: backtest + monitor + report)    ← all Angular, no APIs needed (B1/C1-C3 APIs ready from M3)
M3 (Engine: narrative + trade detail)   ← M3.1 API done, M3.2 + M3.3 pending
M4 (Engine: housekeeping + gaps)        ← new work
M5 (cTrader: owner verifies)            ← infrastructure ready, DON'T IMPLEMENT
```

### What was already built in M1/M3 (don't rebuild):

| Item | Endpoint/File |
|------|--------------|
| Narrative service | `GET /api/runs/{id}/narrative?afterSeq=&kinds=&severity=` — `RunNarrativeService.cs` |
| DB reset API | `POST /api/system/reset` with `{ scope: "runs"|"config"|"all", confirm: "delete-everything" }` — `SystemController.cs` |
| System info | `GET /api/system/info` — version, branch, db paths, run counts |
| Merge plan doc | `docs/iterations/iter-merge-plan/PLAN.md` — complete 5-phase sequence |


## §5 — Your implementation order (start here)

### Phase 1: M1.1 — Nav consolidation (Angular, ~2 commits)

**Goal:** 6 nav areas replacing 12. No new API needed.

```powershell
# Navigate to Angular project
cd web-ui
```

**What to change in `web-ui/src/app/`:**
1. **Nav component** — reduce to 6 areas: Live · Runs · Strategies · Risk (hub: profiles + FTMO + governor + packs) · Data · Settings
2. **Risk hub page** — one page with sub-tabs mounting EXISTING standalone components (`risk-profiles`, `prop-firm-rules`, `governor`, `add-on-packs`). Mount, don't rewrite.
3. **Runs area** — sub-nav: All runs · Compare · Trades · cTrader sessions
4. Redirect old paths. Use child routes so refresh/back work.

**Gate:** every existing route reachable; `npm run build` succeeds; nav ≤7 top-level items.

### Phase 2: M2.1 — New-Backtest redesign (Angular, ~3 commits)

**Goal:** Two-pane layout with pre-start data-coverage check.

**What to change:**
1. Two-pane layout: LEFT = strategy row builder, RIGHT = sticky summary panel
2. Data coverage check: call `GET /api/data-manager/inventory`, per selected (symbol, TF) show ✓/✗ + m1 overlap. Block start when missing with "download it" link.
3. Protections as compact toggle-chip row with "all on/off" master. Keep `StartRunRequest` unchanged.
4. Field hygiene: grouped sections (Data & venue / Money / Protections), numeric inputs with unit suffixes

**Gate:** owner can drive a tape run without docs; missing-data selection caught pre-start.

### Phase 3: M2.2 + M2.3 — Monitor + Report (Angular, ~4 commits)

**Monitor redesign (M2.2):**
- 2×2 grid: equity+DD chart, risk tiles, live narrative journal (`GET /api/runs/{id}/narrative`), open positions table
- Replace `state.RecentJournal` ring with narrative polling (use the M3.1 API — cursor-paged, poll every 2s)
- Terminal state: "View report" CTA, reconcile badges

**Report tabs (M2.3):**
- Tabs: Overview · Trades · Journal · Costs & Risk (replace 12-section scroll)
- Trades table: DEFAULT 9 columns (Sym/Dir/Entry→Exit/Net/R/Pips/Exit reason/Strategy/Hold); column-chooser
- `<app-journal>` component used by BOTH monitor and report

### Phase 4: M2.4 — Charts (Angular, ~3 commits)

**C1 trade chart:** entry/exit arrows (time-anchored markers above/below candles). SL/TP as step-lines from trail events.

**C2 drawdown:** daily DD bar chart (22:00 UTC roll — NOT calendar date). Underwater area chart.

**C3 equity unification:** same chart for monitor and report. Live append-only via `series.update()`.

### Phase 5: M3.2 + M3.3 — Engine hardening (~3 commits)

**M3.2 (monitor switch):** Delete `TallyEvent` journal branch. `BacktestJournal`/`LogLines` become error/system only.

**M3.3 (trade narrative):** EF migration adding `EntryReason`, `EntryRegime`, `EntrySnapshotJson`, `ExitDetailJson` to `Trades` table. Stamp in effect executor. Golden untouched.

### Phase 6: M4 — Housekeeping + gaps (~3 commits)

1. Multi-select delete runs from list (FK-safe cascade)
2. Coverage view: per (symbol, TF) m1 overlap badge in Data Manager
3. F5: split commission in `ComputeCosts` (half at open, half at close)
4. F6/F7: document gap-through and limit-SL ordering in code/report

### Phase 7: STOP here — M5 is for the owner

Do NOT implement M5. The infrastructure is ready (reconcile endpoint, compare-both dispatch, download jobs). The owner runs cTrader, records oracle artifacts, and commits them.

**What the owner will do:**
- Download EURUSD H1+M1 for their working set
- Run tape vs cTrader same config → reconcile endpoint
- Build oracle set with committed `shamshir-report.json` artifacts
- cBot E2E tests (`RequiresCTrader=true`)


## §6 — Code map (find anything fast)

| What you need | File |
|--------------|------|
| All domain interfaces | `src/TradingEngine.Domain/Interfaces/` |
| Backtest orchestrator (run lifecycle) | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` |
| Run detail (read API) | `src/TradingEngine.Web/Services/RunQueryService.cs` |
| Tape venue (fast in-process) | `src/TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs` |
| Replay venue (per-run bars) | `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` |
| Narrative service | `src/TradingEngine.Web/Services/RunNarrativeService.cs` |
| Narrate API endpoint | `src/TradingEngine.Web/Api/RunsController.cs` (line ~250) |
| DB reset API | `src/TradingEngine.Web/Api/SystemController.cs` |
| Reconcile API | `src/TradingEngine.Web/Api/BacktestAnalyticsController.cs` |
| Sweep runner | `src/TradingEngine.Web/Services/SweepRunnerService.cs` |
| Download jobs | `src/TradingEngine.Web/Services/DownloadJobService.cs` |
| DI registration | `src/TradingEngine.Web/Configuration/ServiceRegistration.cs` |
| Engine host options | `src/TradingEngine.Domain/EngineHostOptions.cs` |
| Inner host DI wiring | `src/TradingEngine.Host/EngineServiceCollectionExtensions.cs` |
| cBot (cTrader adapter) | `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` |
| Journal entity (StepRecords) | `src/TradingEngine.Infrastructure/Persistence/Entities/JournalEntryEntity.cs` |
| Trade entity | `src/TradingEngine.Infrastructure/Persistence/Entities/TradeResultEntity.cs` |
| Run entity | `src/TradingEngine.Infrastructure/Persistence/Entities/BacktestRunEntity.cs` |
| Angular components | `web-ui/src/app/` |
| Merge plan | `docs/iterations/iter-merge-plan/PLAN.md` |
| Decisions | `DECISIONS.md` (D85–D96 are ours) |
| Progress | `docs/audit/PROGRESS.md` |
| cTrader skill | `.claude/skills/shamshir-ctrader/SKILL.md` |
| cTrader E2E skill | `.claude/skills/ctrader-e2e/SKILL.md` |
| Run Shamshir skill | `.claude/skills/run-shamshir/SKILL.md` |


## §7 — Workflow rules (per commit)

1. **Pick ONE phase item** from §5 above
2. Implement it in `web-ui/` (Angular) or `src/` (C#) — never both in one commit
3. **Run gates:**
   ```powershell
   dotnet build
   dotnet test tests/TradingEngine.Tests.Unit          # must stay 314/0/6
   dotnet test tests/TradingEngine.Tests.Integration   # must stay 91/0
   ```
4. For Angular commits: `cd web-ui && npm run build` must succeed
5. Commit with message format: `M1.1: <what you did>`
6. Update `docs/audit/PROGRESS.md` in the same commit — add a line under the phase
7. Never commit `.claude/settings.local.json`, `BuildInfo.g.cs`, `build-info.ts`
8. Golden 63/63 gate after any `src/` change (not Angular)
9. Delete `src/TradingEngine.Web/data/trading.db*` and restart the app if DB schema changes

## §8 — How to drive a tape run (verify your work)

```powershell
# Build Angular + backend
cd web-ui && npm run build; cd ..
dotnet build src/TradingEngine.Web

# Start the app
dotnet run --project src/TradingEngine.Web --launch-profile https

# In another terminal, POST a tape run:
$body = '{"symbols":["EURUSD"],"periods":["H1"],"start":"2025-05-30","end":"2025-06-02","balance":50000,"venue":"tape","strategyIds":["trend-breakout"]}'
$runId = (curl -s -X POST http://localhost:5134/api/runs -H "Content-Type: application/json" -d $body | ConvertFrom-Json).runId
curl -s http://localhost:5134/api/runs/$runId | ConvertFrom-Json | Select-Object runId,status,venue,totalBars,exitResolution

# Check narrative (after run completes):
curl -s "http://localhost:5134/api/runs/$runId/narrative" | ConvertFrom-Json | Select-Object -ExpandProperty events | Format-Table seq,category,headline
```

The tape run should complete with `venue=tape`, `status=completed` (if marketdata.db has bars in range) or `failed` (if no bars — expected behavior). `exitResolution` should show `"M1"` or the fallback string.


## §9 — Quick answers to "why is this like that?"

- **Why `IReplayVenue` exists:** Both adapters had `BarCount` but no common interface. The orchestrator cast to the wrong concrete type (B1). Fixed with a tiny interface in Domain.
- **Why spread was added to fills (F1):** Both replay venues filled at mid-price. Now longs buy at ask, shorts sell at bid. Golden 63/63 survived because kernel-vs-imperative both use the same adapter.
- **Why `ExitResolution` is on RunDetailResponse:** The tape venue silently falls back to single-resolution when M1 bars are missing. Now it's surfaced so the owner KNOWS they got wick-fidelity or not.
- **Why DB reset has ClearAllPools + rename:** Windows locks SQLite files even after the connection closes. The rename-then-recreate dance is the only reliable pattern on Windows.
- **Why narrative is server-side:** One C# headline builder = one vocabulary everywhere. The Angular monitor and report both consume the same narrative stream via cursor-polling.
- **Why the plan merges:** The master plan (`iter/master-plan` branch, `docs/iterations/iter-master-plan/PLAN.md`) defined 7 tracks (A–G). The owner wants UI/UX first, engine next, cTrader last. The merged plan in `docs/iterations/iter-merge-plan/PLAN.md` re-sequences accordingly.
- **Why cTrader items are "owner verifies":** cTrader CLI not installed on this dev machine. Infrastructure is ready (reconcile endpoint, download backend, compare-both orchestration). Owner runs the actual cTrader backtests and commits artifacts.
