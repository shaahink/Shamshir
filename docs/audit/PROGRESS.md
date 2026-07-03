# Progress Metrics

**Updated:** 2026-07-03 (iter/data-mgmt — shards pipeline, tape speed, limit fixes)
**Branch:** `iter/data-mgmt` (active) / `develop` (authoritative, merged)
**Last commit:** `28219ad` (iter/data-mgmt) / `d786d3f` (develop)
**Gates:** Unit 314/0/6 · Integration 94/0 · Golden 63/63 · build 0 · npm 0

## Branch Summary

| Branch | State | Notes |
|--------|-------|-------|
| `develop` | **Merged** `d786d3f` | Contains all iter/tape-trust work + experiments tier1 |
| `iter/data-mgmt` | **Active** `28219ad` | Data management, tape speed, limit fixes, build race fix |
| `iter/tape-trust` | Ancestor `1a7cc93` | Parent of iter/data-mgmt |
| `origin/iter/merge-plan` | Stale (sibling) | All valuable fixes ported to iter/data-mgmt |

## iter/data-mgmt — delivered (2026-07-03)

### Data management
- **Shards pipeline**: `data/shards/` directory, `GET pending-shards`, `POST ingest-shards`
- **Auto-archive**: successfully ingested files move to `data/shards/archive/`
- **Keep files checkbox**: download with `KeepShards=true` preserves NDJSON for inspection
- **Pending files UI**: Data Manager shows unprocessed shards with Ingest All button
- **Market data reset**: Settings page Clear Market Data button (`scope=marketdata`)

### Tape speed control
- **Speed 0-10x**: slider on new-backtest form and live run monitor
- **Pause/resume**: `PATCH /api/runs/{id} { speed: 0 }` with `ManualResetEventSlim`
- **Delay mechanism**: `100ms / speed` per bar in `FeedBarsAsync`

### Limit order fixes
- **P0**: Tape dual-res limit expiry fixed (`decrementExpiry: true`)
- **Full audit**: `docs/audit/LIMIT-ORDER-AUDIT.md` covering all 4 venues

### Server-side validation
- **Tape data check**: `RunsController.Start` validates market data exists before starting tape run
- **Coverage guidance**: new-backtest shows first/last bar dates from inventory for each symbol

### Build
- **Angular race fixed**: correct `BeforeTargets="ResolveStaticWebAssetsConfiguration"` + PS 5.1 path compat
- **Dead config**: removed `ConnectionStrings:Trading` from appsettings.json

### Angular
- **Journal close-fill**: `isCloseFill` detection — close events now visible in journal tab
- **trade-chart-card**: `effect()` replaces `OnChanges` + `OnDestroy` cleanup

### Gaps remaining
- C1 short-spread (2 lines, golden-sensitive)
- D1 DB fragmentation
- D2 Hardcoded defaults audit
- P1 Sell-limit halfSpread alignment
- P3 Limit-order integration tests
- Owner-only: V2–V5, M5, cBot E2E

## Branch Decision (RESOLVED 2026-07-03)

`origin/iter/merge-plan` (21 sibling commits by same author, `1fba208` fork) is a parallel implementation.
**Decision: keep `iter/tape-trust` as authoritative.** The sibling's RunNarrativeService uses invented
camelCase JSON keys — it's broken (same schema bug this branch fixed). Our branch has the correct fix plus
the C2 dd-bar-chart the sibling lacks. Port sibling's valuable additions (M3.3 real data, M4.2 delete,
prune, 15 Angular fixes, docs) manually — do NOT merge the branch.

## 2026-07-03 audit + fix pass (COMMITTED `6931217`)

### Prior audit fixes

| Bug | File | Status |
|-----|------|--------|
| RunNarrativeService invented JSON keys — journal rendered blank/"rejected" since M3.2 | `RunNarrativeService.cs` | ✅ `6931217` |
| VenueSessions orphan rows on run delete (no FK) | `SqliteBacktestRunRepository.cs` | ✅ `6931217` |
| Daily-DD bucketed by calendar date instead of 22:00 UTC roll | `RunQueryService.cs` | ✅ `6931217` |

### Angular + guard fixes (same commit)

| Bug | File | Status |
|-----|------|--------|
| A6 run-overlap protection — 409 on Start+Delete while active | `RunsController.cs` | ✅ `6931217` |
| trade-detail unhandled async crash on API error | `trade-detail.component.ts` | ✅ `6931217` |
| gateRejections null-safety in report bar inspector | `run-report.component.ts` | ✅ `6931217` |
| trade-chart-card stale on gallery navigation (reload-on-switch) | `trade-chart-card.component.ts` | ✅ `6931217` |

## Gates (2026-07-02, pre-audit baseline)

| Gate | Command | Result |
|------|---------|--------|
| Build | `dotnet build` | 0 errors |
| Unit | `dotnet test tests/TradingEngine.Tests.Unit` | 314 pass / 0 fail / 6 skip |
| Integration | `dotnet test tests/TradingEngine.Tests.Integration` | 90 pass / 0 fail (see note below) |
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
| M3.3: Trade narrative columns (EF migration + API + types) | ⚠️ Data is placeholder (EntryRegime=null, EntryReason=OrderEntryMethod), frontend not wired. Port real data from sibling `4933944`. See NEXT-ITERATION.md P0#2 |
| M4.1: Multi-select delete runs (batch POST, FK-safe cascade) | ✅ 2026-07-03 (VenueSessions fix in `6931217`) |
| M4: Coverage view (m1 overlap badges in Data Manager inventory) | ✅ 2026-07-02 |
| M1.2/M1.3: Settings page (system info + 3 DB reset modals) | ✅ 2026-07-02 |
| M3.1: Narrative service (`GET /api/runs/{id}/narrative`) | ✅ 2026-07-03 (schema fix in `6931217`) |

## Remaining gaps (ordered by priority — see NEXT-ITERATION.md §4 for full detail)

| # | Item | Priority | Notes |
|---|------|----------|-------|
| 1 | C1 short-spread — short entries miss half-spread cost | P0 | Golden blocks; needs re-baseline |
| 2 | M3.3 EntryReason/EntryRegime real data (sibling `4933944`) | P0 | Current data is placeholder |
| 3 | M4.2 per-symbol delete (sibling `7a86f0e`) | P1 | 3-layer: interface+SQL+API+UI |
| 4 | Keep-last-N prune (sibling `2a8d40e`) | P1 | `POST /api/runs/prune` |
| 5 | Download symbols 6→12 + M5/M15 TFs (sibling `c6ebdb1`) | P1 | Match new-backtest symbols |
| 6 | Download job robustness (sibling `a1cad43`+`3be027e`) | P1 | Seed-data, cTrader check, polling |
| 7 | Port 11 remaining Angular fixes from sibling `5ef3b67` | P1 | 4 done this session |
| 8 | Docs gap — reference docs, RESOLVED-ISSUES, WORKFLOW | P2 | Copy from sibling |
| 9 | DB fragmentation (D1) — single `TRADING_DB_PATH` | P2 | Config-only |
| 10 | Hardcoded defaults audit (D2) — `EURUSD`/`H1`/`10000` | P2 | Static audit |
| 11 | Angular build race — `RebuildAngularIfStale` | P2 | Workaround: `-p:NgProjectDir=C:/nonexistent-skip` |
| 12 | F5 commission half-at-open split | P4 | Golden re-baseline needed |
| 13 | M5 cTrader trust — V2-V5, oracle set, drift alarm | Owner | cTrader CLI required |
| 14 | cBot E2E tests | Owner | cTrader CLI required |

F6 (gap-through) and F7 (fine-bar gaps) are documented, not yet fully implemented.

## Speed baseline (informal)

| Path | Bars | Wall-clock | Bars/sec |
|------|------|-----------|----------|
| Tape replay | 170 (EURUSD M1) | ~531 ms | ~320 |
| cTrader CLI | 170 (EURUSD M1) | ~33 s | ~5 |
