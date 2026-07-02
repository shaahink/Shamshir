# Progress Metrics

**Updated:** 2026-07-02
**Branch:** `iter/tape-trust`
**Master plan:** `docs/iterations/iter-merge-plan/PLAN.md` (consolidated from `iter/master-plan`)

## Gates (2026-07-02, final)

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
| M1.1: Nav consolidation (6 nav areas) | ✅ 2026-07-02 |
| M1.2/M1.3: Settings page (system info + reset modals) | ✅ 2026-07-02 |
| M2.1: New-Backtest redesign (two-pane, coverage check, toggle chips) | ✅ 2026-07-02 |
| M2.2: Monitor redesign (2x2 grid, narrative API polling, terminal CTA) | ✅ 2026-07-02 |
| M2.3: Report tabs (Overview/Trades/Journal/Costs&Risk, column chooser) | ✅ 2026-07-02 |
| M1.3: DB reset API | ✅ Built |
| M1.2: System info endpoint | ✅ Built |
| M3.1: Narrative service | ✅ Built |

## Merged plan — pending

| Phase | Items | Depends on |
|-------|-------|-----------|
| M2.4 (Charts C1-C3) | Angular: entry/exit markers, SL step-lines, DD bars | Angular |
| M3.2 (Monitor switch to narrative) | Server + Angular: replace RecentJournal ring | M3.1 API done |
| M3.3 (Trade narrative columns) | Migration + executor stamping | M3.1 done |
| M4 (Housekeeping + gaps) | Runs delete, coverage view, F5/F6/F7 | |
| M5 (cTrader trust) | Owner verified — A1-A3 oracle set + drift alarm | cTrader CLI |

## Speed baseline (informal)

| Path | Bars | Wall-clock | Bars/sec |
|------|------|-----------|----------|
| Tape replay | 170 (EURUSD M1) | ~531 ms | ~320 |
| cTrader CLI | 170 (EURUSD M1) | ~33 s | ~5 |
