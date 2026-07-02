# iter-merge-plan — Session Handover for Owner Review

**Written:** 2026-07-02 (session close)
**Branch:** `develop` (all work merged from `iter/merge-plan`)
**Active worktree:** `C:\Code\shamshir-trust` (develop)
**Main worktree:** `C:\Code\Shamshir` (iter/tape-trust — **switch to develop before working**)

---

## What this iteration delivered

### M3.3 — Entry/Exit narrative on trades (commit: `4933944`)
- `EntryReason`, `EntryRegime`, `EntrySnapshotJson` now populated at position open
- Data threads from BarEvaluator → OrderProposed → Kernel → PositionState → PublishTradeClosed → EffectExecutor
- "Why entered" and "Why exited" sections visible on both trade-detail page and report expanded trade row
- Golden 63/63 untouched (executor stamping is outside kernel reducer)

### F6/F7 — Tape-venue edge cases documented (commit: `ee76211`)
- F6: Limit+SL same-fine-bar ordering (conservative SL-before-TP, minor impact)
- F7: Fine bars in decision-TF gaps (optimistic bias, exit gaps never fire)
- All fidelity gaps (F1-F8) now registered in `docs/audit/RECONCILE-FINDINGS.md`

### Data coverage badge (commit: `dfff4d3`)
- M1 overlap ✓/✗ chip per (symbol, TF) in Data Manager inventory
- Spread-pips per symbol computed from ISymbolInfoRegistry
- Live `GET /api/data-manager/inventory` now includes `m1Overlap` and `spreadPips`

### Run overlap protection (commit: `26ff6f3`)
- `POST /api/runs` returns 409 Conflict if another run is "starting" or "running"
- Prevents concurrent runs which could corrupt DB state

### Download job polling (commit: `3be027e`)
- After `POST /api/data-manager/download`, UI polls job status every 1.5s
- Live status: queued → running → recording → ingesting → done/failed
- Failed jobs show red error message — no more silent failures
- Requires cTrader CLI configured (same as backtest path)

### Audit fixes (15 bugs) (commit: `5ef3b67`)
- **trade-detail**: safeParse rejects arrays/primitives, exitReason always visible, unhandled promise caught, snapshot null guards
- **run-report**: TradeChartCardComponent reloads on trade switch (effect), NaN guards on accumulators, null guards on trades().find(), gateRejections null-safe, expandedTradeId @if alias, journalTableData null guard
- **DTOs**: openedAtUtc added to global trade endpoint, dlResult type fixed, StrategySummary gets riskProfileId+orderEntryMethod
- **Null safety**: Duplicate buttons in 3 detail components now guard against null data()
- **Imports**: trade-gallery now imports OnDestroy

### Quick wins (commit: `5928aa9`)
- Journal close fills now show as separate rows (was merged into entry proposals)
- Dead `ConnectionStrings:Trading` config removed from host appsettings.json

### Docs merged (commit: `8a568fd`)
- `docs/iterations/iter-master-plan/PLAN.md` (Tracks A-G) synced from iter/master-plan branch
- Removed stale worktrees (shamshir-master, shamshir-mdtape)
- `docs/audit/PROGRESS.md` consolidated to 18 remaining gaps with reasons+remedies

---

## What's ready for you to test

1. **Data Manager** — go to `/data-manager`, select EURUSD + H1+M1 + 7 days, click Download. Watch status live (blue → green when done, red if failed with error text). Refresh → inventory shows bars. Coverage column has green M1 ✓ chip + spread pips.

2. **New Backtest** — `/runs/new`, select Tape venue + EURUSD H1 + trend-breakout + 30 days. If M1 missing, Start button disabled with "No M1 data (tape)". Coverage check shows coverage rows with M1 status.

3. **Live Monitor** — `/runs/{runId}/monitor` — 2×2 grid, equity chart, risk tiles, live narrative journal (2s polling), counters, progress bar. Cancel button works. Overlap protection blocks starting a second run.

4. **Report** — `/runs/{runId}` — 4 tabs (Overview/Trades/Journal/Costs&Risk). Trades table with column chooser. Click a trade row → expanded shows "Why entered" + "Why exited" + trade chart. EntryReason/EntryRegime populated.

5. **Trade Detail** — `/trades/{id}` — 16 stat tiles, "Why entered" section with reason + regime + snapshot chips, "Exit: <reason>" always visible, strategy label, trade chart.

6. **Settings** — `/settings` — system info, prune keep-last-N, 3 reset scopes with type-the-word confirm.

---

## What's left (18 remaining gaps — see `docs/audit/PROGRESS.md` for full table)

### Tier 1 — Quick wins (~2 hours)
| # | What | Remedy |
|---|------|--------|
| A4 | Verify violations render correctly, commission/swap in close path | Run tape backtest, inspect journal |
| D2 | Hardcoded values audit (symbol defaults, timeframe defaults) | Grep + replace with config |
| D1 | DB path unification (multiple `data/trading.db` paths) | Single env var |

### Tier 2 — Features (~days, no cTrader)
| # | What | Remedy |
|---|------|--------|
| Track F1 | Portfolio entity (named config, one-click run) | New Domain + EF + API + Angular |
| Track G1 | Symbol scorecard (cost per ATR, coverage %) | New API endpoint + Angular table |
| Q1 | Excursion recorder (per-trade excursion paths) | Venue-side bar scanner |
| C1 | Venue status bar/page (live NetMQ state) | Angular + SignalR |

### Tier 3 — cTrader-dependent
| # | What | Remedy |
|---|------|--------|
| Track F2 | Pairwise daily-PnL correlation | Needs multi-strategy runs on real data |
| Track F3 | Regime-gated portfolio | Compare ON vs OFF runs |
| Track G2 | Symbol-fit exploration sweep | Needs T5 sweeps + Q1 data |
| Q2 | Walk-forward harness + P(pass) metric | Orchestrator loop over windows |

### Tier 4 — Deferred (golden-sensitive)
| # | What | Reason |
|---|------|--------|
| F5 | Commission half-at-open | Golden re-baseline required |
| C1 | Short entries miss half-spread cost | Fixed fill price, golden must be re-baselined |

### Tier 5 — Owner-only (cTrader CLI)
| # | What | Notes |
|---|------|-------|
| V2-V5 | Owner data download + reconcile | cTrader CLI |
| M5.1/A1 | Oracle set with committed report artifacts | 3-5 oracle configs |
| M5.2/A2 | Drift alarm test + UI fidelity chip | Reads committed artifacts |
| M5.3/A3 | Per-bar recorded spread | cBot rebuild |

---

## Gate commands

```powershell
dotnet build
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden|FullyQualifiedName~Deterministic)"
```

## Worktree cleanup

```
C:\Code\shamshir-trust  → develop (ACTIVE — where all work happens)
C:\Code\Shamshir        → iter/tape-trust (MAIN WORKTREE — switch to develop: cd C:\Code\Shamshir; git checkout develop; git branch -D iter/tape-trust)
```

## Session commits (12 total)

```
3be027e fix: download job status polling — surface failures live with 1.5s poll
5ef3b67 audit: fix 15 bugs across Angular components + DTOs
5928aa9 A4/D2: journal close-fill visibility + dead config removal
d51a350 docs: session close — group 1&2 done, deep audit findings
c6ebdb1 A1/A2/B1/E3/F1: audit fixes
26ff6f3 A6: run overlap protection
dfff4d3 M4.2: data coverage badge
ee76211 docs: register F5-F7 fidelity gaps
4933944 M3.3: populate EntryReason/EntryRegime/EntrySnapshotJson
8a568fd docs: merge review — sync iter-master-plan/PLAN.md
0d1e756 docs: session close — update AGENTS.md
a1cad43 (reverted) seed-data endpoint
```
