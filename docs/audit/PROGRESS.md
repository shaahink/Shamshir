# Progress Metrics

**Updated:** 2026-07-03
**Branch:** `iter/tape-trust`
**Master plan:** `docs/iterations/iter-merge-plan/PLAN.md` (consolidated from `iter/master-plan`)
**Next-iteration action plan:** `docs/iterations/iter-merge-plan/NEXT-ITERATION.md` — read before further work,
including a major finding that `origin/iter/merge-plan` is a divergent sibling branch needing reconciliation.

## 2026-07-03 audit + fix pass (uncommitted)

Static audit of HANDOVER.md's claims (2 parallel research passes over Angular + C# backend) found and fixed 3 bugs:

| Bug | File | Status |
|-----|------|--------|
| `RunNarrativeService` read invented camelCase/flat JSON keys against the real PascalCase/nested-value-object `EventJson` — every live journal line rendered blank/"rejected" since M3.2 removed the fallback feed | `RunNarrativeService.cs` | ✅ Fixed (ported verified fix from `origin/iter/merge-plan`, which independently found the same bug) |
| Run delete left orphaned `VenueSessions` rows (no FK, only an index) — M4's own "0 rows in all related tables" gate was never actually true | `SqliteBacktestRunRepository.cs` | ✅ Fixed |
| Daily-DD chart bucketed by calendar date, violating PLAN.md's explicit "22:00 UTC roll, NOT calendar date" rule | `RunQueryService.cs` | ✅ Fixed |

Gates re-verified after fixes: Unit 314/0/6, Integration 90/0 (see discrepancy note below), Golden 63/63, `dotnet
build` 0 errors, `npm run build` 0 errors. Changes are uncommitted — not asked to commit this session.

**Gate discrepancy:** Integration is 90/0, not the 91/0 documented below since 2026-07-02. Isolated to predate this
session's changes (last prior commit only touched Angular). Not investigated further — see NEXT-ITERATION.md §2.

**Major finding:** `origin/iter/merge-plan` (worktree `C:\code\shamshir-trust`, pushed, 21 unique commits) is an
independent redo of this same M1–M5 plan that went through an *additional* audit pass this branch never got — 15
Angular bug fixes, run-overlap protection (A6), fuller M4.2 (per-symbol delete + storage size), and better
reference docs. Full detail and reconciliation options in `NEXT-ITERATION.md` §0 — needs an owner decision.

## Gates (2026-07-02, pre-audit baseline)

| Gate | Command | Result |
|------|---------|--------|
| Build | `dotnet build` | 0 errors |
| Unit | `dotnet test tests/TradingEngine.Tests.Unit` | 314 pass / 0 fail / 6 skip |
| Integration | `dotnet test tests/TradingEngine.Tests.Integration` | 91 pass / 0 fail |
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

## Merged plan — implemented

| Item | Status |
|------|--------|
| M1.1: Nav consolidation (6 nav areas, risk hub, runs sub-nav, redirects) | ✅ 2026-07-02 |
| M1.2/M1.3: Settings page (system info + 3 DB reset modals) | ✅ 2026-07-02 |
| M2.1: New-Backtest redesign (two-pane, coverage check, toggle chips) | ✅ 2026-07-02 |
| M2.2: Monitor redesign (2x2 grid, narrative polling, terminal CTA) | ✅ 2026-07-02 |
| M2.3: Report tabs (Overview/Trades/Journal/Costs&Risk, column chooser) | ✅ 2026-07-02 |
| M2.4: Charts (C1 entry/exit arrows, C2 DD bar chart + underwater, C3 unified equity) | ✅ 2026-07-02 |
| M3.2: Journal cleanup (remove dead RecentJournal from SignalR) | ✅ 2026-07-02 |
| M3.3: Trade narrative columns (EF migration EntryReason/Regime/SnapshotJSon/ExitDetailJson + API + types) | ⚠️ Schema+API done 2026-07-02, but data is placeholder (EntryRegime always null, EntryReason=OrderEntryMethod) — see NEXT-ITERATION.md P0.2. Frontend never wired (deliberately, until data is real) |
| M4: Multi-select delete runs (batch POST, FK-safe cascade) | ⚠️ 2026-07-02, had a VenueSessions orphan-row bug — fixed 2026-07-03 |
| M4: Coverage view (m1 overlap badges in Data Manager inventory) | ✅ 2026-07-02 |
| M1.3: DB reset API (`POST /api/system/reset`) | ✅ Built |
| M1.2: System info endpoint (`GET /api/system/info`) | ✅ Built |
| M3.1: Narrative service (`GET /api/runs/{id}/narrative`) | ⚠️ Built 2026-07-02, was silently broken (schema bug) until fixed 2026-07-03 |

## Merged plan — pending / documented gaps

| Item | Status |
|------|--------|
| Run-overlap protection (A6 on sibling branch) | Missing on `iter/tape-trust` — two runs can be started concurrently. See NEXT-ITERATION.md P0.1 |
| M4.2: per-(symbol,TF) delete range + storage size per symbol | Missing — no delete action in Data Manager inventory table, no size field. See NEXT-ITERATION.md P1 |
| Angular bug parity with `origin/iter/merge-plan`'s 15-bug audit fix (`5ef3b67`) | Not cross-checked against `iter/tape-trust`. See NEXT-ITERATION.md P2 |
| Report trades-table default columns vs PLAN.md spec | Mismatch (Sym/Dir/Lots/Entry/Exit/Gross/Comm/Swap/Net actual vs Sym/Dir/Entry→Exit/Net/R/Pips/Exit-reason/Strategy/Hold spec'd). Minor. See NEXT-ITERATION.md P3 |
| F5: Commission half-at-open split | Documented in TradeCostCalculator; requires venue entry-side tracking |
| F6: Gap-through slippage handling | Documented in TapeReplayAdapter; implemented T3 |
| F7: Fine bars in decision-TF gaps | Documented in TapeReplayAdapter; per-bar spread needed for full fix |
| M5: cTrader trust (oracle set + reconcile) | Owner only — infrastructure ready (reconcile endpoint, compare-both, download jobs) |

## Speed baseline (informal)

| Path | Bars | Wall-clock | Bars/sec |
|------|------|-----------|----------|
| Tape replay | 170 (EURUSD M1) | ~531 ms | ~320 |
| cTrader CLI | 170 (EURUSD M1) | ~33 s | ~5 |
