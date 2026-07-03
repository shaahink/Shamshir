# iter-merge-plan — Final Clean State

**Updated:** 2026-07-03 (session close — W1+W2 ported, merged to develop, branches cleaned)
**Branch:** `iter/tape-trust` (active) / `develop` (merged `d786d3f`)
**Last commit:** `1a7cc93` (iter/tape-trust) / `d786d3f` (develop)
**Gates:** Unit 314/0/6 · Integration 109/0 (develop) / 94/0 (iter) · Golden 63/63 · build 0 · npm 0

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

## §4 — REMAINING WORK (post W1+W2 port)

All P0/P1/P2 items from the sibling branch have been ported except **C1 short-spread** (golden-sensitive).
15 Angular fixes from `5ef3b67`: 6 applicable fixed, 9 were N/A (tied to M3.3 narrative features now ported).

### Only Remaining Bug

| # | Item | Severity | Impact |
|---|------|----------|--------|
| **C1** | **Short entries miss half-spread cost** — both `TapeReplayAdapter.cs:262` and `BacktestReplayAdapter.cs:196` fill short entries at mid instead of `mid - halfSpread`. Systematic optimistic bias for every short trade. | P0 CRITICAL | Golden 63/63 blocks touching fill prices. Needs re-baseline. |

**Fix**: change `: midPrice` to `: midPrice - halfSpread` in the short-entry ternary in both adapters. Then regenerate golden snapshot.

### cTrader-dependent (owner only)

| # | Item | Notes |
|---|------|-------|
| V2-V5 | Download EURUSD H1+M1, speed baseline, tape vs cTrader reconcile, engine-DB vs cTrader report | cTrader CLI required |
| M5 | cTrader trust — oracle set, drift alarm, per-bar spread | Needs V2-V5 first |
| cBot E2E | `RequiresCTrader=true` suite | cTrader CLI + cBot rebuild |

### Future (next iterations)

| # | Item | Notes |
|---|------|-------|
| Q1/Q2 | Excursion recorder, walk-forward harness (QUANT-ROADMAP.md) | Quant research features |
| Track F1 | Portfolio entity — named config with strategy rows + risk budgets | |
| Track G1 | Symbol scorecard — costPerAtrPct, m1 coverage%, gap frequency | |
| F5 | Commission half-at-open split | Golden re-baseline needed |

---

## §5 — What NOT to do

- Do NOT group anything "daily" by calendar date — 22:00 UTC prop-firm roll.
- Do NOT touch kernel/strategy/risk math — golden must stay byte-identical (63/63 verified).
- Do NOT implement M5 (owner-only, needs cTrader CLI).
- Do NOT merge `origin/iter/merge-plan` — port its fixes manually.
- Do NOT add comments to code (convention).
