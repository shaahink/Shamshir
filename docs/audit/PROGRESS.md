# Progress Metrics

**Updated:** 2026-07-03 (branch reconciliation + tape-backtest live verification)
**Branch:** `develop` — now the canonical integration branch. See
`docs/iterations/iter-tape-enable/FOLLOWUP-RECONCILE.md` for the full reconciliation record and readiness verdict.
**Worktree used this session:** `C:\code\shamshir-dev`
**Superseded branches (historical, do not build on):** `iter/tape-trust` (11 commits, all superseded — one genuine
fix ported forward, see below), `iter/merge-plan` (fully merged into `develop` already before this session).
**Master plans:** `docs/iterations/iter-merge-plan/PLAN.md` (M1–M5) + `docs/iterations/iter-master-plan/PLAN.md` (Tracks A–G) + `docs/QUANT-ROADMAP.md` (Q1–Q4)

## 2026-07-03 — branch reconciliation + fix (pushed to origin/develop, commit 71133dd)

`develop` was compared file-by-file against the sibling `iter/tape-trust` branch (which independently redid the
same M1–M4 plan). Every difference was audited; `develop` already had equal-or-better versions of everything
(RunNarrativeService schema fix with test coverage, VenueSessions cascade-delete with test coverage, real
EntryReason/EntryRegime threading, working "Why entered" UI, run-overlap 409 guard, full Data Manager
storage/delete-range) **except one genuine gap**: daily PnL/DD was bucketed by calendar date instead of the
22:00 UTC prop-firm roll (`RunQueryService.GetRunDailyPnLAsync` + `BacktestAnalyticsController`'s two endpoints).
Fixed and pushed as `71133dd`. Gates below are post-fix, on `develop`.

## Gates (2026-07-03, develop @ 71133dd)

| Gate | Command | Result |
|------|---------|--------|
| Build | `dotnet build` | 0 errors |
| Unit | `dotnet test tests/TradingEngine.Tests.Unit` | 314 pass / 0 fail / 6 skip |
| Integration | `dotnet test tests/TradingEngine.Tests.Integration` | 110 pass / 0 fail (incl. WebSmokeTests — required a stopgap `wwwroot` copy, see FOLLOWUP-RECONCILE.md npm caveat) |
| Golden/Determinism | `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden\|FullyQualifiedName~Characterization\|FullyQualifiedName~Acceptance\|FullyQualifiedName~Lifecycle\|FullyQualifiedName~Deterministic\|FullyQualifiedName~Equivalence\|FullyQualifiedName~Journal)"` | 63 pass / 0 fail |
| cTrader E2E | `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true&FullyQualifiedName!~NetMQBridge"` | BLOCKED — cTrader CLI not installed. Owner to run. |
| Live tape-backtest (curl, real EURUSD H1 data) | drove a real run end-to-end | ✅ completed, real narrative confirmed readable (see FOLLOWUP-RECONCILE.md) |
| DB Migrations | `dotnet ef database update` | Applied: InitialCreate + AddTradeNarrativeColumns |

## Gates (2026-07-02, pre-reconciliation baseline on former iter/merge-plan)

| Gate | Command | Result |
|------|---------|--------|
| Build | `dotnet build` | 0 errors |
| Unit | `dotnet test tests/TradingEngine.Tests.Unit` | 314 pass / 0 fail / 6 skip |
| Integration | `dotnet test tests/TradingEngine.Tests.Integration` | 108 pass / 0 fail |
| Golden/Determinism | `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden\|FullyQualifiedName~Characterization\|FullyQualifiedName~Acceptance\|FullyQualifiedName~Lifecycle\|FullyQualifiedName~Deterministic\|FullyQualifiedName~Equivalence\|FullyQualifiedName~Journal)"` | 63 pass / 0 fail |
| cTrader E2E | `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true&FullyQualifiedName!~NetMQBridge"` | BLOCKED — cTrader CLI not installed. Owner to run. |
| DB Migrations | `dotnet ef database update` | Applied: InitialCreate + AddTradeNarrativeColumns |

## Completed — iter-tape-trust (T0–T5)

| Phase | Items | Status |
|-------|-------|--------|
| T0 | B1, B1b, B2, F8, B9 (tape truth) | ✅ |
| T1 | B3, B4, B6, B7, B8, B10 (data pipeline) | ✅ |
| T2 | Reconcile mapper, endpoint, RECONCILE-FINDINGS | ✅ |
| T3 | F1 spread, F4 gap-through, B5 expiry, F2 intrabar equity | ✅ |
| T4 | Compare-both dispatch | ✅ |
| T5 | Sweep runner, content-address skip, journal thinning (SkipJournal) | ✅ |

### B1–B11 bugs: ✅ all fixed
### F1–F4, F8 fidelity gaps: ✅ all fixed

---

## Completed — iter-merge-plan (M1–M4, Groups 1–2)

### Previously done (before this session)

| Item | Status |
|------|--------|
| M1.1: Nav consolidation (6 areas + 2 hub pages) | ✅ |
| M1.2: System info endpoint + Settings UI | ✅ |
| M1.3: DB reset API + UI (runs\|config\|all) | ✅ |
| M2.1: New-Backtest redesign (two-pane, data coverage) | ✅ |
| M2.2: Monitor redesign (2x2 grid, narrative polling) | ✅ |
| M2.3: Report tabs (Overview/Trades/Journal/Costs&Risk) | ✅ |
| M2.4: Charts (daily PnL histogram, underwater equity) | ✅ |
| M3.1: Narrative service +13 tests | ✅ |
| M3.2: Monitor→narrative switch (TallyEvent removed) | ✅ |
| M4.1: Multi-select delete runs + prune keep-last-N | ✅ |
| M4.2: Data Manager per-(symbol,TF) delete + storage totals | ✅ |
| M4.3: SkipJournal verified | ✅ |

### This session (2026-07-02)

| Item | Commit | What |
|------|--------|------|
| **M3.3 finish** | `4933944` | EntryReason/EntryRegime/EntrySnapshotJson populated at open (threaded OrderProposed→PositionState→PublishTradeClosed→EffectExecutor). "Why entered"/"Why exited" surfaced on trade-detail + report expanded row. Golden 63/63. |
| **F6 doc** | `ee76211` | F5–F7 registered in RECONCILE-FINDINGS.md; F1–F4,F8 audit trail. |
| **F7 doc** | `ee76211` | (same commit) |
| **Data coverage badge** | `dfff4d3` | M1 overlap ✓/✗ + spread-pips per (symbol,TF) in Data Manager inventory. |
| **Overlap protection** | `26ff6f3` | POST /api/runs returns 409 Conflict if a run is already active. |
| **Audit fixes A1/A2/B1/E3/F1** | `c6ebdb1` | Download symbols 6→12, M5+M15 TFs added, Start disabled when tape+no M1, RunDetail TS gets exitResolution, download banner shows "queued". |
| **Docs merge + worktree cleanup** | `8a568fd` | Copied iter-master-plan/PLAN.md, removed stale worktrees, updated AGENTS.md. |

---

## Deep audit findings (2026-07-02 — tape backtest flow, end-to-end)

Full audit traced the complete user journey: no data → download → new backtest → tape execution → live monitor → report.

### Fixed in this session

| ID | Where | Finding | Remedy |
|----|-------|---------|--------|
| A1 | Data Manager | Download form listed 6 symbols; backtest builder has 12 | Expanded to 12 (match new-backtest ALL_SYMBOLS) |
| A2 | Data Manager | M5, M15 timeframes missing from download form | Added M5, M15 to allTfs |
| B1 | New-Backtest | Start not disabled when tape venue + no M1 data | `tapeM1Gap` computed disables button with tooltip |
| E3 | api.types.ts | TypeScript RunDetail missing ExitResolution field | Added `exitResolution?: string` |
| F1 | Data Manager | Download banner showed "undefined barsRecorded" on fire-and-forget | Changed to "Download queued — refresh to see" |

### Known / documented (not blocking)

| ID | Severity | Finding | Reason not fixing yet | Remedy when ready |
|----|----------|---------|----------------------|-------------------|
| C1 | CRITICAL | Short entries miss half-spread cost in BOTH TapeReplayAdapter and BacktestReplayAdapter. Systematic optimistic bias for short trades. | Part of F1 fidelity gap; requires golden-safe venue-side fix. Golden 63/63 constrains touching fill-price logic. | Fix `SubmitOrderAsync` spread direction: `direction==Long ? mid+half : mid-half`. Verify golden survives. |
| A3 | LOW | Download fire-and-forget — client doesn't poll job status. Banner says "queued; refresh to see" (now fixed to not lie). | Cosmetic; inventory refresh resolves it. | Add polling interval for active jobs in Data Manager. |
| B2 | LOW | StartRunRequest lacks `exitTimeframe` field; backend hardcodes M1. | M1 is the correct default 99% of the time. | Add optional exitTimeframe to UI if owner needs non-M1 exits. |
| C2 | LOW | Tape venue pushes only close-price tick per bar; tick-based strategies unsupported. | No tick-based strategies exist today. | Add per-bar tick synthesis if needed later. |
| D1 | LOW | Monitor gets run detail but discards it. | Harmless; metadata goes unused. | Remove or reuse for status bar info. |
| E2 | LOW | Journal tab shows raw StepRecord; narrative events are a different API. | Both views useful (raw journal for audit, narrative for reading). | Document the difference; both are correct. |

---

## ALL REMAINING GAPS — with reasons + remedies

Items the OWNER can pick up or delegate. Ordered by owner effort (lowest effort first).

### Tier 1: Quick wins (minutes–hours, no cTrader, no golden risk)

| # | ID | What | Reason it's not done | Remedy |
|---|----|------|---------------------|--------|
| **1** | **A4** | Verify violations render readable strings (not `[object Object]`), verify commission/swap flow through close path, verify order+fill join in journal view. | Needs a driven run with trades to observe actual journal output. | Run tape backtest with trend-breakout strategy, inspect journal entries for close/fill events in report. |
| **2** | **D2** | Audit hardcoded defaults: timeframe hardcodes, symbol hardcodes, balance defaults, magic numbers in UI→engine config path. | Time-intensive static audit; low-risk fixes. | Grep for `EURUSD\|H1\|10000` literal defaults in src/ and web-ui/. Replace with config-derived values where safe. |
| **3** | **D1** | DB fragmentation — unify `trading.db` to single configurable path. Multiple paths exist: `src/TradingEngine.Web/data/trading.db`, `data/trading.db`, test artifacts in random folders. | Config-only; harmless unless someone commits a large DB. | Single env-var `TRADING_DB_PATH` defaulting to `data/trading.db` relative to working dir. |

### Tier 2: Features (hours–days, no cTrader, no golden risk)

| # | ID | What | Reason it's not done | Remedy |
|---|----|------|---------------------|--------|
| **4** | **Track F1** | Portfolio entity — named config with strategy rows + risk budgets. New-Backtest "Run portfolio <name>" one-click. Report per-row contribution table. | Feature not scoped in merge-plan M1-M5; defined in iter-master-plan. | New Domain entity + EF migration + API endpoints + Angular UI. Golden-safe (composition via EffectiveConfigResolver). |
| **5** | **Track G1** | Symbol scorecard — for every symbol with data, compute costPerAtrPct, m1 coverage%, gap frequency. Sortable table in Data Manager with "nominate" star. | Feature not scoped in merge-plan. | New API endpoint + Angular table. Reuse ISymbolInfoRegistry + PipCalculator for ATR math. |
| **6** | **Q1** | Excursion recorder — per-trade per-exit-TF-bar excursion path (high/low vs entry). Foundation for exit calibration grid (QUANT-ROADMAP §4). | Research feature, not in merge-plan. | Venue-side: while scanning exit bars, append (barTime, highVsEntry, lowVsEntry) per open position. Persist on close. |
| **7** | **C1** | Venue status bar/page — live venue: connected/disconnected, bars received, last tick time, NetMQ port state. | Out of merge-plan scope. | Angular component + SignalR hub for live status. |

### Tier 3: cTrader-dependent features (owner's machine, hours–days)

| # | ID | What | Reason it's not done | Remedy |
|---|----|------|---------------------|--------|
| **8** | **Track F2** | Correlation evidence — pairwise daily-PnL correlation from multiple runs. Matrix heatmap in Compare/Portfolio view. | Needs real multi-strategy runs on downloaded data. | Run ≥2 strategies on same symbols/window, compute Spearman ρ on daily PnL (22:00 roll). |
| **9** | **Track F3** | Regime-gated portfolio — activation rule per row (trend rows only in Trending). Validate ON vs OFF with evidence. | Needs Tracks F1+F2 first. | Compare tape runs with regime-gating ON vs OFF; report net/DD/P(pass). |
| **10** | **Track G2** | Symbol-fit exploration — for nominated symbol, run exploration sweep of surviving strategies. | Needs T5 sweeps + Q1 excursion data. | Pre-filled sweep from scorecard page → SweepRunnerService. |
| **11** | **Q2** | Walk-forward harness — window splitter + per-window config freeze + stitched OOS curve. P(pass) as first-class sweep metric. | Quant methodology feature. | Orchestrator-level loop over (train,test) windows reusing SweepRunner. |

### Tier 4: Deferred (golden-sensitive or needs owner decision)

| # | ID | What | Reason it's not done | Remedy |
|---|----|------|---------------------|--------|
| **12** | **F5** | Commission half-at-open — split ComputeCosts: half at entry, half at exit. | Shifts intra-trade equity that golden journal captures byte-for-byte. **Golden must be re-baselined.** Net P&L unchanged. | Owner signs off → re-baseline golden snapshot → implement split in ComputeCosts. |
| **13** | **C1 (short spread)** | Short entries miss half-spread cost in TapeReplayAdapter + BacktestReplayAdapter. | Part of F1; fixing it changes golden fill prices. | Fix direction-aware spread in SubmitOrderAsync, re-baseline golden. |

### Tier 5: Owner-only (cTrader CLI + cBot build)

| # | ID | What | Notes |
|---|----|------|-------|
| **14** | **V2–V5** | Owner downloads EURUSD H1+M1, runs speed baseline, runs tape vs cTrader reconcile, runs engine-DB vs cTrader report. | cTrader CLI on owner's machine. |
| **15** | **M5.1 / A1** | Oracle set — run 3–5 oracle configs on cTrader, commit shamshir-report.json artifacts. Define tolerance contract. | Owner runs, commits artifacts. |
| **16** | **M5.2 / A2** | Drift alarm — committed-artifact test (`[RequiresCTrader=false]`) reads oracle artifacts, runs tape vs stored report. UI fidelity chip. | Infra ready; needs owner's artifacts. |
| **17** | **M5.3 / A3** | Per-bar recorded spread — cBot captures Symbol.Spread at bar close → NDJSON schema extension. | cBot rebuild + cTrader E2E. |
| **18** | **cBot E2E** | `RequiresCTrader=true` E2E suite (cBot rebuilt, .algo fresh). | cTrader CLI + NetMQ. |

---

## Session commits (2026-07-02)

```
c6ebdb1 A1/A2/B1/E3/F1: audit fixes
26ff6f3 A6: run overlap protection
dfff4d3 M4.2: data coverage badge
ee76211 docs: register F5-F7 fidelity gaps
4933944 M3.3: populate EntryReason/EntryRegime/EntrySnapshotJson
8a568fd docs: merge review — sync iter-master-plan/PLAN.md
```

## Speed baseline (informal)

| Path | Bars | Wall-clock | Bars/sec |
|------|------|-----------|----------|
| Tape replay | 170 (EURUSD M1) | ~531 ms | ~320 |
| cTrader CLI | 170 (EURUSD M1) | ~33 s | ~5 |
