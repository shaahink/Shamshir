# iter-merge-plan — Clean State Handover

**Updated:** 2026-07-03 (session close — 7 bugs fixed, branch decision made)
**Branch:** `iter/tape-trust` (main worktree, `C:\Code\Shamshir`)
**Last commit:** `6931217` — audit: fix 7 bugs
**Gates:** Unit 314/0/6 · Integration 90/0 · Golden 63/63 · `dotnet build` 0 errors · `npm run build` 0 errors

---

## §0 — Branch Decision: KEEP `iter/tape-trust` (RESOLVED)

Deep research on both branches (same author — shahin Kiassat, both forked from `1fba208`):

| | `iter/tape-trust` (us) | `origin/iter/merge-plan` (sibling) |
|---|---|---|
| **Time** | Evening Jul 2-3 (21:35–01:38) | Daytime Jul 2 (04:43–17:43) |
| **Commits** | 12 on top of `1fba208` | 21 on top of `1fba208` |
| **RunNarrativeService** | FIXED (PascalCase/value-object unwrapping) | BROKEN (invented camelCase keys) |
| **C2 DD bar chart** | Has `dd-bar-chart.component.ts` | Missing |
| **M3.3 data** | Placeholder (null/redundant) | Has real EntryReason/EntryRegime (`4933944`) |
| **M4.2 per-symbol del** | Missing | Has `7a86f0e` |
| **Keep-last-N prune** | Missing | Has `2a8d40e` |
| **Angular fixes** | 4 done (this session) | Has 15 (`5ef3b67`) |
| **Download fixes** | Has `ecdb829` | Has `a1cad43`+`3be027e` |
| **Integration tests** | 90 | 108 (18 more — siblings M3.3/M4.2/deletes) |
| **Reference docs** | Thin | Full set (SYSTEM-REFERENCE, CODE-MAP, etc.) |

**Vote: Keep `iter/tape-trust`.** The sibling's RunNarrativeService is broken (same schema bug the audit found — uses invented camelCase JSON keys that don't match the serializer). Our branch has the correct fix plus the C2 dd-bar-chart feature the sibling lacks. Sibling's 21 commits also overlap heavily with ours (both re-implemented M1-M4 independently), so cherry-picking would be a merge-conflict nightmare.

**Action: Port sibling's missing work onto `iter/tape-trust` manually** — not merge the branch. See §4 for the prioritized list.

---

## §1 — FIXED & COMMITTED (`6931217`)

### 1.1 RunNarrativeService schema bug — CRITICAL, was live-broken (FIXED)
`satisfied``src/TradingEngine.Web/Services/RunNarrativeService.cs:19-147`
Fixed PascalCase/value-object JSON parsing. The old parser read invented camelCase keys against real serialized
event JSON. Every journal line rendered blank/"rejected" since M3.2 deleted the fallback feed.

### 1.2 VenueSessions orphan row on run delete (FIXED)
`satisfied``src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteBacktestRunRepository.cs:49`
Added `db.VenueSessions.Where(v => v.RunId == runId).ExecuteDeleteAsync(ct)` to the cascade.

### 1.3 Daily-DD chart bucketed by calendar date (FIXED)
`satisfied``src/TradingEngine.Web/Services/RunQueryService.cs:16`
22:00 UTC prop-firm roll via `PropFirmDayOf()`, not calendar midnight.

### 1.4 A6 run-overlap protection (FIXED)
`satisfied``src/TradingEngine.Web/Api/RunsController.cs:85-94,172-182`
Start returns 409 Conflict if a run is `starting`/`running`. Delete also refuses active runs.

### 1.5 trade-detail unhandled async crash (FIXED)
`satisfied``web-ui/src/app/features/trades/trade-detail/trade-detail.component.ts:67`
Wrapped `api.getById` in try/catch.

### 1.6 gateRejections null-safety (FIXED)
`satisfied``web-ui/src/app/features/runs/run-report/run-report.component.ts:307`
`(b.gateRejections ?? []).join('; ')` prevents TypeError on null/undefined.

### 1.7 trade-chart-card reload-on-switch (FIXED)
`satisfied``web-ui/src/app/shared/trade-chart-card.component.ts:68,86-90`
Added `ngOnChanges` + extracted `loadTradeData()` so chart reloads when `tradeId` input changes.

---

## §2 — Known Discrepancy

Integration is **90/0**, not the 108/0 the sibling has. The 18-test gap is explained by sibling's additional
features (M3.3 real-data tests, M4.2 delete/storage tests, prune tests). Our branch never got those tests
because it never got those features. **Not a regression** — the missing tests will arrive with the feature ports.

Sibling has 314+108+63 = same core gate counts otherwise.

---

## §3 — Already-verified (no action needed)

Nav consolidation, Settings (system info + reset modals), New-Backtest coverage check, Monitor 2×2 grid with
narrative polling, Report tabs + column chooser, C1 SL/TP step-lines, C2 dd-bar-chart, C3 unified equity,
`SkipJournal`, F6/F7 docs, RunDetail `exitResolution` field, `detrunPlanJson` parsing.

---

## §4 — PRIORITIZED REMAINING WORK

### P0 — Correctness (do these first, golden-sensitive)

| # | Item | Sibling ref | Notes |
|---|------|-------------|-------|
| **1** | **C1 short-spread** — short entries miss half-spread cost in both replay venues. Systematic optimistic bias for shorts. | PROGRESS C1 | Golden 63/63 blocks touching fill prices. Needs re-baseline. |
| **2** | **M3.3 EntryReason/EntryRegime real data** — current data is placeholder (EntryRegime=null, EntryReason=entryMethod). Thread OrderProposed verdict + BarEvaluator regime through to EffectExecutor. | `4933944` | Sibling has working implementation. Engine-adjacent change; port from sibling. |

### P1 — Feature gaps (quick wins, sibling has reference)

| # | Item | Sibling ref | Notes |
|---|------|-------------|-------|
| **3** | **M4.2 per-symbol delete** — Data Manager inventory has no delete action. Add `IMarketDataStore.DeleteBarsAsync` + API + UI. | `7a86f0e` | Port from sibling — 3 layers, each straightforward. |
| **4** | **Keep-last-N prune** — `POST /api/runs/prune` {keep:N}. Delete oldest runs, keep active. | `2a8d40e` | Port from sibling. |
| **5** | **Download symbols 6→12** + M5/M15 TFs in download form. | `c6ebdb1` | Match new-backtest's ALL_SYMBOLS. |
| **6** | **Download job robustness** — seed-data endpoint, cTrader check, status polling improvements. | `a1cad43`+`3be027e` | Our `ecdb829` may partially cover; diff needed. |
| **7** | **Port remaining 11 Angular bug fixes** from sibling's `5ef3b67`. | `5ef3b67` | 4 done this session (trade-detail, gateRejections, chart, overlap). 11 more to verify. |

### P2 — Infrastructure + docs (no golden risk)

| # | Item | Notes |
|---|------|-------|
| **8** | **Docs gap** — sibling has `docs/reference/SYSTEM-REFERENCE.md`, `CODE-MAP.md`, `BACKTEST-ARCHITECTURE.md`, `TEST-ARCHITECTURE.md`, `RESOLVED-ISSUES.md`. Copy from sibling. | Files exist on `origin/iter/merge-plan` — `git checkout origin/iter/merge-plan -- docs/reference/ docs/RESOLVED-ISSUES.md docs/WORKFLOW.md` |
| **9** | **DB fragmentation (D1)** — unify `trading.db` to single configurable path. | Config-only, safe. |
| **10** | **Hardcoded values audit (D2)** — grep for literal `EURUSD`, `H1`, `10000` defaults. | Time-intensive but safe. |
| **11** | **Angular build race fix** — pin `RebuildAngularIfStale` to content-based staleness check. | Workaround: `-p:NgProjectDir=C:/nonexistent-skip` after `npm run build`. Hits every session. |

### P3 — Owner-only (cTrader CLI)

| # | Item | Notes |
|---|------|-------|
| **12** | **V2-V5** — download EURUSD H1+M1, speed baseline, tape vs cTrader reconcile, engine-DB vs cTrader report. | Needs cTrader CLI. |
| **13** | **M5 cTrader trust** — oracle set, drift alarm, per-bar spread. | Blocked on V2-V5. |
| **14** | **cBot E2E** — `RequiresCTrader=true` suite. | Needs cTrader CLI + cBot rebuild. |

### P4 — Next iterations (quant roadmap + tracks)

| # | Item | Notes |
|---|------|-------|
| **15** | Q1 excursion recorder, Q2 walk-forward harness (QUANT-ROADMAP.md) | Research features. |
| **16** | Track F1 portfolio, Track G1 symbol scorecard | Feature work. |

---

## §5 — What NOT to do

- Do NOT group anything "daily" by calendar date — 22:00 UTC prop-firm roll.
- Do NOT touch kernel/strategy/risk math — golden must stay byte-identical (63/63 verified).
- Do NOT implement M5 (owner-only, needs cTrader CLI).
- Do NOT merge `origin/iter/merge-plan` — port its fixes manually.
- Do NOT add comments to code (convention).
