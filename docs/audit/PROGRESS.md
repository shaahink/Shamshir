# Progress Metrics

**Updated:** 2026-07-02
**Branch:** `iter/tape-trust`
**Master plan:** `docs/iterations/iter-merge-plan/PLAN.md` (consolidated from `iter/master-plan`)

## Gates (2026-07-02, final)

| Gate | Command | Result |
|------|---------|--------|
| Build | `dotnet build` | 0 errors |
| Unit | `dotnet test tests/TradingEngine.Tests.Unit` | 314 pass / 0 fail / 6 skip |
| Integration | `dotnet test tests/TradingEngine.Tests.Integration` | 105 pass / 0 fail (+14: narrative projection/query, run-delete cascade, market-data delete) |
| Golden/Determinism | `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&(FullyQualifiedName~Golden\|FullyQualifiedName~Characterization\|FullyQualifiedName~Acceptance\|FullyQualifiedName~Lifecycle\|FullyQualifiedName~Deterministic\|FullyQualifiedName~Equivalence\|FullyQualifiedName~Journal)"` | 63 pass / 0 fail |
| cTrader E2E | `dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader=true&FullyQualifiedName!~NetMQBridge"` | BLOCKED — cTrader CLI not installed on this machine. Owner to run. |

## iter-tape-trust — completed (T0–T5)

| Phase | Items | Status |
|-------|-------|--------|
| T0 | B1, B1b, B2, F8, B9 (tape truth) | ✅ |
| T1 | B3, B4, B6, B7, B8, B10 (data pipeline) | ✅ |
| T2 | Reconcile mapper, endpoint, RECONCILE-FINDINGS | ✅ |
| T3 | F1 spread, F4 gap-through, B5 expiry, F2 intrabar equity | ✅ |
| T4 | Compare-both dispatch | ✅ |
| T5 | Sweep runner, content-address skip, journal thinning (SkipJournal) | ✅ |

## iter-tape-trust — pending (needs cTrader)

| Item | Blocked by |
|------|-----------|
| V2 owner's working set download | cTrader CLI |
| V3 speed baseline | cTrader CLI |
| V4 tape vs cTrader reconcile | cTrader CLI + real runs |
| V5 engine-DB vs cTrader report | cTrader CLI + real runs |
| cBot E2E tests | cTrader CLI (cBot rebuilt, `.algo` fresh) |

## Merged plan — implemented (M1 + M3 partial)

| Item | Status |
|------|--------|
| M1.3: DB reset API (`POST /api/system/reset` with runs/config/all) | ✅ Built, needs UI |
| M1.2: System info endpoint (`GET /api/system/info`) | ✅ Built, needs UI |
| M3.1: Narrative service (`GET /api/runs/{id}/narrative`) | ✅ Built, needs UI |
| Merge plan doc (`docs/iterations/iter-merge-plan/PLAN.md`) | ✅ Written |
| M1.1: Nav consolidation (6 areas + 2 hub pages) | ✅ Built 2026-07-02 |
| M2.1: New-Backtest redesign (two-pane, data coverage, toggle-chips) | ✅ Built 2026-07-02 |
| M2.2: Monitor redesign (2x2 grid, narrative polling) | ✅ Built 2026-07-02 |
| M2.3: Report tabs (Overview/Trades/Journal/Costs & Risk, 9-col trade table) | ✅ Built 2026-07-02 |
| M2.4: Charts (daily PnL histogram, underwater equity) | ✅ Built 2026-07-02 |
| M3.2: Monitor switch to narrative (TallyEvent removed) | ✅ Built 2026-07-02 |
| M3.3: Trade narrative columns (EF migration + ExitDetailJson) | ⚠️ Partial — `ExitDetailJson` stamped; `EntryReason`/`EntryRegime`/`EntrySnapshotJson` columns exist but never populated, and neither is surfaced in the trade UI yet |

## Review pass 2026-07-02 (M2/M3 audit + fixes) — all verified

| Fix | Severity | Detail |
|-----|----------|--------|
| RunNarrativeService rewrite | **HIGH** | M3.1 read invented camelCase fields (`accepted`/`entryPrice`/`lots`) against the real PascalCase EventJson (nested `Symbol.Value`/`Price.Value`, enum "Long"). Every monitor journal line came out empty or "rejected". Rewrote to the real schema (case-insensitive, correct EventKinds incl. `TRAIL`/`BREAKEVEN`/`RIDE`/`DayRolled`, accept/reject via `DecisionReason != "Accepted"`). Also fixed: `kinds` filter was a no-op; `latestSeq` now advances past excluded rows so polling can't stall. +13 tests (projection + query). |
| new-backtest dead link | MED | `routerLink="/data-manager"` with no `RouterLink` import → inert. Fixed. |
| run-report NG0955 | MED | per-bar verdicts `@for … track v.strategyId` is non-unique → duplicate-key crash on Costs & Risk tab. Changed to `track $index`. |
| monitor poll leak | LOW | narrative `setInterval` never stopped after a run finished. Now does a final catch-up fetch then clears on terminal status. |

## Merged plan — implemented (M4, non-cTrader)

| Item | Status |
|------|--------|
| M1.2/M1.3 Settings UI (was "built, needs UI") | ✅ System info + prune + 3 reset actions (type-the-word confirm); replaced stale hardcoded branch label |
| M4.1: Multi-select delete runs (FK-safe cascade) + prune keep-last-N | ✅ Backend (`POST /api/runs/delete`, `/prune` + `IBacktestRunRepository.DeleteRunsAsync`) + run-list UI + Settings prune; +2 cascade tests |
| M4.2: Data Manager per-(symbol,TF) delete + per-symbol storage totals | ✅ Backend (`POST /api/data-manager/delete` + `IMarketDataStore.DeleteBarsAsync`) + UI; +2 tests |
| M4.3: Verify `SkipJournal` skips StepRecord writes | ✅ Verified — binds `NullJournalWriter` (no-op); correct |

## Merged plan — remaining

| # | Phase | Items | Scope |
|---|-------|-------|-------|
| 1 | M3.3 | Populate `EntryReason`/`EntryRegime`/`EntrySnapshotJson` at open + surface "why entered/exited" in trade UI | C# + Angular |
| 2 | M4.3 | **F5 deferred** — commission half-at-open shifts intra-trade equity that the golden journal captures byte-for-byte; needs a golden re-baseline the plan forbids (net P&L unchanged either way). F6/F7 doc-only. Coverage fidelity chip needs A2 oracle (cTrader). | C# (golden-sensitive) |
| — | M5 | cTrader trust: oracle set + drift alarm | **OWNER ONLY** |

## Speed baseline (informal)

| Path | Bars | Wall-clock | Bars/sec |
|------|------|-----------|----------|
| Tape replay | 170 (EURUSD M1) | ~531 ms | ~320 |
| cTrader CLI | 170 (EURUSD M1) | ~33 s | ~5 |
