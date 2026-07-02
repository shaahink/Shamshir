# Progress Metrics

**Updated:** 2026-07-02 (merge review — docs synced from iter/master-plan, stale worktrees removed)
**Branch:** `iter/merge-plan`
**Master plans:** `docs/iterations/iter-merge-plan/PLAN.md` (M1–M5, current) + `docs/iterations/iter-master-plan/PLAN.md` (Tracks A–G, reference) + `docs/QUANT-ROADMAP.md` (Q1–Q4, methodology)

## Active worktrees

| Path | Branch | Role |
|------|--------|------|
| `C:\Code\Shamshir` | `iter/tape-trust` | **PINNED** — do not work here |
| `C:\Code\shamshir-trust` | `iter/merge-plan` | **ACTIVE** — all work happens here |

*(Stale worktrees `shamshir-master` (iter/master-plan) and `shamshir-mdtape` (iter/marketdata-tape) removed 2026-07-02.)*

## Gates (2026-07-02, final)

| Gate | Command | Result |
|------|---------|--------|
| Build | `dotnet build` | 0 errors |
| Unit | `dotnet test tests/TradingEngine.Tests.Unit` | 314 pass / 0 fail / 6 skip |
| Integration | `dotnet test tests/TradingEngine.Tests.Integration` | 105 pass / 0 fail (+14: narrative projection/query, run-delete cascade, market-data delete) |
| Golden/Determinism | `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden\|FullyQualifiedName~Characterization\|FullyQualifiedName~Acceptance\|FullyQualifiedName~Lifecycle\|FullyQualifiedName~Deterministic\|FullyQualifiedName~Equivalence\|FullyQualifiedName~Journal)"` | 63 pass / 0 fail |
| cTrader E2E | `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true&FullyQualifiedName!~NetMQBridge"` | BLOCKED — cTrader CLI not installed on this machine. Owner to run. |

---

## iter-tape-trust — completed (T0–T5)

| Phase | Items | Status |
|-------|-------|--------|
| T0 | B1, B1b, B2, F8, B9 (tape truth) | ✅ |
| T1 | B3, B4, B6, B7, B8, B10 (data pipeline) | ✅ |
| T2 | Reconcile mapper, endpoint, RECONCILE-FINDINGS | ✅ |
| T3 | F1 spread, F4 gap-through, B5 expiry, F2 intrabar equity | ✅ |
| T4 | Compare-both dispatch | ✅ |
| T5 | Sweep runner, content-address skip, journal thinning (SkipJournal) | ✅ |

### All B1–B11 bugs: ✅ fixed
### All F1–F4, F8 fidelity gaps: ✅ fixed
### F5–F7 fidelity gaps: tracked in remaining items below

---

## iter-merge-plan — implemented (M1–M4)

| Item | Status |
|------|--------|
| M1.1: Nav consolidation (6 areas + 2 hub pages) | ✅ |
| M1.2: System info endpoint + Settings UI | ✅ |
| M1.3: DB reset API + UI (scope: runs|config|all with type-the-word confirm) | ✅ |
| M2.1: New-Backtest redesign (two-pane, data coverage, toggle-chips) | ✅ |
| M2.2: Monitor redesign (2x2 grid, narrative polling) | ✅ |
| M2.3: Report tabs (Overview/Trades/Journal/Costs&Risk, 9-col trade table) | ✅ |
| M2.4: Charts (daily PnL histogram, underwater equity) | ✅ |
| M3.1: Narrative service (`GET /api/runs/{id}/narrative`), +13 tests | ✅ |
| M3.2: Monitor switch to narrative (TallyEvent removed) | ✅ |
| M3.3: Trade narrative columns (EF migration, ExitDetailJson stamping) | ⚠️ Partial — see remaining #1 |
| M4.1: Multi-select delete runs + prune keep-last-N | ✅ |
| M4.2: Data Manager per-(symbol,TF) delete + storage totals | ✅ |
| M4.3: SkipJournal verified | ✅ |

### Review pass 2026-07-02 (M2/M3 audit + fixes)

| Fix | Severity | Detail |
|-----|----------|--------|
| RunNarrativeService rewrite | **HIGH** | Rewrote to real PascalCase EventJson schema; +13 tests |
| new-backtest dead link | MED | Missing `RouterLink` import fixed |
| run-report NG0955 | MED | `track v.strategyId` → `track $index` |
| monitor poll leak | LOW | `setInterval` cleared on terminal status |

---

## ALL REMAINING ITEMS — in suggested implementation order

Items are ordered by priority: implement now → implement next → deferred/owner. Skip anything needing cTrader (owner's machine).

### Group 1: DO NOW (ready, no cTrader, non-golden-sensitive)

| # | ID | What | Scope | Master-plan track |
|---|----|------|-------|-------------------|
| **1** | **M3.3 finish** | Populate `EntryReason`/`EntryRegime`/`EntrySnapshotJson` at position open (thread through OrderProposed→PositionState→PublishTradeClosed→EffectExecutor). Surface "why entered / why exited" on trade-detail page + expanded report row. Golden 63/63 stays (executor stamping is outside kernel reducer). | C# pipeline + API DTOs + Angular UI | B3 |
| **2** | **F6 doc** | Document limit+SL same-fine-bar ordering in RECONCILE-FINDINGS.md and TapeReplayAdapter code. `ProcessPendingLimits` runs before `ProcessSlTpHits` per fine bar → limit filled on bar k gets SL-checked on same bar k. Intra-bar ordering unknowable at M1; conservative (SL-before-TP). Minor impact. | `.md` doc only | iter-tape-trust M4 |
| **3** | **F7 doc** | Document fine bars in decision-TF gaps in RECONCILE-FINDINGS.md. Fine bars in missing H1 windows consumed by `_exitIndex++` + warmup skip without exit checks. SL/TP exits in gaps never fire → optimistic bias. Mitigation: noted for future gap-exit-detection pass. | `.md` doc only | iter-tape-trust M4 |

### Group 2: DO NEXT (ready, no cTrader, low risk)

| # | ID | What | Scope | Master-plan track |
|---|----|------|-------|-------------------|
| **4** | **Data coverage badge** | Per-(symbol,TF) m1 overlap badge in Data Manager. Show coverage % + green/yellow/red chip. Feeds M2.1's pre-start data-coverage check. | C# API + Angular | T1 (iter-tape-trust) |
| **5** | **A6 — UX glitches** | Equity curve flicker (diff/throttle updates), cancel-button on active runs, overlap protection (no second run while first draining), progress% accounts for market gaps, DD timeline label corrected. | Angular + C# (cancel) | — |
| **6** | **A4 — Journal completeness** | Verify violations render readable strings (not `[object Object]`), verify commission/swap flow through close path, verify order+fill join in journal view. | C# validation + Angular | — |
| **7** | **D2 — Hardcoded values audit** | Audit timeframe defaults, symbol defaults, balance defaults, magic numbers in UI→engine config path. Fix any that bypass user selection. | C# audit | — |
| **8** | **D1 — DB fragmentation** | Unify `trading.db` to single configurable path. Clean up test artifacts. Document the canonical location. | C# config + EF | — |

### Group 3: LATER (features, no cTrader)

| # | ID | What | Scope | Master-plan track |
|---|----|------|-------|-------------------|
| **9** | **Track F1 — Portfolio entity** | New `Portfolio` config (DB-seeded): strategy rows + risk budget fractions + activation rule. New-Backtest gets "Run portfolio <name>" one-click. Report shows per-row contribution table. Golden untouched (composition via `EffectiveConfigResolver` deep-merge). | C# Domain + EF migration + API + Angular | F1 |
| **10** | **Track G1 — Symbol scorecard** | `GET /api/symbols/scorecard` — for every symbol with data in `marketdata.db`, compute costPerAtrPct (spread÷ATR), m1 coverage%, data range, gap frequency. Sortable table in Data Manager with "nominate" star. | C# API + Angular | G1 |
| **11** | **Q1 — Excursion recorder** | Venue-side per-trade excursion path recorder (per-exit-TF-bar high/low vs entry) for exploration mode. Foundation for exit calibration grid (QUANT-ROADMAP §4). | C# (TapeReplayAdapter + EffectExecutor) | Q1 |
| **12** | **C1 — Venue status bar** | Page/status-bar showing live venue: connected/disconnected, bars received, last tick time, NetMQ port state. Today buried in logs. | Angular + SignalR | — |
| **13** | **D3 — Perf profiling** | Profile tape backtest path end-to-end. Suspects: per-bar indicator recompute, EF change-tracking on bulk inserts, 5s settle delays. Measure bars/sec first. | C# profiling | — |

### Group 4: LATER (features, needs cTrader data)

| # | ID | What | Scope | Master-plan track |
|---|----|------|-------|-------------------|
| **14** | **Track F2 — Correlation evidence** | Pairwise daily-PnL correlation (22:00 roll) from multiple runs. Matrix heatmap in Compare/Portfolio view. Prereq: real multi-strategy runs on downloaded data. | C# API + Angular | F2 |
| **15** | **Track F3 — Regime-gated portfolio** | Elevate regime filter to portfolio level. Activation rule per row (trend rows only in Trending). Validate ON vs OFF with evidence. | C# config + kernel (opt-in, golden-safe) | F3 |
| **16** | **Track G2 — Symbol-fit exploration** | For nominated symbol: run exploration sweep of surviving strategies. Scorecard page links "explore fit" → pre-filled sweep. | C# SweepRunner extension + Angular | G2 |
| **17** | **Q2 — Walk-forward harness** | Window splitter + per-window config freeze + stitched OOS curve. P(pass) as first-class sweep metric (wire `PassProbabilityEstimator`). | C# Orchestrator + Angular | Q2 |
| **18** | **Q3 — Portfolio assembly + per-bar spread** | Correlation-aware exposure groups (config: symbol→group map, per-group open-risk cap in PreTradeGate). Per-bar recorded spread (recorder captures `Symbol.Spread` at bar close). | C# + cBot rebuild | Q3 + A3 |
| **19** | **Q4 — Stress** | Bar-level block bootstrap (synthetic tapes by resampling). News/weekend flattening validation. | C# + sweep | Q4 |

### Group 5: DEFERRED (needs owner sign-off or golden re-baseline)

| # | ID | What | Reason |
|---|----|------|--------|
| **20** | **F5 deferred** | Commission half-at-open (split `ComputeCosts`: half at entry, half at exit). Shifts intra-trade equity that the golden journal captures byte-for-byte. **Golden must be re-baselined** — forbidden without owner sign-off. Net P&L unchanged either way. | Golden re-baseline required |
| **21** | **Coverage fidelity chip** | Show "Fidelity: verified YYYY-MM-DD ✓" chip on Data page + tape run report header. Needs A2 oracle artifacts from owner's cTrader runs. | Owner must run A1 oracle set first |

### Group 6: OWNER ONLY (cTrader CLI + cBot build)

| # | ID | What | Notes |
|---|----|------|-------|
| **22** | **M5.1 / A1** | Oracle set — download EURUSD H1+M1, run 3–5 oracle configs on cTrader, commit `shamshir-report.json` artifacts. Define tolerance contract. | Owner runs on cTrader machine |
| **23** | **M5.2 / A2** | Drift alarm — committed-artifact test (`[RequiresCTrader=false]`) reads oracle artifacts, runs tape vs stored report, asserts within contract. UI fidelity chip. | Infra ready; owner commits artifacts |
| **24** | **M5.3 / A3** | Per-bar recorded spread — cBot captures `Symbol.Spread` at bar close → NDJSON schema extension. Tape venue uses per-bar spread when present. | cBot rebuild + cTrader E2E |
| **25** | **iter-tape-trust V2–V5** | V2 owner's working set download, V3 speed baseline, V4 tape vs cTrader reconcile, V5 engine-DB vs cTrader report | cTrader CLI |
| **26** | **cBot E2E tests** | `RequiresCTrader=true` E2E suite (cBot rebuilt, `.algo` fresh) | cTrader CLI + NetMQ |

---

## Speed baseline (informal)

| Path | Bars | Wall-clock | Bars/sec |
|------|------|-----------|----------|
| Tape replay | 170 (EURUSD M1) | ~531 ms | ~320 |
| cTrader CLI | 170 (EURUSD M1) | ~33 s | ~5 |
